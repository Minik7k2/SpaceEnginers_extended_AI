#include "engine.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <iostream>
#include <sstream>

namespace zf {

namespace {

constexpr const char* kPlayer = "PLAYER";
constexpr const char* kLastTickKey = "__last_tick_ms__";
constexpr const char* kLastDriftKey = "__last_drift_ms__";

// Pełne obrażenia dla ostrzal_max — powyżej tego delta się już nie pogłębia
// (zniszczenia i tak łapie grid_destroyed).
constexpr double kFullDamage = 2000.0;

// Wartość handlu (kredyty) dla handel_max — powyżej delta się już nie pogłębia (Etap 6).
constexpr double kFullTrade = 5000.0;

// Doklejane do kontekstu rozmowy, gdy frakcja prowadzi działania zbrojne
// (chat_expects_decision) — LLM decyduje wtedy o odpuszczeniu albo trybucie.
constexpr const char* kDecisionPrompt =
    " Prowadzicie teraz działania zbrojne przeciw graczowi. Masz dwie osobne "
    "decyzje w polach JSON (obie domyślnie false). \"odpuszcza\": wpisz true, "
    "jeśli TERAZ odwołujesz atak (przyjmujesz kredyty, kapitulację albo rozejm). "
    "\"zada_surowce\": wpisz true, jeśli zamiast tego ŻĄDASZ trybutu w surowcach — "
    "każesz graczowi dostarczyć ładunek do wyznaczonej skrzynki zrzutu, a ogień "
    "wstrzymujesz dopiero na czas dostawy. Ustaw najwyżej jedno z nich na true; "
    "jeśli odmawiasz wszystkiego, oba zostaw false. W samej wypowiedzi (pole "
    "\"tresc\") NIE pisz nazw tych pól ani true/false — to ma być wyłącznie "
    "kwestia radiowa w twoim charakterze. Jeśli żądasz trybutu, zapowiedz to po "
    "swojemu; konkretną ilość i miejsce zrzutu poda osobny komunikat. Decyduj "
    "wedle swojej natury i tego, co gracz wam zrobił.";

// Klucz surowca -> nazwa po polsku do promptu. Brain operuje kluczami (mod tłumaczy je na
// SubtypeId), ale model ma mówić do gracza po ludzku.
std::string item_pl(const std::string& key) {
    if (key == "Iron") return "sztabek żelaza";
    if (key == "Nickel") return "sztabek niklu";
    if (key == "Silicon") return "sztabek krzemu";
    if (key == "Cobalt") return "sztabek kobaltu";
    if (key == "Silver") return "sztabek srebra";
    if (key == "Gold") return "sztabek złota";
    if (key == "Platinum") return "sztabek platyny";
    if (key == "Magnesium") return "sztabek magnezu";
    if (key == "Uranium") return "sztabek uranu";
    return key;
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

bool is_known_contract_kind(const std::string& kind) {
    const auto& kinds = Config::contract_kinds();
    return std::find(kinds.begin(), kinds.end(), kind) != kinds.end();
}

// Typ kontraktu z brakującego/nieznanego pola. Stare zapisy (przed rozbudową typów)
// i mod bez naszej wersji Contracts.cs przysyłają puste pole — to była dostawa.
std::string kontrakt_kind_or_default(const std::string& kind) {
    return is_known_contract_kind(kind) ? kind : "dostawa";
}

// Rozbija listę surowców "Iron,Nickel,Silicon" z configu na wektor kluczy;
// białe znaki i puste pozycje pomija.
std::vector<std::string> split_items(const std::string& csv) {
    std::vector<std::string> items;
    std::string cur;
    for (const char c : csv) {
        if (c == ',') {
            if (!cur.empty()) {
                items.push_back(cur);
            }
            cur.clear();
        } else if (c != ' ' && c != '\t') {
            cur.push_back(c);
        }
    }
    if (!cur.empty()) {
        items.push_back(cur);
    }
    return items;
}

// Opróżnia bufor wychodzący (spawny, kontrakty, reputacja...) — wspólne dla take_*().
template <class T>
std::vector<T> take(std::vector<T>& pending) {
    std::vector<T> taken;
    taken.swap(pending);
    return taken;
}

// Komunikat techniczny na czat (odpowiedzi na /zf ...). Pusty kind = nigdy przez LLM.
RadioOut system_msg(std::string text) {
    return {"SYSTEM", std::move(text), "white", 0, {}, {}};
}

// Wartość z widełek [lo, hi] wg t z [0, 1] (siła ostrzału, wartość handlu, relacja).
double lerp(double lo, double hi, double t) {
    return lo + (hi - lo) * t;
}

std::int64_t minutes_ms(int minutes) {
    return static_cast<std::int64_t>(minutes) * 60000;
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

namespace {

// Liniowa interpolacja między dwoma węzłami odwzorowania relacja->reputacja.
double map_segment(double v, double x0, double x1, double y0, double y1) {
    if (x1 == x0) {
        return y1;
    }
    return y0 + (v - x0) / (x1 - x0) * (y1 - y0);
}

} // namespace

int vanilla_reputation(double value, const Config& cfg) {
    const double zakres = std::max(1, cfg.reputacja_zakres);
    // Próg musi zostawić miejsce na odcinek wroga/sojusznika, inaczej całe pasmo
    // -100..prog_wrogi zeszłoby do jednej liczby.
    const double prog = std::clamp(static_cast<double>(cfg.reputacja_prog), 1.0, zakres - 1.0);
    // Nasze progi z [relacje] są hot-reloadowane, więc bierzemy je z configu, ale
    // pilnujemy sensownego znaku (dodatni prog_wrogi wywróciłby odwzorowanie).
    const double wrogi = std::min(cfg.prog_wrogi, -1.0);
    const double sojusz = std::max(cfg.prog_sojusznik, 1.0);
    const double v = std::clamp(value, -100.0, 100.0);

    double out;
    if (v <= wrogi) {
        // Węzeł na progu to -(prog+1), a nie -prog: dokładnie na naszym progu wrogości
        // gracz ma być w grze WROGIEM, a nie stać jedną nogą w neutralności.
        out = map_segment(v, -100.0, wrogi, -zakres, -(prog + 1.0));
    } else if (v < 0) {
        out = map_segment(v, wrogi, 0.0, -prog, 0.0);
    } else if (v < sojusz) {
        out = map_segment(v, 0.0, sojusz, 0.0, prog);
    } else {
        out = map_segment(v, sojusz, 100.0, prog + 1.0, zakres);
    }
    const double clamped = std::clamp(out, -zakres, zakres);
    return static_cast<int>(std::llround(clamped));
}

double price_modifier(double value, const Config& cfg) {
    const double v = std::clamp(value, -100.0, 100.0);
    // Węzeł w zerze jest twardy: przy neutralnej relacji cennik ma zostać DOKŁADNIE taki,
    // jaki wygenerowała gra. Inaczej sam start świata przestawiałby ceny na wszystkich
    // stacjach, a gracz nie miałby z czym porównać późniejszej zniżki czy kary.
    const double out = v <= 0 ? map_segment(v, -100.0, 0.0, cfg.ceny_mnoznik_wrog, 1.0)
                              : map_segment(v, 0.0, 100.0, 1.0, cfg.ceny_mnoznik_sojusznik);
    // Pasmo bezpieczeństwa: cena to int w grze, więc mnożnik 0 robi towar za darmo,
    // a wielkie wartości przepełniają cennik. Config ma prawo być odważny, nie absurdalny.
    return std::clamp(out, 0.1, 10.0);
}

Engine::Engine(Db& db, Fallback& fallback, std::uint32_t rng_seed)
    : db_(db), fallback_(fallback), rng_(rng_seed) {
    // Nasze frakcje istnieją od startu; obce tagi (np. SPRT z vanilla/MES)
    // rejestrują się przy pierwszym zdarzeniu.
    db_.ensure_faction("HEL", "Korporacja Helion");
    db_.ensure_faction("KRW", "Krwawa Ręka");
    db_.ensure_faction("WGR", "Wolni Górnicy");
    seed_faction_politics();
}

void Engine::seed_faction_politics() {
    // Świat nie zaczyna się od zera: korporacja i piraci są w stanie zimnej wojny,
    // piraci łupią górników, a Helion z górnikami handluje. Bez tych wartości relacje
    // frakcja↔frakcja były wyłącznie teoretyczne — a to od nich zależy, czy strzelanie
    // do wroga danej frakcji cokolwiek u niej daje (atak_na_wroga_bonus).
    // Zasiewamy RAZ na świat; potem zmieniają je wyłącznie zdarzenia, nie config.
    constexpr const char* kSeededKey = "__politics_seeded__";
    if (db_.get_kv(kSeededKey) != 0) {
        return;
    }
    struct Pair {
        const char* a;
        const char* b;
        double value;
    };
    static const Pair kStart[] = {
        {"HEL", "KRW", -70}, // korporacja vs piractwo: otwarta wrogość
        {"KRW", "WGR", -50}, // Krwawa Ręka żeruje na konwojach górników
        {"HEL", "WGR", 10},  // Helion skupuje urobek — chłodna współpraca
    };
    for (const Pair& p : kStart) {
        db_.adjust_relation(p.a, p.b, p.value); // relacje są dwukierunkowe i symetryczne
        db_.adjust_relation(p.b, p.a, p.value);
    }
    db_.set_kv(kSeededKey, 1);
    std::cout << "[brain] polityka frakcji zasiana: " << politics_report() << "\n";
}

void Engine::ensure_known_faction(const std::string& tag) {
    if (!tag.empty()) {
        db_.ensure_faction(tag, tag);
    }
}

bool Engine::has_active_raid(const std::string& faction, std::int64_t now_ms) const {
    const std::int64_t started = db_.get_kv(raid_key(faction));
    if (started == 0) {
        return false;
    }
    // Bez TTL flaga rajdu z poprzedniej sesji wisiałaby w bazie wiecznie: frakcja
    // pytałaby o okup za atak, którego dawno nie ma (statki MES same despawnują).
    return now_ms - started < kRaidTtlMs;
}

void Engine::set_active_raid(const std::string& faction, std::int64_t now_ms) {
    db_.set_kv(raid_key(faction), now_ms);
}

std::optional<PendingRansom> Engine::active_ransom(const std::string& faction,
                                                  std::int64_t now_ms) {
    auto ransom = db_.get_pending_ransom(faction);
    if (ransom && now_ms - ransom->deadline_ms > kRansomGraceMs) {
        std::cout << "[brain] okup surowcowy " << faction
                  << ": żądanie po terminie bez odpowiedzi moda — wygaszone\n";
        db_.clear_pending_ransom(faction);
        return std::nullopt;
    }
    return ransom;
}

std::optional<FactionRow> Engine::find_faction(const std::string& tag) const {
    for (FactionRow& row : db_.list_factions()) {
        if (row.tag == tag) {
            return std::move(row);
        }
    }
    return std::nullopt;
}

std::string Engine::faction_state(const std::string& tag) const {
    const auto row = find_faction(tag);
    return row ? row->state : "spokoj";
}

bool Engine::on_cooldown(const std::string& key, std::int64_t period_ms, std::int64_t now_ms) const {
    const std::int64_t last = db_.get_kv(key);
    return last != 0 && now_ms - last < period_ms;
}

double Engine::shift_player_relation(const std::string& faction, double delta, const std::string& why) {
    const double value = db_.adjust_relation(faction, kPlayer, delta);
    std::cout << "[brain] relacja " << faction << "->gracz " << format_value(delta) << " " << why
              << " => " << format_value(value) << "\n";
    return value;
}

std::string Engine::contract_faction(const Event& ev, const std::string& contract_id) const {
    // Z pola zdarzenia, a gdy go nie ma — z bazy po ID kontraktu (mod po wczytaniu
    // świata zna tylko ID, przypisanie do frakcji trzyma brain).
    std::string faction = ev.str("faction");
    if (faction.empty() && !contract_id.empty()) {
        faction = db_.get_contract_faction(contract_id);
    }
    return faction;
}

void Engine::push_template_voice(std::vector<RadioOut>& out, const std::string& faction) const {
    // Głos frakcji szablonem (pusty kind = z pominięciem LLM) — komendy deterministyczne.
    const std::string voice = render_first(faction, {"neutral"});
    if (!voice.empty()) {
        out.push_back({faction, voice, faction_color(faction), 0, {}, {}});
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
        context += " Wasza relacja z graczem: " + format_value(rel.value) + " (" +
                   state_display(faction_state(faction)) + ").";
    }
    out.push_back({faction, text, faction_color(faction), priority, kind, std::move(context),
                   expect_decision, std::move(player_msg)});
}

std::vector<SpawnOut> Engine::take_spawns() {
    return take(pending_spawns_);
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
        if (on_cooldown(spawn_key(faction), minutes_ms(cfg.spawn_cooldown_min), now_ms)) {
            return;
        }
    }
    db_.set_kv(spawn_key(faction), now_ms);
    if (kind == "raid") {
        set_active_raid(faction, now_ms); // można go potem odwołać (okup/kapitulacja/rozejm)
    }
    std::cout << "[brain] spawn_request " << faction << " kind=" << kind
              << (force ? " (wymuszony)" : "") << "\n";
    pending_spawns_.push_back({faction, kind, std::move(context), /*near_player=*/true});
}

void Engine::update_state(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    const auto row = find_faction(faction);
    if (!row) {
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
        set_active_raid(faction, 0); // pokój = żaden rajd już nie wisi
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
    using Handler = void (Engine::*)(const Event&, const Config&, std::int64_t,
                                     std::vector<RadioOut>&);
    // heartbeat nie ma handlera; session_start dodatkowo wymusza resync niżej.
    static const std::map<std::string, Handler> kHandlers = {
        {"session_start", &Engine::handle_session_start},
        {"combat_hit", &Engine::handle_combat_hit},
        {"grid_destroyed", &Engine::handle_grid_destroyed},
        {"proximity", &Engine::handle_proximity},
        {"chat_message", &Engine::handle_chat},
        {"debug_command", &Engine::handle_debug},
        {"trade", &Engine::handle_trade},
        {"contract_created", &Engine::handle_contract_created},
        {"contract_taken", &Engine::handle_contract_taken},
        {"contract_done", &Engine::handle_contract_done},
        {"ransom_paid", &Engine::handle_ransom_paid},
        {"ransom_expired", &Engine::handle_ransom_expired},
    };

    std::vector<RadioOut> out;
    const auto handler = kHandlers.find(ev.type);
    if (handler != kHandlers.end()) {
        (this->*handler->second)(ev, cfg, now_ms, out);
    }
    // Świeżo wczytany świat ma reputację z zapisu (albo z DefaultRelation w SBC), a nie
    // z naszej bazy — po session_start przepisujemy WSZYSTKO, bez czekania na zmianę.
    sync_reputations(cfg, /*force=*/ev.type == "session_start");
    // Cennik tak samo: świeżo wczytany świat ma w sklepach ceny z zapisu, a mod nie wie,
    // od jakiego mnożnika je odtworzyć, dopóki mu go nie podamy.
    sync_prices(cfg, /*force=*/ev.type == "session_start");
    return out;
}

std::vector<ContractOut> Engine::take_contracts() {
    return take(pending_contracts_);
}

std::vector<ReputationOut> Engine::take_reputations() {
    return take(pending_reputations_);
}

void Engine::sync_reputations(const Config& cfg, bool force) {
    if (!cfg.reputacja_sync) {
        reputacja_byla_wlaczona_ = false;
        return;
    }
    if (!reputacja_byla_wlaczona_) {
        force = true; // włączone dopiero co (hot-reload) — mod nie zna jeszcze żadnej wartości
        reputacja_byla_wlaczona_ = true;
    }

    // Kolejka wychodząca tylko dla NASZYCH frakcji. Reputacja frakcji vanilla/MES
    // (RTSL, SPRT, ...) należy do gry — nadpisywanie jej naszymi liczbami psułoby
    // ekonomię, której nie prowadzimy.
    const auto push = [&](const std::string& a, const std::string& b, double value) {
        const int vanilla = vanilla_reputation(value, cfg);
        const std::string key = b.empty() ? a : a + "|" + b;
        const auto it = last_vanilla_.find(key);
        if (!force && it != last_vanilla_.end() && it->second == vanilla) {
            return;
        }
        last_vanilla_[key] = vanilla;
        pending_reputations_.push_back({a, b, value, vanilla});
    };

    std::vector<std::string> own;
    for (const FactionRow& row : db_.list_factions()) {
        if (is_own_faction(row.tag)) {
            own.push_back(row.tag);
        }
    }
    for (const std::string& tag : own) {
        push(tag, {}, db_.get_relation(tag, kPlayer).value);
    }
    if (!cfg.reputacja_polityka) {
        return;
    }
    // Polityka frakcja↔frakcja: w grze reputacja pary jest symetryczna (SetReputation
    // ustawia ją w obie strony), więc wysyłamy każdą parę raz.
    for (std::size_t i = 0; i < own.size(); ++i) {
        for (std::size_t j = i + 1; j < own.size(); ++j) {
            push(own[i], own[j], db_.get_relation(own[i], own[j]).value);
        }
    }
}

std::vector<PriceOut> Engine::take_prices() {
    return take(pending_prices_);
}

void Engine::sync_prices(const Config& cfg, bool force) {
    if (!cfg.ceny_sync) {
        ceny_byly_wlaczone_ = false;
        return;
    }
    if (!ceny_byly_wlaczone_) {
        force = true; // włączone dopiero co (hot-reload) — mod nie zna jeszcze cennika
        ceny_byly_wlaczone_ = true;
    }

    // Tylko NASZE frakcje: cennik stacji vanilla/MES należy do gry, tak samo jak ich
    // reputacja. Nadpisywanie go psułoby ekonomię, której nie prowadzimy.
    for (const FactionRow& row : db_.list_factions()) {
        if (!is_own_faction(row.tag)) {
            continue;
        }
        const double value = db_.get_relation(row.tag, kPlayer).value;
        const double modifier = price_modifier(value, cfg);
        const bool embargo = value <= cfg.ceny_prog_embarga;

        const auto it = last_price_.find(row.tag);
        if (!force && it != last_price_.end() && it->second.embargo == embargo &&
            std::fabs(it->second.modifier - modifier) < cfg.ceny_prog_zmiany) {
            continue; // drgnięcie poniżej progu i embargo bez zmian — nie ruszamy sklepu
        }
        last_price_[row.tag] = LastPrice{modifier, embargo};
        pending_prices_.push_back({row.tag, value, modifier, embargo});
    }
}

void Engine::handle_combat_hit(const Event& ev, const Config& cfg, std::int64_t now_ms,
                               std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    const double t = std::clamp(ev.num("damage") / kFullDamage, 0.0, 1.0);
    shift_player_relation(faction, lerp(cfg.ostrzal_min, cfg.ostrzal_max, t), "za ostrzał");
    db_.add_memory(now_ms, faction, "ostrzal", 0,
                   "Gracz ostrzelał " + faction + " (" + ev.str("weapon") + ").");

    // Atak na wroga frakcji cieszy jej wrogów: +bonus u każdej frakcji będącej
    // z ostrzelaną w relacji <= prog_wrogi. Z cooldownem — mod zgłasza combat_hit co 3 s,
    // więc bez niego jedna dłuższa strzelanina wywindowała relacje u wszystkich wrogów
    // ostrzelanej frakcji (obserwacja z gry: +15 u HEL i WGR w 20 sekund).
    const std::int64_t enemy_cd_ms = minutes_ms(std::max(0, cfg.atak_na_wroga_cooldown_min));
    for (const FactionRow& other : db_.list_factions()) {
        if (other.tag == faction) {
            continue;
        }
        if (db_.get_relation(other.tag, faction).value <= cfg.prog_wrogi) {
            const auto key = std::make_pair(other.tag, faction);
            const auto seen = enemy_bonus_at_.find(key);
            if (seen != enemy_bonus_at_.end() && now_ms - seen->second < enemy_cd_ms) {
                continue; // ta sama potyczka — bonus już wypłacony
            }
            enemy_bonus_at_[key] = now_ms;
            shift_player_relation(other.tag, cfg.atak_na_wroga_bonus,
                                  "(wróg " + faction + " ostrzelany)");
        }
    }

    update_state(faction, cfg, now_ms, out);
    emit(out, faction, render_first(faction, {"grozba", "zal", "neutral"}, {{"sekundy", "60"}}),
         1, cfg, now_ms, "grozba",
         "Gracz właśnie ostrzelał wasz statek (broń: " + ev.str("weapon") + "). Zareaguj.");
}

void Engine::handle_grid_destroyed(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                   std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    // Stacja (siatka statyczna) to nie to samo co statek: kara jest wyższa, a do tego
    // zostaje TRWAŁY modyfikator — sufit relacji. Świat mściwy z CLAUDE.md: takiego
    // czynu nie zmyje ani dryf, ani okup; górna granica sympatii frakcji spada na stałe.
    const bool is_station = ev.flag("is_station", false);
    const std::string what = is_station ? "stację" : "statek";
    const double delta = is_station ? cfg.zniszczenie_stacji : cfg.zniszczenie_statku;

    if (is_station) {
        db_.lower_relation_cap(faction, kPlayer, cfg.sufit_po_zniszczeniu_stacji);
        std::cout << "[brain] sufit relacji " << faction << "->gracz obniżony na stałe do "
                  << format_value(cfg.sufit_po_zniszczeniu_stacji) << " (zniszczona stacja)\n";
    }

    shift_player_relation(faction, delta,
                          std::string("za zniszczenie ") + (is_station ? "stacji" : "statku"));
    const std::string grid_name = ev.str("grid");
    db_.add_memory(now_ms, faction, is_station ? "zniszczenie_stacji" : "zniszczenie_statku", 2,
                   "Gracz zniszczył " + what + " \"" + grid_name + "\" frakcji " + faction +
                       (is_station ? " — tego nie zapomnimy nigdy." : "."));

    update_state(faction, cfg, now_ms, out);
    const std::string ctx =
        is_station
            ? "Gracz właśnie zniszczył waszą STACJĘ \"" + grid_name +
                  "\" — to strata nie do odrobienia. Zareaguj."
            : "Gracz właśnie zniszczył wasz statek \"" + grid_name + "\". Zareaguj.";
    emit(out, faction, render_first(faction, {"grozba", "zal", "neutral"}, {{"sekundy", "30"}}),
         1, cfg, now_ms, "grozba", ctx);
}

void Engine::handle_proximity(const Event& ev, const Config& cfg, std::int64_t now_ms,
                              std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty() || ev.str("state") != "enter") {
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
    // Saldo gracza dosyłane przez mod (ModAPI ma je od ręki, brain nie ma jak policzyć).
    // Bramka kredytowa przyjmuje tylko ofertę pokrytą saldem — inaczej "dam ci milion"
    // z pustym kontem kupowałoby pokój za darmo.
    if (ev.has_num("balans")) {
        player_balance_ = static_cast<std::int64_t>(ev.num("balans"));
    }

    const std::string target = ev.str("target");
    if (target.empty()) {
        return;
    }
    // Bez wymogu zasięgu (config/test) traktujemy wszystko jak w zasięgu.
    std::string signal = ev.str("signal");
    if (signal.empty() || !cfg.radio_wymagaj_zasiegu) {
        signal = "clear";
    }
    if (!find_faction(target)) {
        return; // adresat spoza bazy — nikt nie odbiera
    }
    if (signal == "none") {
        // Poza zasięgiem: frakcja nie słyszy. Krótkie echo SYSTEM, żeby gracz
        // wiedział, że to nie błąd, tylko brak łączności.
        out.push_back(system_msg("Brak zasięgu — " + target + " nie odpowiada."));
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
                          "docierają tylko strzępy: \"" + ev.str("text") +
                          "\". NIE znasz pełnej treści i nie zgaduj, o co chodziło. Zareaguj "
                          "na słaby sygnał po swojemu: zaznacz, że trzeszczy/rwie się, i każ "
                          "powtórzyć albo podejść bliżej.";
        emit(out, target, render_first(target, {kind, "neutral"}), 0, cfg, now_ms,
             kind, ctx, /*expect_decision=*/false);
        return;
    }

    const bool decyzja = chat_expects_decision(target, now_ms);
    std::string ctx = "Gracz nadaje do was przez radio: \"" + ev.str("text") +
                      "\". Odpowiedz mu.";

    ctx += pending_ransom_context(target, now_ms);
    if (decyzja) {
        ctx += kDecisionPrompt;
    }
    emit(out, target, render_first(target, {kind, "neutral"}), 0, cfg, now_ms,
         kind, ctx, decyzja, /*player_msg=*/ev.str("text"));
}

std::string Engine::pending_ransom_context(const std::string& faction, std::int64_t now_ms) {
    // Wiszący trybut w kontekście: bez tego na pytanie "ile mi zostało czasu?"
    // model zmyślał, bo o żadnym terminie nie wiedział (obserwacja z gry).
    const auto ransom = active_ransom(faction, now_ms);
    if (!ransom) {
        return {};
    }
    const std::int64_t left_ms = ransom->deadline_ms - now_ms;
    if (left_ms <= 0) {
        return {}; // termin minął, zaraz przyjdzie ransom_expired
    }
    const std::int64_t left_min = (left_ms + 59999) / 60000;
    return " Wisi twoje żądanie trybutu: gracz ma dostarczyć " +
           std::to_string(ransom->amount) + " " + item_pl(ransom->item) +
           " do skrzynki zrzutu (ma ją oznaczoną na mapie jako ZRZUT " + faction +
           "), a do końca terminu zostało mu " + std::to_string(left_min) +
           " min. Jeśli pyta o czas, ilość albo miejsce — podaj te liczby wprost "
           "i nie zmyślaj innych.";
}

void Engine::handle_trade(const Event& ev, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    std::string kind = ev.str("kind"); // buy/sell — informacyjnie
    if (kind.empty()) {
        kind = "wymiana";
    }
    const double value = ev.num("value");
    const double t = std::clamp(value / kFullTrade, 0.0, 1.0);
    shift_player_relation(faction, lerp(cfg.handel_min, cfg.handel_max, t),
                          "za handel (" + kind + " " + format_value(value) + ")");
    db_.add_memory(now_ms, faction, "handel", 0, "Gracz handlował z " + faction + " (" + kind + ").");
    update_state(faction, cfg, now_ms, out); // handel bez radia — zbyt częsty, żeby nadawać
}

void Engine::handle_contract_created(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                     std::vector<RadioOut>& out) {
    const std::string contract_id = ev.str("contract_id");
    const std::string faction = ev.str("faction");
    if (contract_id.empty() || faction.empty()) {
        return;
    }
    ensure_known_faction(faction);

    // Utrwalenie: ID z gry musi przeżyć restart świata (CLAUDE.md), a przy
    // contract_done to stąd bierzemy frakcję — mod nie musi jej pamiętać.
    // Mod mógł zamienić typ na dostawę (brak celu w świecie) — utrwalamy TO, co
    // naprawdę powstało w grze, bo od tego zależy mnożnik przy rozliczeniu.
    const std::string kind = kontrakt_kind_or_default(ev.str("kind"));
    db_.upsert_contract(contract_id, faction, kind, "open", ev.data.dump());
    std::cout << "[brain] kontrakt " << contract_id << " (" << faction << ", " << kind
              << ") wystawiony w grze\n";

    // Głos frakcji: ogłoszenie zlecenia. {oferta} podstawia opis z moda (co i za ile).
    const std::string opis = ev.str("opis");
    const std::string reward = ev.str("reward_str");
    emit(out, faction, render_first(faction, {"oferta", "neutral"}, {{"oferta", opis},
                                                                    {"kwota", reward}}),
         0, cfg, now_ms, "oferta",
         "Wystawiacie właśnie zlecenie dla gracza: " + opis +
             (reward.empty() ? "" : " Nagroda: " + reward + " kredytów.") +
             " Ogłoś to krótko przez radio po swojemu.");
}

void Engine::handle_contract_taken(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                   std::vector<RadioOut>& out) {
    const std::string contract_id = ev.str("contract_id");
    const std::string faction = contract_faction(ev, contract_id);
    if (faction.empty()) {
        std::cerr << "[brain] contract_taken bez frakcji i bez znanego ID (" << contract_id
                  << ") — pomijam\n";
        return;
    }
    // Callback przyjęcia potrafi przyjść dwa razy (np. mod odtwarza stan po wczytaniu
    // świata) — a kary u wrogów mają zaboleć raz. Status w bazie jest tu bezpiecznikiem.
    if (!contract_id.empty() && db_.get_contract_status(contract_id) == "taken") {
        return;
    }
    ensure_known_faction(faction);
    if (!contract_id.empty()) {
        db_.set_contract_status(contract_id, "taken");
    }

    const std::string kind = kontrakt_kind_or_default(
        contract_id.empty() ? ev.str("kind") : db_.get_contract_kind(contract_id));
    std::cout << "[brain] kontrakt " << contract_id << " (" << faction << ", " << kind
              << ") przyjęty przez gracza\n";

    // Tu stał spawn konwoju dla zlecenia "eskorta" (usuniętego 2026-08-09 razem z typem —
    // gra nie ma definicji ContractTypeEscort, więc gałąź nie mogła się wykonać ani razu).
    // Sam POMYSŁ był dobry i warto go kiedyś wskrzesić na innym typie: konwój wyrusza
    // dopiero, gdy gracz przyjmie zlecenie, a nie przy jego wystawieniu — inaczej statki
    // krążą bez celu przy robocie, której gracz nawet nie zobaczył. Kod w historii gita.

    // Cel nagrody za głowę dowiaduje się, że ktoś na niego poluje, i wysyła ochronę.
    std::string target;
    if (kind == "nagroda" && !contract_id.empty()) {
        const std::string payload = db_.get_contract_payload(contract_id);
        if (!payload.empty()) {
            const auto parsed = nlohmann::json::parse(payload, nullptr, /*allow_exceptions=*/false);
            if (parsed.is_object() && parsed.contains("target") && parsed["target"].is_string()) {
                target = parsed["target"].get<std::string>();
            }
        }
        if (!target.empty()) {
            request_spawn(target, "patrol", cfg, now_ms,
                          "Ochrona frakcji " + target + " — ktoś przyjął nagrodę za jej głowy.");
        }
    }

    // Świat mściwy nie zapomina, po czyjej stronie stanąłeś: każdy WRÓG wystawcy
    // traci do gracza zaufanie. To jedyna kara — sam fakt przyjęcia roboty.
    if (cfg.kontrakt_przyjety_u_wroga != 0) {
        for (const FactionRow& other : db_.list_factions()) {
            if (other.tag == faction || other.tag == kPlayer) {
                continue;
            }
            if (db_.get_relation(faction, other.tag).value > cfg.prog_wrogi) {
                continue; // nie są wrogami wystawcy — nic im do tego
            }
            shift_player_relation(other.tag, cfg.kontrakt_przyjety_u_wroga,
                                  "za przyjęcie zlecenia od " + faction);
            db_.add_memory(now_ms, other.tag, "zlecenie_wroga", 0,
                           "Gracz przyjął zlecenie od " + faction + ", z którym jesteśmy na wojennej stopie.");
            update_state(other.tag, cfg, now_ms, out);
        }
    }

    db_.add_memory(now_ms, faction, "zlecenie_przyjete", 0,
                   "Gracz przyjął nasze zlecenie (" + kind + ").");
    emit(out, faction, render_first(faction, {"zlecenie_przyjete", "neutral"}), 0, cfg, now_ms,
         "zlecenie_przyjete",
         "Gracz właśnie przyjął wasze zlecenie (" + kind +
             "). Potwierdź to krótko przez radio po swojemu — bez dziękowania z góry.");
}

void Engine::handle_contract_done(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                  std::vector<RadioOut>& out) {
    const std::string contract_id = ev.str("contract_id");
    const std::string faction = contract_faction(ev, contract_id);
    if (faction.empty()) {
        std::cerr << "[brain] contract_done bez frakcji i bez znanego ID (" << contract_id
                  << ") — pomijam\n";
        return;
    }
    ensure_known_faction(faction);

    const bool success = ev.flag("success", true);
    if (!contract_id.empty()) {
        db_.set_contract_status(contract_id, success ? "done" : "failed");
    }

    // Trudniejszy typ = większe odkupienie i większa kara. Typ bierzemy z bazy: mod
    // przysyła tylko ID i wynik, a po wczytaniu świata nie pamięta nawet frakcji.
    const std::string kind = kontrakt_kind_or_default(db_.get_contract_kind(contract_id));
    const double mult = contract_kind_multiplier(cfg, kind);
    const double delta = (success ? cfg.kontrakt_max : -cfg.kontrakt_min) * mult;
    std::ostringstream why;
    why << (success ? "za wykonany kontrakt (" : "za zawalony kontrakt (") << kind << ", mnożnik "
        << mult << ")";
    shift_player_relation(faction, delta, why.str());
    db_.add_memory(now_ms, faction, success ? "kontrakt_ok" : "kontrakt_fail", 0,
                   success ? "Gracz wykonał kontrakt dla " + faction + "."
                           : "Gracz zawalił kontrakt dla " + faction + ".");
    update_state(faction, cfg, now_ms, out);

    if (success) {
        emit(out, faction, render_first(faction, {"neutral"}), 0, cfg, now_ms, "neutral",
             "Gracz właśnie wykonał dla was kontrakt. Podziękuj mu po swojemu.");
    } else {
        emit(out, faction, render_first(faction, {"zal", "kpina", "neutral"}), 0, cfg, now_ms, "zal",
             "Gracz zawalił wasz kontrakt. Wyraź niezadowolenie po swojemu.");
    }
}

void Engine::maybe_offer_contract(const std::string& faction, const Config& cfg,
                                  std::int64_t now_ms, std::vector<RadioOut>& out) {
    (void)out; // radio leci dopiero przy contract_created (gdy wiemy, że kontrakt istnieje)
    if (!cfg.kontrakty_wlaczone || !is_own_faction(faction)) {
        return;
    }
    // Wrogowie nie dają roboty. Próg jest wyżej niż wojna (-60), więc frakcja w wojnie
    // musi najpierw wyjść na prostą (okup/de-eskalacja), a dopiero potem odrabiać czynami.
    const double value = db_.get_relation(faction, kPlayer).value;
    if (value < cfg.kontrakty_prog_relacji) {
        return;
    }
    if (db_.count_open_contracts(faction) >= cfg.kontrakty_max_otwartych) {
        return;
    }
    if (on_cooldown(contract_key(faction), minutes_ms(cfg.kontrakty_cooldown_min), now_ms)) {
        return;
    }
    db_.set_kv(contract_key(faction), now_ms);

    const std::string kind = pick_contract_kind(faction, cfg);
    if (kind.empty()) {
        std::cerr << "[brain] kontrakt: " << faction
                  << " nie ma ani jednego typu zlecenia z wagą > 0 ([kontrakty.typy])\n";
        return;
    }
    queue_contract(faction, kind, value, cfg, now_ms);
}

std::string Engine::worst_enemy_of(const std::string& faction, const Config& cfg) const {
    std::string worst;
    double worst_value = 0;
    for (const FactionRow& other : db_.list_factions()) {
        if (other.tag == faction || other.tag == kPlayer) {
            continue;
        }
        const double value = db_.get_relation(faction, other.tag).value;
        if (value < worst_value) {
            worst_value = value;
            worst = other.tag;
        }
    }
    // Nagroda za głowę kogoś, z kim jesteśmy tylko chłodno, nie ma sensu — a bez tej
    // bramki HEL zlecałby zabijanie WGR (polityka +10) przy pierwszym losowaniu.
    return worst_value <= cfg.prog_wrogi ? worst : std::string{};
}

std::string Engine::pick_contract_kind(const std::string& faction, const Config& cfg) {
    const std::string state = faction_state(faction);
    // Nagroda za głowę wymaga celu; bez wroga w polityce typ w ogóle nie wchodzi do
    // losowania (inaczej mod dostawałby zlecenie, które i tak musi zamienić na dostawę).
    const bool bounty_possible = !worst_enemy_of(faction, cfg).empty();

    std::vector<std::pair<std::string, double>> pool;
    double total = 0;
    for (const std::string& kind : Config::contract_kinds()) {
        if (kind == "nagroda" && !bounty_possible) {
            continue;
        }
        const double weight = contract_kind_weight(cfg, faction, kind, state);
        if (weight <= 0) {
            continue;
        }
        total += weight;
        pool.emplace_back(kind, weight);
    }
    if (pool.empty()) {
        return {};
    }

    std::uniform_real_distribution<double> dist(0.0, total);
    double roll = dist(rng_);
    for (const auto& [kind, weight] : pool) {
        roll -= weight;
        if (roll <= 0) {
            return kind;
        }
    }
    return pool.back().first;  // zaokrąglenia double — ostatni z puli
}

void Engine::queue_contract(const std::string& faction, const std::string& kind, double relation,
                            const Config& cfg, std::int64_t now_ms) {
    // Nagroda skalowana relacją: im lepiej was widzą, tym lepiej płatna robota.
    const double span = 100.0 - cfg.kontrakty_prog_relacji;
    const double t = span > 0 ? std::clamp((relation - cfg.kontrakty_prog_relacji) / span, 0.0, 1.0)
                              : 0.0;
    const double mult = contract_kind_multiplier(cfg, kind);
    const auto reward = static_cast<std::int64_t>(
        lerp(cfg.kontrakty_nagroda_min, cfg.kontrakty_nagroda_max, t) * mult);

    const std::string target = kind == "nagroda" ? worst_enemy_of(faction, cfg) : std::string{};

    std::cout << "[brain] kontrakt: " << faction << " wystawia zlecenie (" << kind << ") za "
              << reward << " kr (relacja " << format_value(relation) << ", mnożnik " << mult
              << (target.empty() ? "" : ", cel " + target) << ")\n";
    (void)now_ms;
    pending_contracts_.push_back({faction, kind, reward, cfg.kontrakty_czas_min, target});
    // Konwój do eskorty NIE powstaje tutaj — dopiero gdy gracz przyjmie zlecenie
    // (handle_contract_taken). Inaczej statki krążyły bez celu przy każdym zleceniu,
    // którego gracz nawet nie zobaczył.
}

bool Engine::chat_expects_decision(const std::string& faction, std::int64_t now_ms) const {
    if (has_active_raid(faction, now_ms)) {
        return true;
    }
    const std::string state = faction_state(faction);
    return state == "napiecie" || state == "wojna";
}

void Engine::apply_deescalation(const std::string& faction, const Config& cfg,
                                std::int64_t now_ms, std::int64_t amount) {
    if (!has_active_raid(faction, now_ms)) {
        return; // nie ma aktywnego rajdu — nie ma czego odwoływać
    }
    set_active_raid(faction, 0);
    db_.clear_pending_ransom(faction); // łaska nadrzędna nad wiszącym okupem surowcowym (mod sprząta skrzynkę)
    const double value = db_.adjust_relation(faction, kPlayer, cfg.deeskalacja_bonus);
    const std::string kwota = amount > 0 ? " (okup " + std::to_string(amount) + " kr)" : "";
    db_.add_memory(now_ms, faction, "deeskalacja", 1,
                   "Frakcja " + faction + " przyjęła propozycję gracza i odwołała atak" + kwota + ".");
    std::cout << "[brain] deeskalacja " << faction << ": przyjęto (relacja "
              << format_value(cfg.deeskalacja_bonus) << " => " << format_value(value)
              << ")" << kwota << ", stand_down\n";
    pending_standdowns_.push_back({faction, amount});
}

std::int64_t Engine::cash_ransom_threshold(const std::string& faction, const Config& cfg) const {
    if (cfg.deeskalacja_prog_kredyty <= 0) {
        return 0; // bramka wyłączona configiem — o pokoju decyduje wyłącznie model
    }
    const double value = db_.get_relation(faction, kPlayer).value;
    const double mnoznik = 1.0 + std::max(0.0, -value) * cfg.deeskalacja_prog_za_punkt;
    return static_cast<std::int64_t>(static_cast<double>(cfg.deeskalacja_prog_kredyty) * mnoznik);
}

std::int64_t parse_ransom_amount(const std::string& text) {
    for (std::size_t i = 0; i < text.size();) {
        if (text[i] >= '0' && text[i] <= '9') {
            std::size_t j = i;
            while (j < text.size() && text[j] >= '0' && text[j] <= '9') {
                ++j;
            }
            if (j - i >= 2 && j - i <= 12) { // ignoruj pojedyncze cyfry i absurdy (overflow)
                std::int64_t val = 0;
                for (std::size_t k = i; k < j; ++k) {
                    val = val * 10 + (text[k] - '0');
                }
                return val;
            }
            i = j;
        } else {
            ++i;
        }
    }
    return 0;
}

OcenaOkupu ocen_oferte_okupu(std::int64_t oferta, std::int64_t prog, std::int64_t saldo) {
    if (prog <= 0 || saldo < 0) {
        // Bramka wyłączona albo saldo nieznane — nie kupujemy obietnic, których nie da
        // się sprawdzić (mod dosyła saldo w chat_message; -1 = nie wie).
        return OcenaOkupu::Brak;
    }
    if (oferta < prog) {
        return OcenaOkupu::ZaMalo;
    }
    return saldo >= oferta ? OcenaOkupu::Wiazaca : OcenaOkupu::BezPokrycia;
}

OcenaOkupu Engine::on_llm_reply(const LlmReply& reply, const Config& cfg, std::int64_t now_ms) {
    const std::int64_t oferta = parse_ransom_amount(reply.player_msg);
    const std::int64_t prog = cash_ransom_threshold(reply.faction, cfg);
    const OcenaOkupu ocena = ocen_oferte_okupu(oferta, prog, player_balance_);
    const bool wiazaca = ocena == OcenaOkupu::Wiazaca;
    if (wiazaca) {
        std::cout << "[brain] okup kredytowy " << reply.faction << ": oferta " << oferta
                  << " kr >= próg " << prog << " kr (saldo " << player_balance_
                  << ") — pokój niezależnie od decyzji modelu\n";
    } else if (ocena == OcenaOkupu::BezPokrycia) {
        std::cout << "[brain] okup kredytowy " << reply.faction << ": oferta " << oferta
                  << " kr bez pokrycia (saldo " << player_balance_ << ") — pusta obietnica\n";
    }

    if (reply.demand_goods && !wiazaca) {
        request_goods_ransom(reply.faction, cfg, now_ms);
    } else if (reply.deescalate || wiazaca) {
        apply_deescalation(reply.faction, cfg, now_ms, oferta);
    }
    return ocena;
}

std::vector<std::pair<std::string, std::int64_t>> Engine::take_standdowns() {
    return take(pending_standdowns_);
}

void Engine::request_goods_ransom(const std::string& faction, const Config& cfg,
                                  std::int64_t now_ms) {
    if (!has_active_raid(faction, now_ms)) {
        return; // brak aktywnego rajdu — nie ma pod co żądać trybutu
    }
    if (active_ransom(faction, now_ms)) {
        return; // okup już wisi — nie dubluj skrzynki zrzutu
    }
    const std::vector<std::string> items = split_items(cfg.okup_towary);
    if (items.empty()) {
        std::cerr << "[brain] okup surowcowy: pusta lista [okup_surowce].towary — pomijam\n";
        return;
    }
    std::uniform_int_distribution<std::size_t> pick_item(0, items.size() - 1);
    const std::string item = items[pick_item(rng_)];

    const int lo = std::min(cfg.okup_ilosc_min, cfg.okup_ilosc_max);
    const int hi = std::max(cfg.okup_ilosc_min, cfg.okup_ilosc_max);
    std::uniform_int_distribution<int> pick_amount(lo, hi);
    // Zaokrąglij do 50, żeby żądanie brzmiało jak okrągła liczba ("500", nie "473").
    std::int64_t amount = ((static_cast<std::int64_t>(pick_amount(rng_)) + 25) / 50) * 50;
    if (amount < 50) {
        amount = 50;
    }

    db_.set_pending_ransom(faction, PendingRansom{item, amount,
                                                  now_ms + static_cast<std::int64_t>(cfg.okup_deadline_s) * 1000});
    pending_ransom_demands_.push_back({faction, item, amount, cfg.okup_deadline_s});
    db_.add_memory(now_ms, faction, "okup_surowce_zadanie", 1,
                   "Frakcja " + faction + " zażądała od gracza trybutu: " + std::to_string(amount) +
                   " x " + item + " do skrzynki zrzutu.");
    std::cout << "[brain] okup surowcowy " << faction << ": żądanie " << amount << "x " << item
              << " (deadline " << cfg.okup_deadline_s << " s), ransom_demand\n";
}

std::vector<RansomDemandOut> Engine::take_ransom_demands() {
    return take(pending_ransom_demands_);
}

void Engine::handle_ransom_paid(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);
    db_.clear_pending_ransom(faction);
    set_active_raid(faction, 0); // trybut dostarczony — rajd odwołany (mod despawnuje statki)

    const std::string item = ev.str("item");
    const std::int64_t amount = static_cast<std::int64_t>(ev.num("amount"));
    const double rel = db_.adjust_relation(faction, kPlayer, cfg.okup_bonus_dostawa);

    // Odkup CZYNEM (nie czasem): udana dostawa zmniejsza trwałą nieufność świata mściwego.
    const int broken = db_.ransom_broken(faction);
    if (broken > 0) {
        db_.set_ransom_broken(faction, broken - 1);
    }

    const std::string co = amount > 0 && !item.empty()
                               ? " (" + std::to_string(amount) + " x " + item + ")"
                               : "";
    db_.add_memory(now_ms, faction, "okup_surowce_oplacony", 1,
                   "Gracz dostarczył frakcji " + faction + " żądany trybut" + co + " — atak odwołany.");
    std::cout << "[brain] okup surowcowy " << faction << ": OPŁACONY" << co << " (relacja "
              << format_value(cfg.okup_bonus_dostawa) << " => " << format_value(rel) << ")\n";
    update_state(faction, cfg, now_ms, out);
    emit(out, faction, render_first(faction, {"neutral"}), 0, cfg, now_ms, "neutral",
         "Gracz dostarczył wam żądany trybut w surowcach i odkupił się. Potwierdź zawieszenie broni po swojemu.");
}

void Engine::handle_ransom_expired(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                   std::vector<RadioOut>& out) {
    const std::string faction = ev.str("faction");
    if (faction.empty()) {
        return;
    }
    ensure_known_faction(faction);
    db_.clear_pending_ransom(faction);
    // flaga rajdu w SQLite zostaje — ataki trwają dalej (mod wznawia ogień statków rajdu).

    // Mod zgłasza powód. "brak_skrzynki" = to skrzynka przepadła (sprzątacz śmieci SE),
    // a nie gracz zawiódł — kasujemy żądanie bez kary i bez nieufności. Karanie za własny
    // brak skrzynki byłoby najgorszą wersją świata mściwego.
    if (ev.str("reason") == "brak_skrzynki") {
        std::cout << "[brain] okup surowcowy " << faction
                  << ": skrzynka zrzutu przepadła — żądanie anulowane BEZ kary\n";
        return;
    }

    const int broken = db_.ransom_broken(faction) + 1;
    db_.set_ransom_broken(faction, broken); // trwały modyfikator: kolejne okupy trudniejsze/odrzucane
    const double rel = db_.adjust_relation(faction, kPlayer, -cfg.okup_kara_zlamanie);

    db_.add_memory(now_ms, faction, "okup_surowce_zlamany", 2,
                   "Gracz obiecał okup frakcji " + faction +
                   ", ale nie dostarczył trybutu na czas — złamał słowo.");
    std::cout << "[brain] okup surowcowy " << faction << ": ZŁAMANY (nieufność=" << broken
              << ", relacja " << format_value(-cfg.okup_kara_zlamanie) << " => " << format_value(rel)
              << "), ataki trwają\n";
    update_state(faction, cfg, now_ms, out);
    emit(out, faction, render_first(faction, {"grozba", "kpina", "neutral"}, {{"sekundy", "30"}}),
         1, cfg, now_ms, "grozba",
         "Gracz obiecał wam trybut i nie dostarczył go na czas — oszukał was. Zareaguj gniewem i nie odpuszczaj.");
}

void Engine::handle_session_start(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                  std::vector<RadioOut>& out) {
    (void)ev;
    (void)cfg;
    (void)now_ms;
    (void)out;
    const int cleared = db_.clear_all_pending_ransoms();
    if (cleared > 0) {
        std::cout << "[brain] nowa sesja świata: " << cleared
                  << " wiszące żądanie(a) trybutu wygaszone (mod nie pamięta skrzynek)\n";
    }
}

void Engine::handle_debug(const Event& ev, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    const std::string cmd = ev.str("cmd");
    if (cmd == "rel") {
        out.push_back(system_msg(relations_report()));
    } else if (cmd == "tick") {
        std::vector<RadioOut> tick_out = tick(cfg, now_ms, /*force=*/true);
        out.push_back(system_msg("Tick wymuszony."));
        out.insert(out.end(), tick_out.begin(), tick_out.end());
    } else if (cmd == "spawn") {
        debug_spawn(ev, cfg, now_ms, out);
    } else if (cmd == "kontrakt") {
        debug_contract(ev, cfg, now_ms, out);
    } else if (cmd == "okup") {
        debug_ransom(ev, cfg, now_ms, out);
    } else if (cmd == "okup-surowce") {
        debug_goods_ransom(ev, cfg, now_ms, out);
    }
}

void Engine::debug_spawn(const Event& ev, const Config& cfg, std::int64_t now_ms,
                         std::vector<RadioOut>& out) {
    // /zf raid <frakcja> [kind]: wymuszony spawn do testu potoku (omija cooldown).
    const std::string faction = ev.str("faction");
    const auto row = find_faction(faction);
    if (!row) {
        out.push_back(system_msg("Nie znam frakcji \"" + faction + "\" — spawn pominięty."));
        return;
    }
    std::string kind = ev.str("kind");
    if (kind.empty()) {
        kind = kind_for_state(row->state);
    }
    request_spawn(faction, kind, cfg, now_ms, "Ręcznie wywołany spawn (/zf raid).", /*force=*/true);
    out.push_back(system_msg("Spawn zlecony: " + faction + " (" + kind + ")."));
}

void Engine::debug_contract(const Event& ev, const Config& cfg, std::int64_t now_ms,
                            std::vector<RadioOut>& out) {
    // /zf kontrakt <frakcja>: wymuszona oferta (omija cooldown i limit otwartych) —
    // test potoku kontraktów bez czekania na tick i bez zabawy relacjami.
    const std::string faction = ev.str("faction");
    if (!is_own_faction(faction)) {
        out.push_back(system_msg("Kontrakty wystawiają tylko HEL/KRW/WGR (nie \"" + faction + "\")."));
        return;
    }
    // Opcjonalny typ: "/zf kontrakt KRW nagroda" testuje konkretną klasę kontraktu
    // bez czekania na losowanie. Bez typu — normalne losowanie wagami.
    std::string kind = ev.str("kind");
    if (!kind.empty()) {
        if (!is_known_contract_kind(kind)) {
            std::string known;
            for (const std::string& k : Config::contract_kinds()) {
                known += (known.empty() ? "" : ", ") + k;
            }
            out.push_back(system_msg("Nieznany typ zlecenia \"" + kind + "\". Znane: " + known + "."));
            return;
        }
        if (kind == "nagroda" && worst_enemy_of(faction, cfg).empty()) {
            out.push_back(system_msg("Frakcja " + faction +
                                     " nie ma wroga w polityce — nagroda za głowę nie ma celu."));
            return;
        }
    } else {
        kind = pick_contract_kind(faction, cfg);
        if (kind.empty()) {
            out.push_back(system_msg("Żaden typ zlecenia nie ma wagi > 0 ([kontrakty.typy])."));
            return;
        }
    }
    // Relacja = próg: wymuszone zlecenie jest najtańsze z widełek, żeby test nie
    // zależał od stanu relacji (a mnożnik typu i tak jest widoczny w kwocie).
    queue_contract(faction, kind, cfg.kontrakty_prog_relacji, cfg, now_ms);
    db_.set_kv(contract_key(faction), now_ms);
    out.push_back(system_msg("Zlecenie " + faction + " (" + kind + ") — wystawiam."));
}

void Engine::debug_ransom(const Event& ev, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out) {
    // /zf okup <frakcja>: deterministyczny wyzwalacz de-eskalacji — niezależny od
    // LLM (qwen-3B bywa za słaby, by sam trafnie odpuścić). Odwołuje aktywny rajd.
    const std::string faction = ev.str("faction");
    if (!has_active_raid(faction, now_ms)) {
        out.push_back(system_msg("Frakcja " + faction + " nie prowadzi rajdu — nie ma czego odwołać."));
        return;
    }
    push_template_voice(out, faction);
    apply_deescalation(faction, cfg, now_ms);
    out.push_back(system_msg("Okup przyjęty: " + faction + " odwołuje rajd."));
}

void Engine::debug_goods_ransom(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                std::vector<RadioOut>& out) {
    // /zf okup-surowce <frakcja>: deterministyczny wyzwalacz żądania trybutu (B+) —
    // niezależny od LLM. Wymaga aktywnego rajdu (jak /zf okup).
    const std::string faction = ev.str("faction");
    if (!has_active_raid(faction, now_ms)) {
        out.push_back(
            system_msg("Frakcja " + faction + " nie prowadzi rajdu — nie ma pod co żądać trybutu."));
        return;
    }
    if (active_ransom(faction, now_ms)) {
        out.push_back(system_msg("Frakcja " + faction + " już wystawiła żądanie trybutu."));
        return;
    }
    push_template_voice(out, faction);
    request_goods_ransom(faction, cfg, now_ms);
    out.push_back(system_msg("Żądanie trybutu wystawione: " + faction + "."));
}

std::vector<RadioOut> Engine::tick(const Config& cfg, std::int64_t now_ms, bool force) {
    std::vector<RadioOut> out;

    if (!force && on_cooldown(kLastTickKey, minutes_ms(cfg.tick_co_minut), now_ms)) {
        return out;
    }
    db_.set_kv(kLastTickKey, now_ms);

    // 1) Dryf: relacje wolno wracają do 0 (dryf_pkt na dryf_co_minut).
    const std::int64_t drift_period_ms = minutes_ms(cfg.dryf_co_minut);
    std::int64_t last_drift = db_.get_kv(kLastDriftKey);
    if (last_drift == 0) {
        last_drift = now_ms; // pierwszy tick świata — dryf liczymy od teraz
        db_.set_kv(kLastDriftKey, last_drift);
    }
    const std::int64_t steps = drift_period_ms > 0 ? (now_ms - last_drift) / drift_period_ms : 0;
    if (steps > 0) {
        for (const auto& [a, b] : db_.list_relation_pairs()) {
            // Dryf dotyczy WYŁĄCZNIE stosunku do gracza: uraza do niego blednie z czasem,
            // ale wojna Helionu z piratami nie kończy się sama dlatego, że minął tydzień.
            // Politykę frakcji zmieniają zdarzenia, nie zegar.
            if (b != kPlayer) {
                continue;
            }
            const double value = db_.get_relation(a, b).value;
            const double magnitude = std::min(std::abs(value), cfg.dryf_pkt * static_cast<double>(steps));
            if (magnitude > 0) {
                db_.adjust_relation(a, b, value > 0 ? -magnitude : magnitude);
            }
        }
        db_.set_kv(kLastDriftKey, last_drift + steps * drift_period_ms);
        std::cout << "[brain] dryf relacji: " << steps << " krok(ów)\n";
    }

    // 2) Maszyna stanów + odnowienie budżetu akcji + ewentualne zlecenie.
    for (const FactionRow& row : db_.list_factions()) {
        update_state(row.tag, cfg, now_ms, out);
        db_.set_faction_budget(row.tag, cfg.budzet_akcji_na_tick);
        // Kontrakty to jedyna szybka droga odkupienia (dryf to 1 pkt / 2 h), więc
        // oferta idzie każdym tickiem, w którym frakcja nie jest wroga i nie ma
        // jeszcze otwartego zlecenia — bramkuje ją cooldown, nie los.
        maybe_offer_contract(row.tag, cfg, now_ms, out);
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

    // Dryf zmienia relacje bez żadnego zdarzenia z gry — bez tego okno frakcji
    // zamarłoby na wartości z ostatniej strzelaniny.
    sync_reputations(cfg, /*force=*/false);
    sync_prices(cfg, /*force=*/false);

    return out;
}

std::string Engine::politics_report() const {
    std::string report;
    const std::vector<FactionRow> rows = db_.list_factions();
    for (std::size_t i = 0; i < rows.size(); ++i) {
        for (std::size_t j = i + 1; j < rows.size(); ++j) {
            const RelationRow rel = db_.get_relation(rows[i].tag, rows[j].tag);
            if (rel.value == 0) {
                continue; // nieznajome frakcje nie zaśmiecają raportu
            }
            if (!report.empty()) {
                report += " | ";
            }
            report += rows[i].tag + "/" + rows[j].tag + " " + format_value(rel.value);
        }
    }
    return report.empty() ? "brak" : report;
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
    if (report.empty()) {
        return "Brak frakcji w bazie.";
    }
    return report + " || polityka: " + politics_report();
}

} // namespace zf
