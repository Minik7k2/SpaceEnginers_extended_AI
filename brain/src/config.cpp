#include "config.hpp"

#include <algorithm>
#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <vector>

#include <toml++/toml.hpp>

namespace zf {

namespace {

// Typo w nazwie typu kontraktu jest ciche i kosztowne: waga wyląduje w mapie, ale
// nigdy nie zostanie użyta, a frakcja bez ostrzeżenia wystawia same dostawy. Stąd
// twardy błąd configu zamiast ignorowania nieznanego klucza.
void require_contract_kind(const std::string& kind, const std::string& where) {
    const auto& kinds = Config::contract_kinds();
    if (std::find(kinds.begin(), kinds.end(), kind) != kinds.end()) {
        return;
    }
    std::string known;
    for (const std::string& k : kinds) {
        known += (known.empty() ? "" : ", ") + k;
    }
    throw std::runtime_error("nieznany typ kontraktu w " + where + ": \"" + kind +
                             "\" (znane: " + known + ")");
}

// Wagi używane, gdy config w ogóle nie wspomina o danym typie: dostawa i transport
// są chlebem powszednim, reszta rzadsza. Bez tego brak [kontrakty.typy] oznaczałby
// zerowe wagi wszędzie i ani jednego kontraktu.
double builtin_weight(const std::string& kind) {
    if (kind == "dostawa") {
        return 3;
    }
    if (kind == "transport") {
        return 2;
    }
    return 1;
}

// Nakłada wartości z tabeli na config — tylko klucze obecne w pliku nadpisują
// dotychczasowe wartości, dzięki czemu rules.local.toml może zawierać sam
// [bridge].storage_dir.
void apply_table(const toml::table& tbl, Config& cfg) {
    // Uwaga: at() rzuca przy braku klucza, przez co value_or nigdy by nie zadziałało —
    // dla kluczy opcjonalnych używamy operator[], defaulty z value_or są wtedy realne.
    if (const auto* bridge = tbl["bridge"].as_table()) {
        cfg.storage_dir = (*bridge)["storage_dir"].value_or(cfg.storage_dir);
        cfg.poll_ms = (*bridge)["poll_ms"].value_or(cfg.poll_ms);
        cfg.db_path = (*bridge)["db_path"].value_or(cfg.db_path);
        cfg.db_per_swiat = (*bridge)["db_per_swiat"].value_or(cfg.db_per_swiat);
        cfg.rotate_bytes = (*bridge)["rotate_bytes"].value_or(cfg.rotate_bytes);
    }

    if (const auto* llm = tbl["llm"].as_table()) {
        cfg.llm_model_path = (*llm)["model_path"].value_or(cfg.llm_model_path);
        cfg.llm_use_gpu = (*llm)["use_gpu"].value_or(cfg.llm_use_gpu);
        cfg.llm_max_chars = (*llm)["max_chars"].value_or(cfg.llm_max_chars);
        cfg.llm_threads = (*llm)["threads"].value_or(cfg.llm_threads);
    }

    if (const auto* rel = tbl["relacje"].as_table()) {
        cfg.prog_sojusznik = (*rel)["prog_sojusznik"].value_or(cfg.prog_sojusznik);
        cfg.prog_wrogi = (*rel)["prog_wrogi"].value_or(cfg.prog_wrogi);
        cfg.prog_wojna = (*rel)["prog_wojna"].value_or(cfg.prog_wojna);
        cfg.histereza_wyjscie_z_wojny =
            (*rel)["histereza_wyjscie_z_wojny"].value_or(cfg.histereza_wyjscie_z_wojny);
        cfg.dryf_pkt = (*rel)["dryf_pkt"].value_or(cfg.dryf_pkt);
        cfg.dryf_co_minut = (*rel)["dryf_co_minut"].value_or(cfg.dryf_co_minut);
    }

    if (const auto* zm = tbl["zmiany"].as_table()) {
        cfg.ostrzal_min = (*zm)["ostrzal_min"].value_or(cfg.ostrzal_min);
        cfg.ostrzal_max = (*zm)["ostrzal_max"].value_or(cfg.ostrzal_max);
        cfg.zniszczenie_statku = (*zm)["zniszczenie_statku"].value_or(cfg.zniszczenie_statku);
        cfg.zniszczenie_stacji = (*zm)["zniszczenie_stacji"].value_or(cfg.zniszczenie_stacji);
        cfg.sufit_po_zniszczeniu_stacji =
            (*zm)["sufit_po_zniszczeniu_stacji"].value_or(cfg.sufit_po_zniszczeniu_stacji);
        cfg.handel_min = (*zm)["handel_min"].value_or(cfg.handel_min);
        cfg.handel_max = (*zm)["handel_max"].value_or(cfg.handel_max);
        cfg.kontrakt_min = (*zm)["kontrakt_min"].value_or(cfg.kontrakt_min);
        cfg.kontrakt_max = (*zm)["kontrakt_max"].value_or(cfg.kontrakt_max);
        cfg.kontrakt_przyjety_u_wroga =
            (*zm)["kontrakt_przyjety_u_wroga"].value_or(cfg.kontrakt_przyjety_u_wroga);
        cfg.atak_na_wroga_bonus = (*zm)["atak_na_wroga_bonus"].value_or(cfg.atak_na_wroga_bonus);
        cfg.atak_na_wroga_cooldown_min =
            (*zm)["atak_na_wroga_cooldown_min"].value_or(cfg.atak_na_wroga_cooldown_min);
        cfg.deeskalacja_bonus = (*zm)["deeskalacja_bonus"].value_or(cfg.deeskalacja_bonus);
        cfg.deeskalacja_prog_kredyty =
            (*zm)["deeskalacja_prog_kredyty"].value_or(cfg.deeskalacja_prog_kredyty);
        cfg.deeskalacja_prog_za_punkt =
            (*zm)["deeskalacja_prog_za_punkt"].value_or(cfg.deeskalacja_prog_za_punkt);
    }

    if (const auto* tick = tbl["tick"].as_table()) {
        cfg.tick_co_minut = (*tick)["co_minut"].value_or(cfg.tick_co_minut);
        cfg.szansa_zdarzenia_losowego =
            (*tick)["szansa_zdarzenia_losowego"].value_or(cfg.szansa_zdarzenia_losowego);
        cfg.budzet_akcji_na_tick = (*tick)["budzet_akcji_na_tick"].value_or(cfg.budzet_akcji_na_tick);
    }

    if (const auto* radio = tbl["radio"].as_table()) {
        cfg.radio_limit_na_frakcje_na_min =
            (*radio)["limit_na_frakcje_na_min"].value_or(cfg.radio_limit_na_frakcje_na_min);
        cfg.radio_ttl_sekund = (*radio)["ttl_sekund"].value_or(cfg.radio_ttl_sekund);
        cfg.radio_wymagaj_zasiegu =
            (*radio)["wymagaj_zasiegu"].value_or(cfg.radio_wymagaj_zasiegu);
    }

    if (const auto* spawn = tbl["spawn"].as_table()) {
        cfg.spawn_wlaczone = (*spawn)["wlaczone"].value_or(cfg.spawn_wlaczone);
        cfg.spawn_cooldown_min = (*spawn)["cooldown_min"].value_or(cfg.spawn_cooldown_min);
    }

    if (const auto* okup = tbl["okup_surowce"].as_table()) {
        cfg.okup_towary = (*okup)["towary"].value_or(cfg.okup_towary);
        cfg.okup_ilosc_min = (*okup)["ilosc_min"].value_or(cfg.okup_ilosc_min);
        cfg.okup_ilosc_max = (*okup)["ilosc_max"].value_or(cfg.okup_ilosc_max);
        cfg.okup_deadline_s = (*okup)["deadline_s"].value_or(cfg.okup_deadline_s);
        cfg.okup_bonus_dostawa = (*okup)["bonus_dostawa"].value_or(cfg.okup_bonus_dostawa);
        cfg.okup_kara_zlamanie = (*okup)["kara_zlamanie"].value_or(cfg.okup_kara_zlamanie);
    }

    if (const auto* kon = tbl["kontrakty"].as_table()) {
        cfg.kontrakty_wlaczone = (*kon)["wlaczone"].value_or(cfg.kontrakty_wlaczone);
        cfg.kontrakty_prog_relacji = (*kon)["prog_relacji"].value_or(cfg.kontrakty_prog_relacji);
        cfg.kontrakty_cooldown_min = (*kon)["cooldown_min"].value_or(cfg.kontrakty_cooldown_min);
        cfg.kontrakty_max_otwartych = (*kon)["max_otwartych"].value_or(cfg.kontrakty_max_otwartych);
        cfg.kontrakty_nagroda_min = (*kon)["nagroda_min"].value_or(cfg.kontrakty_nagroda_min);
        cfg.kontrakty_nagroda_max = (*kon)["nagroda_max"].value_or(cfg.kontrakty_nagroda_max);
        cfg.kontrakty_czas_min = (*kon)["czas_min"].value_or(cfg.kontrakty_czas_min);
        cfg.kontrakty_mnoznik_nagrody_w_napieciu =
            (*kon)["mnoznik_nagrody_w_napieciu"].value_or(cfg.kontrakty_mnoznik_nagrody_w_napieciu);

        // [kontrakty.typy]: liczby = wagi domyślne, podtabele = nadpisania per frakcja
        // ([kontrakty.typy.KRW]). Scalamy klucz po kluczu, żeby rules.local.toml mógł
        // przestawić jedną wagę bez przepisywania całej tabeli.
        if (const auto* typy = (*kon)["typy"].as_table()) {
            for (const auto& [key, node] : *typy) {
                const std::string name(key.str());
                if (const auto* per_faction = node.as_table()) {
                    for (const auto& [kind_key, weight] : *per_faction) {
                        const std::string kind(kind_key.str());
                        require_contract_kind(kind, "[kontrakty.typy." + name + "]");
                        cfg.kontrakty_wagi[name][kind] = weight.value_or(0.0);
                    }
                    continue;
                }
                require_contract_kind(name, "[kontrakty.typy]");
                cfg.kontrakty_wagi[""][name] = node.value_or(0.0);
            }
        }

        if (const auto* mn = (*kon)["mnoznik"].as_table()) {
            for (const auto& [key, node] : *mn) {
                const std::string kind(key.str());
                require_contract_kind(kind, "[kontrakty.mnoznik]");
                cfg.kontrakty_mnoznik[kind] = node.value_or(1.0);
            }
        }
    }

    if (const auto* rep = tbl["reputacja"].as_table()) {
        cfg.reputacja_sync = (*rep)["sync"].value_or(cfg.reputacja_sync);
        cfg.reputacja_zakres = (*rep)["zakres"].value_or(cfg.reputacja_zakres);
        cfg.reputacja_prog = (*rep)["prog"].value_or(cfg.reputacja_prog);
        cfg.reputacja_polityka = (*rep)["polityka"].value_or(cfg.reputacja_polityka);
    }

    if (const auto* ceny = tbl["ceny"].as_table()) {
        cfg.ceny_sync = (*ceny)["sync"].value_or(cfg.ceny_sync);
        cfg.ceny_mnoznik_wrog = (*ceny)["mnoznik_wrog"].value_or(cfg.ceny_mnoznik_wrog);
        cfg.ceny_mnoznik_sojusznik =
            (*ceny)["mnoznik_sojusznik"].value_or(cfg.ceny_mnoznik_sojusznik);
        cfg.ceny_prog_zmiany = (*ceny)["prog_zmiany"].value_or(cfg.ceny_prog_zmiany);
        cfg.ceny_prog_embarga = (*ceny)["prog_embarga"].value_or(cfg.ceny_prog_embarga);
    }
}

// Automatyczne znalezienie katalogu storage moda. Ścieżka wygląda tak:
//   %APPDATA%/SpaceEngineers/Saves/<steamid>/<świat>/Storage/<mod>/events.jsonl
// i zmienia się przy KAŻDYM nowym świecie — ręczne wpisywanie jej do rules.local.toml
// było najczęstszym powodem „brain nie widzi gry". Szukamy więc pliku events.jsonl
// w drzewie zapisów i bierzemy katalog z najświeższym (mod pisze session_start przy
// każdym wczytaniu świata, więc najnowszy = ten, w którym gracz właśnie jest).
// ZF_SAVES_DIR nadpisuje korzeń poszukiwań (testy, nietypowe instalacje, Linux/Proton).
std::string detect_storage_dir() {
    namespace fs = std::filesystem;

    std::vector<fs::path> roots;
    if (const char* override_root = std::getenv("ZF_SAVES_DIR")) {
        // Prawdziwe nadpisanie, nie dodatkowy korzeń: testy (i nietypowe instalacje)
        // muszą móc odciąć się od prawdziwych zapisów SE na tej samej maszynie.
        roots.emplace_back(override_root);
    } else if (const char* appdata = std::getenv("APPDATA")) {
        roots.emplace_back(fs::path(appdata) / "SpaceEngineers" / "Saves");
    }

    fs::path best;
    fs::file_time_type best_time{};
    for (const fs::path& root : roots) {
        std::error_code ec;
        if (!fs::is_directory(root, ec)) {
            continue;
        }
        auto it = fs::recursive_directory_iterator(
            root, fs::directory_options::skip_permission_denied, ec);
        if (ec) {
            continue;
        }
        for (const auto& entry : it) {
            if (it.depth() >= 5) {
                it.disable_recursion_pending(); // Saves/<user>/<świat>/Storage/<mod>/plik
            }
            std::error_code entry_ec;
            if (entry.path().filename() != "events.jsonl" || !entry.is_regular_file(entry_ec)) {
                continue;
            }
            const auto mtime = fs::last_write_time(entry.path(), entry_ec);
            if (entry_ec) {
                continue;
            }
            if (best.empty() || mtime > best_time) {
                best = entry.path().parent_path();
                best_time = mtime;
            }
        }
    }
    return best.empty() ? std::string{} : best.generic_string();
}

// Skrót pełnej ścieżki storage. Sama nazwa świata nie wystarczy jako klucz: dwa
// zapisy o tej samej nazwie (drugi profil Steam, kopia zapasowa świata) dzieliłyby
// jedną bazę — czyli dokładnie ten błąd, który tu naprawiamy.
std::string short_hash(const std::string& text) {
    std::uint64_t h = 1469598103934665603ULL; // FNV-1a 64
    for (const unsigned char c : text) {
        h ^= c;
        h *= 1099511628211ULL;
    }
    const auto folded = static_cast<std::uint32_t>(h ^ (h >> 32));
    std::string out(8, '0');
    for (int i = 7; i >= 0; --i) {
        out[static_cast<std::size_t>(i)] = "0123456789abcdef"[(folded >> ((7 - i) * 4)) & 0xFu];
    }
    return out;
}

// Nazwa świata ze ścieżki storage: Saves/<steamid>/<ŚWIAT>/Storage/<mod>. Przy innym
// układzie (ręczna ścieżka, testy) bierzemy ostatni człon — chodzi tylko o to, żeby
// człowiek poznał plik po nazwie; unikalność zapewnia hash.
std::string world_slug(const std::string& storage_dir) {
    namespace fs = std::filesystem;
    fs::path p(storage_dir);
    if (p.filename().empty()) {
        p = p.parent_path(); // ścieżka z końcowym separatorem
    }
    fs::path candidate = p;
    if (p.has_parent_path() && p.parent_path().filename() == "Storage") {
        candidate = p.parent_path().parent_path();
    }

    std::string name = candidate.filename().string();
    std::string slug;
    for (const char c : name) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                        c == '-' || c == '_';
        slug.push_back(ok ? c : '_');
        if (slug.size() >= 40) {
            break;
        }
    }
    while (!slug.empty() && slug.back() == '_') {
        slug.pop_back();
    }
    return slug.empty() ? std::string("swiat") : slug;
}

// "state/zf_state.sqlite3" + storage świata -> "state/zf_state_<świat>_<hash>.sqlite3".
// Klucz bierzemy ze ścieżki storage, a nie z nazwy świata w session_start, bo bazę
// trzeba otworzyć zanim przyjdzie pierwsze zdarzenie.
std::string per_world_db_path(const std::string& db_path, const std::string& storage_dir) {
    std::filesystem::path p(db_path);
    const std::string ext = p.extension().string();
    p.replace_extension();
    return p.generic_string() + "_" + world_slug(storage_dir) + "_" + short_hash(storage_dir) + ext;
}

toml::table parse_or_throw(const std::string& path) {
    try {
        return toml::parse_file(path);
    } catch (const toml::parse_error& err) {
        throw std::runtime_error(std::string("nie można sparsować configu ") + path + ": " + err.description().data());
    }
}

} // namespace

