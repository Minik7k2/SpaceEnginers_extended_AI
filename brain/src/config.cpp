#include "config.hpp"

#include <stdexcept>

#include <toml++/toml.hpp>

namespace zf {

Config load_config(const std::string& path) {
    toml::table tbl;
    try {
        tbl = toml::parse_file(path);
    } catch (const toml::parse_error& err) {
        throw std::runtime_error(std::string("nie można sparsować configu ") + path + ": " + err.description().data());
    }

    Config cfg;

    const auto* bridge = tbl["bridge"].as_table();
    if (bridge == nullptr) {
        throw std::runtime_error("config " + path + ": brak sekcji [bridge]");
    }
    // Uwaga: at() rzuca przy braku klucza, przez co value_or nigdy by nie zadziałało —
    // dla kluczy opcjonalnych używamy operator[], defaulty z value_or są wtedy realne.
    cfg.storage_dir = (*bridge)["storage_dir"].value_or(std::string{});
    if (cfg.storage_dir.empty()) {
        throw std::runtime_error("config " + path + ": [bridge].storage_dir jest puste — uzupełnij ścieżkę do storage moda");
    }
    cfg.poll_ms = (*bridge)["poll_ms"].value_or(500);
    cfg.db_path = (*bridge)["db_path"].value_or(std::string("state/zf_state.sqlite3"));
    cfg.rotate_bytes = (*bridge)["rotate_bytes"].value_or<std::uint64_t>(5 * 1024 * 1024);

    if (const auto* llm = tbl["llm"].as_table()) {
        cfg.llm_model_path = (*llm)["model_path"].value_or(std::string{});
        cfg.llm_use_gpu = (*llm)["use_gpu"].value_or(true);
        cfg.llm_max_chars = (*llm)["max_chars"].value_or(200);
    }

    return cfg;
}

} // namespace zf
