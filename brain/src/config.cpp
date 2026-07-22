#include "config.hpp"

#include <filesystem>
#include <iostream>
#include <stdexcept>

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
        cfg.deeskalacja_bonus = (*zm)["deeskalacja_bonus"].value_or(cfg.deeskalacja_bonus);
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
    }

    if (const auto* spawn = tbl["spawn"].as_table()) {
        cfg.spawn_wlaczone = (*spawn)["wlaczone"].value_or(cfg.spawn_wlaczone);
        cfg.spawn_cooldown_min = (*spawn)["cooldown_min"].value_or(cfg.spawn_cooldown_min);
    }
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

    if (cfg.storage_dir.empty()) {
        throw std::runtime_error("config " + path + ": [bridge].storage_dir jest puste — utwórz " + local_path +
                                 " z sekcją [bridge] i ścieżką storage_dir do storage moda na tej maszynie");
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
