#include "config.hpp"

#include <cstdlib>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <vector>

#include <toml++/toml.hpp>

namespace zf {

namespace {

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