std::string local_config_path(const std::string& path) {
    std::filesystem::path p(path);
    p.replace_extension(".local.toml");
    return p.generic_string();
}

Config load_config(const std::string& path) {
    const toml::table tbl = parse_or_throw(path);
    if (tbl["bridge"].as_table() == nullptr) {
        throw std::runtime_error("config " + path + ": brak sekcji [bridge]");
    }

    Config cfg;
    apply_table(tbl, cfg);

    // Nakładka per maszyna (poza gitem): rules.local.toml obok rules.toml.
    const std::string local_path = local_config_path(path);
    std::error_code ec;
    if (std::filesystem::exists(local_path, ec)) {
        apply_table(parse_or_throw(local_path), cfg);
    }

    // Pusta albo nieistniejąca ścieżka (typowo: nowy świat) — spróbuj wykryć sam.
    std::error_code storage_ec;
    if (cfg.storage_dir.empty() || !std::filesystem::is_directory(cfg.storage_dir, storage_ec)) {
        const std::string detected = detect_storage_dir();
        if (!detected.empty()) {
            if (cfg.storage_dir.empty()) {
                std::cerr << "[brain] storage wykryty automatycznie: " << detected << "\n";
            } else {
                std::cerr << "[brain] storage z configu nie istnieje (" << cfg.storage_dir
                          << ") — biorę wykryty: " << detected << "\n";
            }
            cfg.storage_dir = detected;
        }
    }

    if (cfg.storage_dir.empty()) {
        throw std::runtime_error("config " + path + ": nie znalazłem storage moda ani w [bridge].storage_dir, "
                                 "ani automatycznie w zapisach Space Engineers. Wczytaj raz świat z modem "
                                 "(wtedy powstaje events.jsonl) albo wpisz ścieżkę ręcznie w " + local_path +
                                 " w sekcji [bridge]");
    }

    cfg.db_path_wspolna = cfg.db_path;
    if (cfg.db_per_swiat) {
        cfg.db_path = per_world_db_path(cfg.db_path, cfg.storage_dir);
    }

    return cfg;
}

