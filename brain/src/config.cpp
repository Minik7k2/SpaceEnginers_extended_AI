#include "config.hpp"

#include <filesystem>
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

} // namespace zf
