// Testy silnika relacji (Etap 3) bez gry: reguły zmian, maszyna stanów z histerezą,
// dryf w ticku, bonus "atak na wroga frakcji", cooldown radia i raport /zf rel.
#include <algorithm>
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

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

    // Cooldown bonusu: mod zgłasza combat_hit co 3 s, więc kolejne trafienia w TEJ SAMEJ
    // potyczce nie mogą dosypywać relacji (regresja: minuta ostrzału = +100 u wszystkich).
    {
        const double before = db.get_relation("KRW", "PLAYER").value;
        now += 3000;
        engine.on_event(
            make_event("combat_hit", {{"faction", "SPRT"}, {"damage", 100.0}, {"hits", 5}, {"weapon", "test"}}),
            cfg, now);
        const double after = db.get_relation("KRW", "PLAYER").value;
        assert(std::abs(after - before) < 0.001 && "bonus w oknie cooldownu ma się NIE powtórzyć");

        // Po cooldownie to już inna potyczka — bonus wraca.
        now += static_cast<std::int64_t>(cfg.atak_na_wroga_cooldown_min) * kMinuteMs + 1000;
        engine.on_event(
            make_event("combat_hit", {{"faction", "SPRT"}, {"damage", 100.0}, {"hits", 5}, {"weapon", "test"}}),
            cfg, now);
        assert(db.get_relation("KRW", "PLAYER").value > after + 4.9 &&
               "po cooldownie bonus ma znów przysługiwać");
    }

    // Próg okupu kredytowego rośnie z wrogością: przy relacji -80 jest 1,8x bazy.
    {
        zf::Db db2(":memory:");
        zf::Engine e2(db2, fallback, /*rng_seed=*/1);
        db2.ensure_faction("KRW", "Krwawa Ręka");
        const std::int64_t baza = e2.cash_ransom_threshold("KRW", cfg);
        assert(baza == cfg.deeskalacja_prog_kredyty && "przy relacji 0 próg == baza z configu");
        db2.adjust_relation("KRW", "PLAYER", -80.0);
        const std::int64_t drogo = e2.cash_ransom_threshold("KRW", cfg);
        assert(drogo > baza && "im gorsza relacja, tym droższy pokój");
        zf::Config wylaczone = cfg;
        wylaczone.deeskalacja_prog_kredyty = 0;
        assert(e2.cash_ransom_threshold("KRW", wylaczone) == 0 && "0 w configu wyłącza bramkę");
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

    // --- Okup w surowcach (B+): żądanie trybutu i dostawa ---
    {
        zf::Db rdb(":memory:");
        zf::Fallback rfb(fallback_path.string());
        zf::Engine re(rdb, rfb, /*rng_seed=*/99);
        zf::Config rcfg; // defaulty == [okup_surowce] z rules.toml
        std::int64_t t = 9000000;

        // Wojna KRW (dwa zniszczenia) => aktywny rajd (warunek żądania trybutu).
        re.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "A"}, {"by_player", true}}), rcfg, t);
        t += 1000;
        re.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "B"}, {"by_player", true}}), rcfg, t);
        re.take_spawns(); // wyczyść bufor spawnów

        // /zf okup-surowce => jedno żądanie trybutu dobrane z configu.
        t += 1000;
        re.on_event(make_event("debug_command", {{"cmd", "okup-surowce"}, {"faction", "KRW"}}), rcfg, t);
        auto demands = re.take_ransom_demands();
        assert(demands.size() == 1 && demands[0].faction == "KRW" && "okup-surowce ma wystawić żądanie trybutu");
        assert(demands[0].deadline_s == rcfg.okup_deadline_s);
        assert(demands[0].amount >= rcfg.okup_ilosc_min && demands[0].amount <= rcfg.okup_ilosc_max &&
               demands[0].amount % 50 == 0 && "ilość trybutu z zakresu, zaokrąglona do 50");
        const std::vector<std::string> allowed = {"Iron", "Nickel", "Silicon", "Cobalt"};
        assert(std::find(allowed.begin(), allowed.end(), demands[0].item) != allowed.end() &&
               "surowiec z listy configu");

        // Drugie żądanie przy wiszącym okupie => brak dublowania skrzynki.
        t += 1000;
        re.on_event(make_event("debug_command", {{"cmd", "okup-surowce"}, {"faction", "KRW"}}), rcfg, t);
        assert(re.take_ransom_demands().empty() && "wiszący okup nie może się dublować");

        // Dostawa trybutu => relacja rośnie o bonus, wiarygodność czysta.
        const double before_paid = rdb.get_relation("KRW", "PLAYER").value;
        t += 1000;
        re.on_event(make_event("ransom_paid",
                               {{"faction", "KRW"}, {"item", demands[0].item}, {"amount", demands[0].amount}}),
                    rcfg, t);
        const double after_paid = rdb.get_relation("KRW", "PLAYER").value;
        assert(after_paid > before_paid + rcfg.okup_bonus_dostawa - 0.001 && "dostawa ma podnieść relację o bonus");
        assert(rdb.ransom_broken("KRW") == 0 && "czysta dostawa nie psuje wiarygodności");
    }

    // --- Okup w surowcach: złamana obietnica (trwała nieufność) + odkup czynem ---
    {
        zf::Db rdb(":memory:");
        zf::Fallback rfb(fallback_path.string());
        zf::Engine re(rdb, rfb, /*rng_seed=*/123);
        zf::Config rcfg;
        std::int64_t t = 12000000;

        re.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "A"}, {"by_player", true}}), rcfg, t);
        t += 1000;
        re.on_event(make_event("grid_destroyed", {{"faction", "KRW"}, {"grid", "B"}, {"by_player", true}}), rcfg, t);
        re.take_spawns();

        t += 1000;
        re.on_event(make_event("debug_command", {{"cmd", "okup-surowce"}, {"faction", "KRW"}}), rcfg, t);
        assert(re.take_ransom_demands().size() == 1);

        // Deadline minął bez dostawy => trwała nieufność, drobna kara relacji, ataki trwają.
        const double before_exp = rdb.get_relation("KRW", "PLAYER").value;
        t += 1000;
        re.on_event(make_event("ransom_expired", {{"faction", "KRW"}}), rcfg, t);
        assert(rdb.ransom_broken("KRW") == 1 && "złamana obietnica ma dać trwałą nieufność");
        assert(rdb.get_relation("KRW", "PLAYER").value < before_exp && "złamana obietnica ma drobną karę relacji");
        bool still_war = false;
        for (const zf::FactionRow& row : rdb.list_factions()) {
            if (row.tag == "KRW") {
                still_war = row.state == "wojna";
            }
        }
        assert(still_war && "po złamaniu obietnicy ataki trwają (wojna)");

        // Odkup CZYNEM: poprzedni okup wygasł, można żądać ponownie; udana dostawa zmniejsza nieufność.
        t += 1000;
        re.on_event(make_event("debug_command", {{"cmd", "okup-surowce"}, {"faction", "KRW"}}), rcfg, t);
        auto d2 = re.take_ransom_demands();
        assert(d2.size() == 1 && "po wygaśnięciu można żądać ponownie");
        t += 1000;
        re.on_event(make_event("ransom_paid", {{"faction", "KRW"}, {"item", d2[0].item}, {"amount", d2[0].amount}}),
                    rcfg, t);
        assert(rdb.ransom_broken("KRW") == 0 && "udana dostawa odkupuje trwałą nieufność (czynem)");
    }

    // --- Polityka frakcja↔frakcja: zasiana na starcie, nie rozmywa się z czasem ---
    {
        zf::Db pdb(":memory:");
        zf::Fallback pfb(fallback_path.string());
        zf::Engine pe(pdb, pfb, /*rng_seed=*/9);
        zf::Config pcfg;
        pcfg.spawn_wlaczone = false;

        // Wrogość korporacji z piratami istnieje od pierwszego uruchomienia i jest symetryczna.
        assert(pdb.get_relation("HEL", "KRW").value <= pcfg.prog_wrogi);
        assert(pdb.get_relation("KRW", "HEL").value == pdb.get_relation("HEL", "KRW").value);
        assert(pdb.get_relation("HEL", "WGR").value > 0 && "Helion i górnicy handlują");
        assert(pe.politics_report().find("HEL/KRW") != std::string::npos);

        // Dzięki temu „wróg mojego wroga" DZIAŁA bez ręcznego zasiewania w teście:
        // ostrzał KRW poprawia stosunki gracza z Helionem.
        const double hel_before = pdb.get_relation("HEL", "PLAYER").value;
        std::int64_t pt = 8000000;
        pe.on_event(make_event("combat_hit", {{"faction", "KRW"}, {"damage", 50.0}, {"hits", 2}, {"weapon", "t"}}),
                    pcfg, pt);
        assert(pdb.get_relation("HEL", "PLAYER").value > hel_before &&
               "atak na piratów ma podnosić relację z ich wrogiem");

        // Dryf nie dotyczy polityki: po dobie zegara wrogość HEL/KRW zostaje bez zmian,
        // choć uraza wobec gracza już blednie.
        const double politics_before = pdb.get_relation("HEL", "KRW").value;
        pdb.adjust_relation("WGR", "PLAYER", -20.0);
        const double player_before = pdb.get_relation("WGR", "PLAYER").value;
        pe.tick(pcfg, pt, /*force=*/true); // pierwszy tick tylko ustawia punkt odniesienia dryfu
        pt += static_cast<std::int64_t>(24) * 60 * kMinuteMs;
        pe.tick(pcfg, pt, /*force=*/true);
        assert(pdb.get_relation("HEL", "KRW").value == politics_before &&
               "polityka frakcji nie może dryfować do zera");
        assert(pdb.get_relation("WGR", "PLAYER").value > player_before &&
               "uraza wobec gracza ma nadal blednąć");
    }

    // --- Trwałość rajdu: restart brainu w trakcie ataku nie może gubić stanu ---
    {
        const fs::path db_file = tmp / "raid_state.sqlite3";
        std::int64_t t = 6000000;
        {
            zf::Db rdb(db_file.string());
            zf::Fallback rfb(fallback_path.string());
            zf::Engine re(rdb, rfb, /*rng_seed=*/5);
            zf::Config rcfg;
            re.on_event(make_event("debug_command", {{"cmd", "spawn"}, {"faction", "KRW"}, {"kind", "raid"}}),
                        rcfg, t);
            auto sp = re.take_spawns();
            assert(sp.size() == 1 && sp[0].kind == "raid");
        } // brain "ubity" — obiekty znikają, zostaje tylko plik bazy

        zf::Db rdb2(db_file.string());
        zf::Fallback rfb2(fallback_path.string());
        zf::Engine re2(rdb2, rfb2, /*rng_seed=*/5);
        zf::Config rcfg2;
        t += 30000;
        // Po restarcie okup nadal ma kogo odwołać (wcześniej flaga żyła tylko w pamięci).
        re2.apply_deescalation("KRW", rcfg2, t, /*amount=*/2000);
        auto sd = re2.take_standdowns();
        assert(sd.size() == 1 && sd[0].first == "KRW" && sd[0].second == 2000 &&
               "aktywny rajd ma przeżyć restart brainu");

        // Drugi raz już nie — rajd został odwołany, a to też jest w bazie.
        re2.apply_deescalation("KRW", rcfg2, t + 1000, 0);
        assert(re2.take_standdowns().empty() && "odwołany rajd nie odwołuje się drugi raz");

        // Rajd starszy niż TTL (60 min) nie liczy się jako aktywny.
        zf::Engine re3(rdb2, rfb2, /*rng_seed=*/5);
        re3.on_event(make_event("debug_command", {{"cmd", "spawn"}, {"faction", "WGR"}, {"kind", "raid"}}),
                     rcfg2, t);
        re3.take_spawns();
        re3.apply_deescalation("WGR", rcfg2, t + 2 * 60 * 60 * 1000, 0);
        assert(re3.take_standdowns().empty() && "rajd sprzed dwóch godzin jest już nieaktywny");

        std::error_code rm_ec;
        fs::remove(db_file, rm_ec);
    }

    // --- Zniszczenie STACJI: cięższa kara + trwały sufit relacji (świat mściwy) ---
    {
        zf::Db sdb(":memory:");
        zf::Fallback sfb(fallback_path.string());
        zf::Engine se(sdb, sfb, /*rng_seed=*/3);
        zf::Config scfg;
        scfg.spawn_wlaczone = false;
        std::int64_t t = 7000000;

        se.on_event(make_event("grid_destroyed", {{"faction", "HEL"}, {"grid", "Stacja Helion"},
                                                  {"by_player", true}, {"is_station", true}}),
                    scfg, t);
        const zf::RelationRow rel = sdb.get_relation("HEL", "PLAYER");
        assert(rel.value <= scfg.zniszczenie_stacji + 0.001 && "stacja ma kosztować zniszczenie_stacji (-50)");
        assert(rel.cap == scfg.sufit_po_zniszczeniu_stacji && "zniszczona stacja ma obniżyć sufit na stałe");

        // Sufit jest TRWAŁY: nawet duży plus (kontrakty, okupy) nie przebije go z powrotem.
        sdb.adjust_relation("HEL", "PLAYER", 500.0);
        assert(sdb.get_relation("HEL", "PLAYER").value == scfg.sufit_po_zniszczeniu_stacji &&
               "po zniszczeniu stacji relacja nie może wrócić powyżej sufitu");

        // Zwykły statek sufitu nie rusza.
        t += 20000;
        se.on_event(make_event("grid_destroyed", {{"faction", "WGR"}, {"grid", "Kopara"},
                                                  {"by_player", true}, {"is_station", false}}),
                    scfg, t);
        const zf::RelationRow ship = sdb.get_relation("WGR", "PLAYER");
        assert(ship.value <= scfg.zniszczenie_statku + 0.001 && ship.value > scfg.zniszczenie_stacji);
        assert(ship.cap == 100 && "zniszczony statek nie obniża sufitu");
    }

    // --- Kontrakty (Etap 6) --- świeży silnik: oferta w ticku, utrwalenie ID, rozliczenie.
    {
        zf::Db cdb(":memory:");
        zf::Fallback cfb(fallback_path.string());
        zf::Engine ce(cdb, cfb, /*rng_seed=*/11);
        zf::Config ccfg;
        ccfg.spawn_wlaczone = false; // izolujemy kanał kontraktów od spawnów
        std::int64_t t = 9000000;

        // Pierwszy tick: każda z trzech naszych frakcji wystawia po jednym zleceniu.
        ce.tick(ccfg, t, /*force=*/true);
        auto offers = ce.take_contracts();
        assert(offers.size() == 3 && "spokojne frakcje mają wystawić po zleceniu");
        assert(ce.take_contracts().empty() && "take_contracts ma czyścić bufor");
        const std::int64_t reward = offers[0].reward;
        assert(reward >= ccfg.kontrakty_nagroda_min && reward <= ccfg.kontrakty_nagroda_max);

        // Drugi tick zaraz potem: cooldown (20 min) blokuje kolejne oferty.
        t += kMinuteMs;
        ce.tick(ccfg, t, /*force=*/true);
        assert(ce.take_contracts().empty() && "cooldown ma blokować drugą ofertę");

        // Mod potwierdza powstanie kontraktu w grze -> ID trafia do SQLite (open).
        t += kMinuteMs;
        auto cout_msgs = ce.on_event(
            make_event("contract_created", {{"contract_id", "1234"}, {"faction", "WGR"},
                                            {"kind", "dostawa"}, {"opis", "500 rudy żelaza"}}),
            ccfg, t);
        assert(cdb.get_contract_faction("1234") == "WGR" && "contract_created ma utrwalić ID");
        assert(cdb.count_open_contracts("WGR") == 1);
        assert(cout_msgs.size() == 1 && cout_msgs[0].faction == "WGR" && "ogłoszenie zlecenia przez radio");

        // Limit otwartych zleceń: po upływie cooldownu WGR i tak nie dostanie drugiego.
        t += static_cast<std::int64_t>(ccfg.kontrakty_cooldown_min + 1) * kMinuteMs;
        ce.tick(ccfg, t, /*force=*/true);
        for (const zf::ContractOut& c : ce.take_contracts()) {
            assert(c.faction != "WGR" && "max_otwartych=1 ma blokować drugie zlecenie WGR");
        }

        // Wykonanie: relacja w górę o kontrakt_max, status w bazie na 'done'.
        const double before = cdb.get_relation("WGR", "PLAYER").value;
        t += kMinuteMs;
        ce.on_event(make_event("contract_done", {{"contract_id", "1234"}, {"success", true}}), ccfg, t);
        const double after = cdb.get_relation("WGR", "PLAYER").value;
        assert(after > before + ccfg.kontrakt_max - 0.001 && "wykonany kontrakt ma dać +kontrakt_max");
        assert(cdb.count_open_contracts("WGR") == 0 && "rozliczony kontrakt przestaje być otwarty");

        // contract_done bez pola faction ma działać (frakcja z bazy po ID) — tak
        // zgłasza je mod po wczytaniu świata, gdy zna już tylko ID kontraktu.
        ce.on_event(make_event("contract_created", {{"contract_id", "77"}, {"faction", "HEL"}}), ccfg, t);
        const double hel_before = cdb.get_relation("HEL", "PLAYER").value;
        t += kMinuteMs;
        ce.on_event(make_event("contract_done", {{"contract_id", "77"}, {"success", false}}), ccfg, t);
        assert(cdb.get_relation("HEL", "PLAYER").value < hel_before && "zawalony kontrakt ma karać");

        // Wrogość zamyka kran: przy relacji poniżej progu frakcja nie daje roboty.
        cdb.adjust_relation("KRW", "PLAYER", -70.0);
        t += static_cast<std::int64_t>(ccfg.kontrakty_cooldown_min + 1) * kMinuteMs;
        ce.tick(ccfg, t, /*force=*/true);
        for (const zf::ContractOut& c : ce.take_contracts()) {
            assert(c.faction != "KRW" && "wroga frakcja nie wystawia zleceń");
        }

        // Wyłącznik globalny.
        ccfg.kontrakty_wlaczone = false;
        t += static_cast<std::int64_t>(ccfg.kontrakty_cooldown_min + 1) * kMinuteMs;
        ce.tick(ccfg, t, /*force=*/true);
        assert(ce.take_contracts().empty() && "kontrakty_wlaczone=false ma wyłączyć oferty");

        // /zf kontrakt <frakcja>: wymuszona oferta mimo cooldownu i wyłącznika (jak /zf raid).
        auto dout = ce.on_event(make_event("debug_command", {{"cmd", "kontrakt"}, {"faction", "HEL"}}),
                                ccfg, t);
        auto forced = ce.take_contracts();
        assert(forced.size() == 1 && forced[0].faction == "HEL" && "/zf kontrakt ma wymusić ofertę");
        assert(dout.size() == 1 && dout[0].faction == "SYSTEM");

        // Obca frakcja (SPRT/vanilla) nie ma bloku kontraktów — odmowa, nie zlecenie.
        auto bad = ce.on_event(make_event("debug_command", {{"cmd", "kontrakt"}, {"faction", "SPRT"}}),
                               ccfg, t);
        assert(ce.take_contracts().empty() && "obca frakcja nie wystawia kontraktów");
        assert(bad.size() == 1 && bad[0].faction == "SYSTEM");
    }

    std::cout << "zf_engine_test: OK\n";
    return 0;
}
