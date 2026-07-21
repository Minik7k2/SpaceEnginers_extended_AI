// Testy silnika relacji (Etap 3) bez gry: reguły zmian, maszyna stanów z histerezą,
// dryf w ticku, bonus "atak na wroga frakcji", cooldown radia i raport /zf rel.
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>

#include "db.hpp"
#include "engine.hpp"
#include "fallback.hpp"

namespace fs = std::filesystem;

namespace {

zf::Event make_event(const std::string& type, const nlohmann::json& data) {
    zf::Event ev;
    ev.type = type;
    ev.data = data;
    return ev;
}

constexpr std::int64_t kMinuteMs = 60000;

} // namespace

int main() {
    const fs::path tmp = fs::temp_directory_path() / "zf_engine_test";
    std::error_code ec;
    fs::remove_all(tmp, ec);
    fs::create_directories(tmp);

    // Hermetyczny fallback.toml — testy nie zależą od brain/personas.
    const fs::path fallback_path = tmp / "fallback.toml";
    {
        std::ofstream out(fallback_path, std::ios::binary);
        out << "[KRW]\n"
               "grozba = \"Masz {sekundy} sekund.\"\n"
               "kpina = \"Slabo strzelasz.\"\n"
               "neutral = \"Szum.\"\n"
               "[HEL]\n"
               "neutral = \"Helion potwierdza.\"\n"
               "[WGR]\n"
               "zal = \"Nie spodziewalismy sie.\"\n"
               "neutral = \"Czego trzeba?\"\n";
    }

    zf::Config cfg; // defaulty == wartości z rules.toml
    zf::Db db(":memory:");
    zf::Fallback fallback(fallback_path.string());

    assert(fallback.has("KRW", "grozba"));
    assert(fallback.render("KRW", "grozba", {{"sekundy", "30"}}) == "Masz 30 sekund.");

    zf::Engine engine(db, fallback, /*rng_seed=*/42);

    // Frakcje z CLAUDE.md istnieją od startu.
    assert(db.list_factions().size() == 3);
    assert(zf::faction_color("KRW") == "red");
    assert(zf::faction_color("HEL") == "blue");
    assert(zf::faction_color("WGR") == "yellow");

    std::int64_t now = 1000000;

    // Lekki ostrzał: delta bliska ostrzal_min (-5).
    auto out = engine.on_event(
        make_event("combat_hit", {{"faction", "KRW"}, {"damage", 10.0}, {"hits", 1}, {"weapon", "test"}}),
        cfg, now);
    {
        const double v = db.get_relation("KRW", "PLAYER").value;
        assert(v < -4.9 && v > -5.5 && "lekki ostrzał ma dawać ~ostrzal_min");
        assert(out.size() == 1 && out[0].faction == "KRW" && out[0].priority == 1);
        assert(out[0].text == "Masz 60 sekund." && "combat ma brać szablon grozba");
    }

    // Drugi ostrzał chwilę później: relacja dalej spada, ale radio milczy (cooldown 15 s).
    now += 1000;
    out = engine.on_event(
        make_event("combat_hit", {{"faction", "KRW"}, {"damage", 5000.0}, {"hits", 50}, {"weapon", "test"}}),
        cfg, now);
    {
        const double v = db.get_relation("KRW", "PLAYER").value;
        assert(v < -19.0 && "ciężki ostrzał (>=2000 dmg) ma dawać ostrzal_max (-15)");
        assert(out.empty() && "cooldown radia ma wyciszyć drugą groźbę");
    }

    // Zniszczenie statku: -30 i stan wojna (relacja <= -60), ciężka pamięć.
    now += 20000;
    out = engine.on_event(
        make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "Testowy"}, {"by_player", true}}),
        cfg, now);
    {
        const double v = db.get_relation("KRW", "PLAYER").value;
        assert(v <= -50.0 && "po zniszczeniu statku relacja mocno w dół");
        // -5..-15 -15 -30 => około -50..-60; dobijmy do wojny drugim zniszczeniem
    }
    now += 20000;
    engine.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "Testowy 2"}, {"by_player", true}}),
                    cfg, now);
    {
        bool krw_at_war = false;
        for (const zf::FactionRow& row : db.list_factions()) {
            if (row.tag == "KRW") {
                krw_at_war = row.state == "wojna";
            }
        }
        assert(krw_at_war && "relacja <= prog_wojna ma przełączać stan na wojnę");
    }

    // Histereza: powrót do -55 NIE kończy wojny (wyjście dopiero > -50).
    {
        const double v = db.get_relation("KRW", "PLAYER").value;
        db.adjust_relation("KRW", "PLAYER", -55.0 - v); // ustaw dokładnie -55
    }
    now += kMinuteMs;
    engine.tick(cfg, now, /*force=*/true);
    for (const zf::FactionRow& row : db.list_factions()) {
        if (row.tag == "KRW") {
            assert(row.state == "wojna" && "histereza: -55 to wciąż wojna");
        }
    }

    // Powyżej histerezy (-45): wojna się kończy, jest napięcie (-45 <= prog_wrogi).
    db.adjust_relation("KRW", "PLAYER", 10.0); // -55 -> -45
    now += kMinuteMs;
    engine.tick(cfg, now, /*force=*/true);
    for (const zf::FactionRow& row : db.list_factions()) {
        if (row.tag == "KRW") {
            assert(row.state == "napiecie" && "powyżej histerezy wojna przechodzi w napięcie");
        }
    }

    // Atak na wroga frakcji: KRW (wróg SPRT) dostaje +5 do gracza za ostrzał SPRT.
    db.ensure_faction("SPRT", "Piraci");
    db.adjust_relation("KRW", "SPRT", -40.0);
    const double krw_before = db.get_relation("KRW", "PLAYER").value;
    now += 20000;
    engine.on_event(
        make_event("combat_hit", {{"faction", "SPRT"}, {"damage", 100.0}, {"hits", 5}, {"weapon", "test"}}),
        cfg, now);
    {
        const double krw_after = db.get_relation("KRW", "PLAYER").value;
        assert(krw_after > krw_before + 4.9 && "wróg ostrzelanej frakcji ma dostać bonus");
    }

    // Dryf: -45 wraca w stronę 0 o dryf_pkt na dryf_co_minut.
    {
        const double before = db.get_relation("KRW", "PLAYER").value;
        now += static_cast<std::int64_t>(cfg.dryf_co_minut) * kMinuteMs * 2;
        engine.tick(cfg, now, /*force=*/true);
        const double after = db.get_relation("KRW", "PLAYER").value;
        assert(after > before && after <= before + cfg.dryf_pkt * 2 + 0.001 && "dryf ma podnosić ujemną relację");
    }

    // Sufit świata mściwego: cap ogranicza wzrost.
    db.lower_relation_cap("WGR", "PLAYER", 20.0);
    db.adjust_relation("WGR", "PLAYER", 90.0);
    assert(db.get_relation("WGR", "PLAYER").value == 20.0 && "cap ma przycinać od góry");

    // /zf rel: raport zawiera frakcje i sufit.
    const std::string report = engine.relations_report();
    assert(report.find("KRW") != std::string::npos);
    assert(report.find("HEL") != std::string::npos);
    assert(report.find("sufit") != std::string::npos);

    // debug_command rel → wiadomość SYSTEM.
    out = engine.on_event(make_event("debug_command", {{"cmd", "rel"}}), cfg, now);
    assert(out.size() == 1 && out[0].faction == "SYSTEM" && !out[0].text.empty());

    // Zwykły tick bez force ma się nie odpalić przed upływem tick_co_minut.
    assert(engine.tick(cfg, now + 1000).empty());

    // Wiadomość @HEL → odpowiedź szablonem neutral.
    now += kMinuteMs * 5;
    out = engine.on_event(
        make_event("chat_message", {{"text", "@HEL czesc"}, {"target", "HEL"}}), cfg, now);
    assert(out.size() == 1 && out[0].faction == "HEL" && out[0].text == "Helion potwierdza.");

    // --- Spawny (Etap 5) --- świeży silnik, żeby nie zależeć od stanu relacji powyżej.
    {
        zf::Db sdb(":memory:");
        zf::Fallback sfb(fallback_path.string());
        zf::Engine se(sdb, sfb, /*rng_seed=*/7);
        zf::Config scfg; // spawn_wlaczone=true, cooldown 5 min
        std::int64_t t = 5000000;

        // Wojna KRW (dwa zniszczenia) => raid w buforze; take_spawns czyści bufor.
        se.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "A"}, {"by_player", true}}), scfg, t);
        t += 1000;
        se.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "B"}, {"by_player", true}}), scfg, t);
        bool raid = false;
        for (const zf::SpawnOut& s : se.take_spawns()) {
            if (s.faction == "KRW" && s.kind == "raid") {
                raid = true;
            }
        }
        assert(raid && "wypowiedzenie wojny ma zlecać raid");
        assert(se.take_spawns().empty() && "take_spawns ma czyścić bufor");

        // /zf raid (force): spawnuje mimo wyłączonych auto-spawnów, plus komunikat SYSTEM.
        scfg.spawn_wlaczone = false;
        auto rout = se.on_event(make_event("debug_command", {{"cmd", "spawn"}, {"faction", "HEL"}}), scfg, t + 2000);
        auto rsp = se.take_spawns();
        assert(rsp.size() == 1 && rsp[0].faction == "HEL" && "force ma spawnować mimo spawn_wlaczone=false");
        assert(rout.size() == 1 && rout[0].faction == "SYSTEM");

        // Nieznana frakcja: brak spawnu, komunikat SYSTEM.
        auto uout = se.on_event(make_event("debug_command", {{"cmd", "spawn"}, {"faction", "XXX"}}), scfg, t + 3000);
        assert(se.take_spawns().empty() && "nieznana frakcja nie spawnuje");
        assert(uout.size() == 1 && uout[0].faction == "SYSTEM");

        // spawn_wlaczone=false tłumi auto-spawn maszyny stanów (WGR do napięcia = -30).
        se.on_event(make_event("grid_destroyed", {{"faction", "WGR"}, {"grid", "C"}, {"by_player", true}}), scfg, t + 4000);
        assert(se.take_spawns().empty() && "spawn_wlaczone=false ma tłumić auto-patrol");
    }

    std::cout << "zf_engine_test: OK\n";
    return 0;
}
