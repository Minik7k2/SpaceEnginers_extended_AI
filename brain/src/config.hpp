#pragma once

#include <cstdint>
#include <string>

namespace zf {

struct Config {
    // [bridge]
    std::string storage_dir;
    int poll_ms = 500;
    std::string db_path = "state/zf_state.sqlite3";
    std::uint64_t rotate_bytes = 5 * 1024 * 1024;

    // [llm] — nieużywane przed Etapem 4, wczytywane już teraz bo config jest hot-reloadowany całościowo.
    std::string llm_model_path;
    bool llm_use_gpu = true;
    int llm_max_chars = 200;
};

// Rzuca std::runtime_error gdy plik nie istnieje lub brakuje wymaganego pola [bridge].storage_dir.
Config load_config(const std::string& path);

} // namespace zf