ConfigWatcher::ConfigWatcher(std::string path) : path_(std::move(path)) {
    config_ = load_config(path_);
    read_mtimes(mtime_main_, mtime_local_);
}

void ConfigWatcher::read_mtimes(std::int64_t& main_out, std::int64_t& local_out) const {
    namespace fs = std::filesystem;
    std::error_code ec;
    const auto mtime = [&ec](const std::string& p) -> std::int64_t {
        const auto t = fs::last_write_time(p, ec);
        return ec ? 0 : static_cast<std::int64_t>(t.time_since_epoch().count());
    };
    main_out = mtime(path_);
    local_out = mtime(local_config_path(path_));
}

const std::vector<std::string>& Config::contract_kinds() {
    // Kolejność = kolejność w dokumentacji i w /zf kontrakt. Każdy typ ma swoją klasę
    // w Sandbox.ModAPI.Contracts (patrz docs/protocol.md i mod/.../Contracts.cs).
    static const std::vector<std::string> kinds{
        "dostawa",       // MyContractAcquisition
        "nagroda",       // MyContractBounty
        "transport",     // MyContractHauling
        "naprawa",       // MyContractRepair
        "poszukiwania",  // MyContractSearch
        "eskorta",       // MyContractEscort
        "wlasne",        // MyContractCustom
    };
    return kinds;
}

