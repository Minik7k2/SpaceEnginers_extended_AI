#include "engine.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <iostream>

namespace zf {

namespace {

constexpr const char* kPlayer = "PLAYER";
constexpr const char* kLastTickKey = "__last_tick_ms__";
constexpr const char* kLastDriftKey = "__last_drift_ms__";

// Pełne obrażenia dla ostrzal_max — powyżej tego delta się już nie pogłębia
// (zniszczenia i tak łapie grid_destroyed).
constexpr double kFullDamage = 2000.0;

std::string data_str(const Event& ev, const char* key) {
    return ev.data.contains(key) && ev.data[key].is_string() ? ev.data[key].get<std::string>()
                                                             : std::string{};
}

double data_num(const Event& ev, const char* key) {
    return ev.data.contains(key) && ev.data[key].is_number() ? ev.data[key].get<double>() : 0.0;
}

std::string state_display(const std::string& state) {
    if (state == "napiecie") {
        return "napięcie";
    }
    return state; // spokoj/wojna czytelne bez zmian ("spokój" ma ogonek, ale klucz DB bez)
}

// Rodzaj floty adekwatny do nastroju frakcji: wojna = raid, napięcie = patrol,
// spokój = convoy (cywilny przelot). Domyślnie patrol.
const char* kind_for_state(const std::string& state) {
    if (state == "wojna") {
        return "raid";
    }
    if (state == "spokoj") {
        return "convoy";
    }
    return "patrol";
}

std::string format_value(double v) {
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%+.0f", v);
    return buf;
}

// Tylko nasze frakcje mają persony i SpawnGroupy MES (HEL/KRW/WGR). Obce tagi
// (SPRT, frakcje ekonomii vanilla — RTSL, CLEN, UNIV itd.) trafiają do bazy relacji
// przy zdarzeniach, ale NIE dostają spawnów: nie ma dla nich grup MES ani zachowań,
// a factionOverride na obcą frakcję i tak kończy się odrzuceniem po stronie moda/MES.
bool is_own_faction(const std::string& tag) {
    return tag == "HEL" || tag == "KRW" || tag == "WGR";
}

} // namespace

std::string faction_color(const std::string& tag) {
    if (tag == "HEL") {
        return "blue";
    }
    if (tag == "KRW") {
        return "red";
    }
    if (tag == "WGR") {
        return "yellow";
    }
    return "white";
}

Engine::Engine(Db& db, Fallback& fallback, std::uint32_t rng_seed)
    : db_(db), fallback_(fallback), rng_(rng_seed) {
    // Nasze frakcje istnieją od startu; obce tagi (np. SPRT z vanilla/MES)
    // rejestrują się przy pierwszym zdarzeniu.
    db_.ensure_faction("HEL", "Korporacja Helion");
    db_.ensure_faction("KRW", "Krwawa Ręka");
    db_.ensure_faction("WGR", "Wolni Górnicy");
}

void Engine::ensure_known_faction(const std::string& tag) {
    if (!tag.empty()) {
        db_.ensure_faction(tag, tag);
    }
}

std::string Engine::render_first(const std::string& faction, std::initializer_list<const char*> kinds,
                                 const std::map<std::string, std::string>& vars) const {
    for (const char* kind : kinds) {
        if (fallback_.has(faction, kind)) {
            return fallback_.render(faction, kind, vars);
        }
    }
    return {};
}

void Engine::emit(std::vector<RadioOut>& out, const std::string& faction, const std::string& text,
                  int priority, const Config& cfg, std::int64_t now_ms,
                  const std::string& kind, std::string context, bool expect_decision,
                  std::string player_msg) {
    if (text.empty() && kind.empty()) {
        return;
    }
    // Limit z configu dotyczy spokojnego radia; zdarzenia bojowe (priority 1)
    // mają krótszy, stały cooldown, żeby walka nie zamieniła się w spam.
    const std::int64_t min_gap_ms =
        priority >= 1 ? 15000 : 60000 / std::max(1, cfg.radio_limit_na_frakcje_na_min);
    const auto it = last_radio_ms_.find(faction);
    if (it != last_radio_ms_.end() && now_ms - it->second < min_gap_ms) {
        return;
    }
    last_radio_ms_[faction] = now_ms;

    if (!kind.empty()) {
        const RelationRow rel = db_.get_relation(faction, kPlayer);
        std::string state = "spokoj";
        for (const FactionRow& row : db_.list_factions()) {
            if (row.tag == faction) {
                state = row.state;
            }
        }
        context += " Wasza relacja z graczem: " + format_value(rel.value) + " (" +
                   state_display(state) + ").";
    }
    out.push_back({faction, text, faction_color(faction), priority, kind, std::move(context),
                   expect_decision, std::move(player_msg)});
}

std::vector<SpawnOut> Engine::take_spawns() {
    std::vector<SpawnOut> taken;
    taken.swap(pending_spawns_);
    return taken;
}

void Engine::request_spawn(const std::string& faction, const std::string& kind, const Config& cfg,
                           std::int64_t now_ms, std::string context, bool force) {
    if (faction.empty()) {
        return;
    }
    // Obce frakcje (vanilla/MES) nie mają SpawnGroupów — nie spawnujemy ich nawet
    // przez /zf raid. Bez tego pula ticka słała spawn_request np. dla RTSL/CLEN,
    // a MES i tak je odrzucał.
    if (!is_own_faction(faction)) {
        std::cout << "[brain] spawn pominięty: frakcja " << faction
                  << " spoza moda (brak SpawnGroupa)\n";
        return;
    }
    if (!force) {
        if (!cfg.spawn_wlaczone) {
            return;
        }
        const std::int64_t cooldown_ms = static_cast<std::int64_t>(cfg.spawn_cooldown_min) * 60000;
        const auto it = last_spawn_ms_.find(faction);
        if (it != last_spawn_ms_.end() && now_ms - it->second < cooldown_ms) {
            return;
        }
    }
    last_spawn_ms_[faction] = now_ms;
    if (kind == "raid") {
        active_raids_.insert(faction); // można go potem odwołać (okup/kapitulacja/rozejm)
    }
    std::cout << "[brain] spawn_request " << faction << " kind=" << kind
              << (force ? " (wymuszony)" : "") << "\n";
    pending_spawns_.push_back({faction, kind, std::move(context), /*near_player=*/true});
}

void Engine::update_state(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    const auto rows = db_.list_factions();
    const auto row = std::find_if(rows.begin(), rows.end(),
                                  [&](const FactionRow& r) { return r.tag == faction; });
    if (row == rows.end()) {
        return;
    }

    const double value = db_.get_relation(faction, kPlayer).value;
    std::string next = row->state;

    if (row->state == "wojna") {
        // Histereza: z wojny wychodzi się dopiero powyżej progu wyjścia, nie progu wejścia.
        if (value > cfg.histereza_wyjscie_z_wojny) {
            next = value <= cfg.prog_wrogi ? "napiecie" : "spokoj";
        }
    } else {
        if (value <= cfg.prog_wojna) {
            next = "wojna";
        } else if (value <= cfg.prog_wrogi) {
            next = "napiecie";
        } else {
            next = "spokoj";
        }
    }

    if (next == row->state) {
        return;
    }
    db_.set_faction_state(faction, next);
    std::cout << "[brain] stan " << faction << ": " << row->state << " -> " << next
              << " (relacja " << format_value(value) << ")\n";
    if (next == "spokoj") {
        active_raids_.erase(faction); // pokój = żaden rajd już nie wisi
    }

    if (next == "wojna") {
        db_.add_memory(now_ms, faction, "wojna", 2, "Frakcja " + faction + " wypowiedziała wojnę graczowi.");
        emit(out, faction, render_first(faction, {"grozba", "kpina", "neutral"}, {{"sekundy", "30"}}),
             1, cfg, now_ms, "grozba", "Miarka się przebrała — wasza frakcja właśnie wypowiedziała graczowi wojnę.");
        // Wojna to rzadkie, ciężkie zdarzenie (chroni je histereza) — raid leci nawet
        // tuż po patrolu z eskalacji, więc omija cooldown. Globalny włącznik nadal działa.
        if (cfg.spawn_wlaczone) {
            request_spawn(faction, "raid", cfg, now_ms,
                          "Frakcja " + faction + " wypowiedziała wojnę i wysyła oddział bojowy na gracza.",
                          /*force=*/true);
        }
    } else if (row->state == "wojna") {
        db_.add_memory(now_ms, faction, "koniec_wojny", 2, "Frakcja " + faction + " zakończyła wojnę z graczem.");
        emit(out, faction, render_first(faction, {"neutral"}), 0, cfg, now_ms,
             "neutral", "Wojna z graczem właśnie się zakończyła — ogłoś zawieszenie broni po swojemu.");
    } else if (next == "napiecie" && row->state == "spokoj") {
        request_spawn(faction, "patrol", cfg, now_ms,
                      "Rosnące napięcie z graczem — frakcja " + faction + " wysyła patrol w jego rejon.");
    }
}

std::vector<RadioOut> Engine::on_event(const Event& ev, const Config& cfg, std::int64_t now_ms) {
    std::vector<RadioOut> out;
    if (ev.type == "combat_hit") {
        handle_combat_hit(ev, cfg, now_ms, out);
    } else if (ev.type == "grid_destroyed") {
        handle_grid_destroyed(ev, cfg, now_ms, out);
    } else if (ev.type == "proximity") {
        handle_proximity(ev, cfg, now_ms, out);
    } else if (ev.type == "chat_message") {
        handle_chat(ev, cfg, now_ms, out);
    } else if (ev.type == "debug_command") {
        handle_debug(ev, cfg, now_ms, out);
    }
    return out;
}

void Engine::handle_combat_hit(const Event& ev, const Config& cfg, std::int64_t now_ms,
                               std::vector<RadioOut>& out) {
    const std::string faction = data_str(ev, "faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    const double damage = data_num(ev, "damage");
    const double t = std::clamp(damage / kFullDamage, 0.0, 1.0);
    const double delta = cfg.ostrzal_min + (cfg.ostrzal_max - cfg.ostrzal_min) * t;
    const double value = db_.adjust_relation(faction, kPlayer, delta);
    std::cout << "[brain] relacja " << faction << "->gracz " << format_value(delta)
              << " za ostrzał => " << format_value(value) << "\n";
    db_.add_memory(now_ms, faction, "ostrzal", 0,
                   "Gracz ostrzelał " + faction + " (" + data_str(ev, "weapon") + ").");

    // Atak na wroga frakcji cieszy jej wrogów: +bonus u każdej frakcji będącej
    // z ostrzelaną w relacji <= prog_wrogi.
    for (const FactionRow& other : db_.list_factions()) {
        if (other.tag == faction) {
            continue;
        }
        if (db_.get_relation(other.tag, faction).value <= cfg.prog_wrogi) {
            const double v = db_.adjust_relation(other.tag, kPlayer, cfg.atak_na_wroga_bonus);
            std::cout << "[brain] relacja " << other.tag << "->gracz "
                      << format_value(cfg.atak_na_wroga_bonus) << " (wróg " << faction
                      << " ostrzelany) => " << format_value(v) << "\n";
        }
    }

    update_state(faction, cfg, now_ms, out);
    emit(out, faction, render_first(faction, {"grozba", "zal", "neutral"}, {{"sekundy", "60"}}),
         1, cfg, now_ms, "grozba",
         "Gracz właśnie ostrzelał wasz statek (broń: " + data_str(ev, "weapon") + "). Zareaguj.");
}

void Engine::handle_grid_destroyed(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                   std::vector<RadioOut>& out) {
    const std::string faction = data_str(ev, "faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    // Rozróżnienie statek/stacja przyjdzie z Etapem 6 (własne stacje) — na razie
    // każda zniszczona siatka liczy się jak statek.
    const double value = db_.adjust_relation(faction, kPlayer, cfg.zniszczenie_statku);
    std::cout << "[brain] relacja " << faction << "->gracz " << format_value(cfg.zniszczenie_statku)
              << " za zniszczenie statku => " << format_value(value) << "\n";
    db_.add_memory(now_ms, faction, "zniszczenie_statku", 2,
                   "Gracz zniszczył statek \"" + data_str(ev, "grid") + "\" frakcji " + faction + ".");

    update_state(faction, cfg, now_ms, out);
    emit(out, faction, render_first(faction, {"grozba", "zal", "neutral"}, {{"sekundy", "30"}}),
         1, cfg, now_ms, "grozba",
         "Gracz właśnie zniszczył wasz statek \"" + data_str(ev, "grid") + "\". Zareaguj.");
}

void Engine::handle_proximity(const Event& ev, const Config& cfg, std::int64_t now_ms,
                              std::vector<RadioOut>& out) {
    const std::string faction = data_str(ev, "faction");
    if (faction.empty() || data_str(ev, "state") != "enter") {
        return;
    }
    ensure_known_faction(faction);

    // Powitanie zależne od nastawienia: wrogo nastawieni ostrzegają, reszta wita.
    const double value = db_.get_relation(faction, kPlayer).value;
    if (value <= cfg.prog_wrogi) {
        emit(out, faction, render_first(faction, {"grozba", "kpina", "neutral"}, {{"sekundy", "20"}}),
             1, cfg, now_ms, "grozba",
             "Gracz zbliżył się do waszego statku, a nie jesteście z nim w dobrych stosunkach. Ostrzeż go.");
    } else {
        emit(out, faction, render_first(faction, {"neutral"}), 0, cfg, now_ms,
             "neutral", "Gracz zbliżył się do waszego statku. Zagadaj do niego po swojemu.");
    }
}

void Engine::handle_chat(const Event& ev, const Config& cfg, std::int64_t now_ms,
                         std::vector<RadioOut>& out) {
    // Etap 3: odpowiadamy szablonem tylko na wiadomości adresowane (@TAG) do znanej
    // frakcji. Etap 4: rozmowa (LLM + persony). Etap 5c: adresowanie ZASIĘGIEM —
    // mod podaje "signal" (clear/weak/none) adresata wg najbliższego grida frakcji.
    const std::string target = data_str(ev, "target");
    if (target.empty()) {
        return;
    }
    // Bez wymogu zasięgu (config/test) traktujemy wszystko jak w zasięgu.
    std::string signal = data_str(ev, "signal");
    if (signal.empty() || !cfg.radio_wymagaj_zasiegu) {
        signal = "clear";
    }
    for (const FactionRow& row : db_.list_factions()) {
        if (row.tag == target) {
            if (signal == "none") {
                // Poza zasięgiem: frakcja nie słyszy. Krótkie echo SYSTEM, żeby gracz
                // wiedział, że to nie błąd, tylko brak łączności.
                out.push_back({"SYSTEM", "Brak zasięgu — " + target + " nie odpowiada.",
                               "white", 0, {}, {}});
                std::cerr << "[brain] chat: " << target << " poza zasięgiem — brak odpowiedzi\n";
                return;
            }
            const double value = db_.get_relation(target, kPlayer).value;
            const char* kind = value <= cfg.prog_wrogi ? "kpina" : "neutral";

            if (signal == "weak") {
                // Szum: frakcja słyszy tylko strzępy — ma REAGOWAĆ na zakłócenia (kazać
                // powtórzyć/podejść), nie zgadywać treści. Bez decyzji o okupie/rozejmie:
                // nie da się przyjąć oferty, której się nie dosłyszało (Etap 5c).
                std::string ctx = "Odbierasz od gracza zaszumioną, rwącą się transmisję radiową — "
                                  "docierają tylko strzępy: \"" + data_str(ev, "text") +
                                  "\". NIE znasz pełnej treści i nie zgaduj, o co chodziło. Zareaguj "
                                  "na słaby sygnał po swojemu: zaznacz, że trzeszczy/rwie się, i każ "
                                  "powtórzyć albo podejść bliżej.";
                emit(out, target, render_first(target, {kind, "neutral"}), 0, cfg, now_ms,
                     kind, ctx, /*expect_decision=*/false);
                return;
            }

            const bool decyzja = chat_expects_decision(target);
            std::string ctx = "Gracz nadaje do was przez radio: \"" + data_str(ev, "text") +
                              "\". Odpowiedz mu.";
            if (decyzja) {
                ctx += " Prowadzicie teraz działania zbrojne przeciw graczowi. W osobnym polu JSON "
                       "\"odpuszcza\" wpisz true, jeśli przyjmujesz jego prośbę (okup, kapitulacja "
                       "albo rozejm) i odwołujesz atak, albo false, jeśli odmawiasz. W samej "
                       "wypowiedzi (pole \"tresc\") NIE pisz słowa \"odpuszcza\" ani true/false — "
                       "to ma być wyłącznie kwestia radiowa w twoim charakterze. Decyduj wedle "
                       "swojej natury i tego, co gracz wam zrobił.";
            }
            emit(out, target, render_first(target, {kind, "neutral"}), 0, cfg, now_ms,
                 kind, ctx, decyzja, /*player_msg=*/data_str(ev, "text"));
            return;
        }
    }
}

bool Engine::chat_expects_decision(const std::string& faction) const {
    if (active_raids_.count(faction) > 0) {
        return true;
    }
    const auto rows = db_.list_factions();
    const auto row = std::find_if(rows.begin(), rows.end(),
                                  [&](const FactionRow& r) { return r.tag == faction; });
    return row != rows.end() && (row->state == "napiecie" || row->state == "wojna");
}

void Engine::apply_deescalation(const std::string& faction, const Config& cfg,
                                std::int64_t now_ms) {
    if (active_raids_.count(faction) == 0) {
        return; // nie ma aktywnego rajdu — nie ma czego odwoływać
    }
    active_raids_.erase(faction);
    const double value = db_.adjust_relation(faction, kPlayer, cfg.deeskalacja_bonus);
    db_.add_memory(now_ms, faction, "deeskalacja", 1,
                   "Frakcja " + faction + " przyjęła propozycję gracza i odwołała atak.");
    std::cout << "[brain] deeskalacja " << faction << ": przyjęto (relacja "
              << format_value(cfg.deeskalacja_bonus) << " => " << format_value(value)
              << "), stand_down\n";
    pending_standdowns_.push_back(faction);
}

std::vector<std::string> Engine::take_standdowns() {
    std::vector<std::string> taken;
    taken.swap(pending_standdowns_);
    return taken;
}

void Engine::handle_debug(const Event& ev, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    const std::string cmd = data_str(ev, "cmd");
    if (cmd == "rel") {
        out.push_back({"SYSTEM", relations_report(), "white", 0, {}, {}});
    } else if (cmd == "tick") {
        std::vector<RadioOut> tick_out = tick(cfg, now_ms, /*force=*/true);
        out.push_back({"SYSTEM", "Tick wymuszony.", "white", 0, {}, {}});
        out.insert(out.end(), tick_out.begin(), tick_out.end());
    } else if (cmd == "spawn") {
        // /zf raid <frakcja> [kind]: wymuszony spawn do testu potoku (omija cooldown).
        const std::string faction = data_str(ev, "faction");
        std::string state = "spokoj";
        bool known = false;
        for (const FactionRow& row : db_.list_factions()) {
            if (row.tag == faction) {
                state = row.state;
                known = true;
            }
        }
        if (!known) {
            out.push_back({"SYSTEM", "Nie znam frakcji \"" + faction + "\" — spawn pominięty.", "white", 0, {}, {}});
            return;
        }
        std::string kind = data_str(ev, "kind");
        if (kind.empty()) {
            kind = kind_for_state(state);
        }
        request_spawn(faction, kind, cfg, now_ms, "Ręcznie wywołany spawn (/zf raid).", /*force=*/true);
        out.push_back({"SYSTEM", "Spawn zlecony: " + faction + " (" + kind + ").", "white", 0, {}, {}});
    } else if (cmd == "okup") {
        // /zf okup <frakcja>: deterministyczny wyzwalacz de-eskalacji — niezależny od
        // LLM (qwen-3B bywa za słaby, by sam trafnie odpuścić). Odwołuje aktywny rajd.
        const std::string faction = data_str(ev, "faction");
        if (active_raids_.count(faction) == 0) {
            out.push_back({"SYSTEM", "Frakcja " + faction + " nie prowadzi rajdu — nie ma czego odwołać.",
                           "white", 0, {}, {}});
            return;
        }
        // Głos frakcji szablonem (pusty kind = z pominięciem LLM), potem naliczenie + stand_down.
        const std::string voice = render_first(faction, {"neutral"});
        if (!voice.empty()) {
            out.push_back({faction, voice, faction_color(faction), 0, {}, {}});
        }
        apply_deescalation(faction, cfg, now_ms);
        out.push_back({"SYSTEM", "Okup przyjęty: " + faction + " odwołuje rajd.", "white", 0, {}, {}});
    }
}

std::vector<RadioOut> Engine::tick(const Config& cfg, std::int64_t now_ms, bool force) {
    std::vector<RadioOut> out;

    const std::int64_t tick_ms = static_cast<std::int64_t>(cfg.tick_co_minut) * 60000;
    const std::int64_t last_tick = db_.get_kv(kLastTickKey);
    if (!force && last_tick != 0 && now_ms - last_tick < tick_ms) {
        return out;
    }
    db_.set_kv(kLastTickKey, now_ms);

    // 1) Dryf: relacje wolno wracają do 0 (dryf_pkt na dryf_co_minut).
    const std::int64_t drift_period_ms = static_cast<std::int64_t>(cfg.dryf_co_minut) * 60000;
    std::int64_t last_drift = db_.get_kv(kLastDriftKey);
    if (last_drift == 0) {
        last_drift = now_ms; // pierwszy tick świata — dryf liczymy od teraz
        db_.set_kv(kLastDriftKey, last_drift);
    }
    const std::int64_t steps = drift_period_ms > 0 ? (now_ms - last_drift) / drift_period_ms : 0;
    if (steps > 0) {
        for (const auto& [a, b] : db_.list_relation_pairs()) {
            const double value = db_.get_relation(a, b).value;
            const double magnitude = std::min(std::abs(value), cfg.dryf_pkt * static_cast<double>(steps));
            if (magnitude > 0) {
                db_.adjust_relation(a, b, value > 0 ? -magnitude : magnitude);
            }
        }
        db_.set_kv(kLastDriftKey, last_drift + steps * drift_period_ms);
        std::cout << "[brain] dryf relacji: " << steps << " krok(ów)\n";
    }

    // 2) Maszyna stanów + odnowienie budżetu akcji.
    for (const FactionRow& row : db_.list_factions()) {
        update_state(row.tag, cfg, now_ms, out);
        db_.set_faction_budget(row.tag, cfg.budzet_akcji_na_tick);
    }

    // E6: wojna to stan ciągły, nie tylko krawędź wejścia. Dopóki frakcja jest
    // w wojnie, ponawiamy rajd co tick — request_spawn bramkuje to spawn_cooldown_min
    // i spawn_wlaczone, a cooldown z rajdu wejściowego chroni przed podwójnym spawnem.
    // Bez tego wojna oznaczała jeden oddział przy wejściu i potem ciszę. Świeży odczyt
    // stanu (po update_state), bo powyższa pętla mogła go właśnie zmienić.
    for (const FactionRow& row : db_.list_factions()) {
        if (row.state == "wojna") {
            request_spawn(row.tag, "raid", cfg, now_ms,
                          "Trwa wojna — frakcja " + row.tag + " ponawia nalot na gracza.");
        }
    }

    // 3) Zdarzenie losowe ważone stanem (wojna 3 : napięcie 2 : spokój 1).
    std::uniform_real_distribution<double> roll(0.0, 1.0);
    if (roll(rng_) < cfg.szansa_zdarzenia_losowego) {
        const std::vector<FactionRow> factions = db_.list_factions();
        std::vector<int> weights;
        for (const FactionRow& row : factions) {
            weights.push_back(row.state == "wojna" ? 3 : row.state == "napiecie" ? 2 : 1);
        }
        if (!factions.empty()) {
            std::discrete_distribution<std::size_t> pick(weights.begin(), weights.end());
            const FactionRow& chosen = factions[pick(rng_)];
            if (chosen.action_budget > 0) {
                db_.set_faction_budget(chosen.tag, chosen.action_budget - 1);
                const char* kind = chosen.state == "wojna"      ? "grozba"
                                   : chosen.state == "napiecie" ? "kpina"
                                                                : "neutral";
                emit(out, chosen.tag,
                     render_first(chosen.tag, {kind, "neutral"}, {{"sekundy", "45"}}),
                     chosen.state == "wojna" ? 1 : 0, cfg, now_ms, kind,
                     "Nic szczególnego się nie dzieje — nadaj krótką rutynową transmisję w waszym stylu.");
                request_spawn(chosen.tag, kind_for_state(chosen.state), cfg, now_ms,
                              "Rutynowy ruch floty frakcji " + chosen.tag + " w rejonie gracza.");
            }
        }
    }

    return out;
}

std::string Engine::relations_report() const {
    std::string report;
    for (const FactionRow& row : db_.list_factions()) {
        if (!report.empty()) {
            report += " | ";
        }
        const RelationRow rel = db_.get_relation(row.tag, kPlayer);
        report += row.tag + " " + format_value(rel.value) + " (" + state_display(row.state) + ")";
        if (rel.cap < 100) {
            report += " sufit " + format_value(rel.cap);
        }
    }
    return report.empty() ? "Brak frakcji w bazie." : report;
}

} // namespace zf