double contract_kind_weight(const Config& cfg, const std::string& faction,
                            const std::string& kind, const std::string& state) {
    double weight = builtin_weight(kind);
    const auto defaults = cfg.kontrakty_wagi.find("");
    if (defaults != cfg.kontrakty_wagi.end()) {
        const auto it = defaults->second.find(kind);
        if (it != defaults->second.end()) {
            weight = it->second;
        }
    }
    const auto per_faction = cfg.kontrakty_wagi.find(faction);
    if (per_faction != cfg.kontrakty_wagi.end()) {
        const auto it = per_faction->second.find(kind);
        if (it != per_faction->second.end()) {
            weight = it->second;
        }
    }
    // Frakcja w napięciu albo wojnie chce, żeby ktoś zrobił za nią brudną robotę —
    // ale mnożnik nie WŁĄCZA typu wyłączonego wagą 0 (0 × N = 0, i tak ma być).
    if (kind == "nagroda" && (state == "napiecie" || state == "wojna")) {
        weight *= cfg.kontrakty_mnoznik_nagrody_w_napieciu;
    }
    return weight > 0 ? weight : 0;
}

double contract_kind_multiplier(const Config& cfg, const std::string& kind) {
    const auto it = cfg.kontrakty_mnoznik.find(kind);
    if (it == cfg.kontrakty_mnoznik.end()) {
        return 1.0;
    }
    // Mnożnik ≤ 0 zerowałby nagrodę i relację — traktujemy jak brak wpisu.
    return it->second > 0 ? it->second : 1.0;
}

bool ConfigWatcher::poll() {
    std::int64_t m = 0;
    std::int64_t l = 0;
    read_mtimes(m, l);
    if (m == mtime_main_ && l == mtime_local_) {
        return false;
    }
    mtime_main_ = m;
    mtime_local_ = l;
    try {
        config_ = load_config(path_);
        std::cerr << "[brain] config przeładowany (" << path_ << ")\n";
        return true;
    } catch (const std::exception& e) {
        std::cerr << "[brain] błąd przeładowania configu, zostaje poprzedni: " << e.what() << "\n";
        return false;
    }
}

} // namespace zf
