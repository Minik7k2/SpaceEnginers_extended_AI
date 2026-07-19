#include <atomic>
#include <chrono>
#include <csignal>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#include "bridge.hpp"
#include "config.hpp"
#include "db.hpp"

namespace {

std::atomic<bool> g_should_stop{false};

// CLion i konsola startują z różnych katalogów roboczych, a config (i ścieżki
// względne w nim, np. db_path) zakładają katalog brain/. Gdy configu nie ma
// w bieżącym katalogu, szukamy go w górę od położenia binarki i tam się
// przenosimy — binarka nie zależy wtedy od miejsca uruchomienia.
void anchor_to_config_dir(const char* argv0, const std::string& config_rel) {
    namespace fs = std::filesystem;
    std::error_code ec;
    if (fs::exists(config_rel, ec)) {
        return;
    }
    const fs::path exe = fs::absolute(fs::path(argv0), ec);
    if (ec) {
        return;
    }
    for (fs::path dir = exe.parent_path(); !dir.empty(); dir = dir.parent_path()) {
        if (fs::exists(dir / config_rel, ec)) {
            fs::current_path(dir, ec);
            if (!ec) {
                std::cout << "[brain] katalog roboczy: " << dir.string() << "\n";
            }
            return;
        }
        if (dir == dir.root_path()) {
            return;
        }
    }
}

void handle_signal(int) {
    g_should_stop.store(true);
}

// Bezpieczne odczyty pól data: value() nlohmanna rzuca przy polu obecnym-ale-null,
// a w pętli głównej mostka wyjątków być nie może.
std::string data_str(const zf::Event& ev, const char* key) {
    return ev.data.contains(key) && ev.data[key].is_string() ? ev.data[key].get<std::string>()
                                                             : std::string{};
}

double data_num(const zf::Event& ev, const char* key) {
    return ev.data.contains(key) && ev.data[key].is_number() ? ev.data[key].get<double>() : 0.0;
}

// Etap 1: mózg bez LLM. Loguje zdarzenia i odpowiada na chat_message testowym
// echem [RADIO | TEST] — kryterium: <3 s od wiadomości na czacie do echa.
// Etap 2: zdarzenia bojowe i proximity na razie tylko logujemy — silnik w Etapie 3.
void handle_event(const zf::Event& ev, zf::CommandWriter& commands) {
    if (ev.type == "session_start") {
        std::cout << "[brain] session_start: świat=" << ev.data.value("world", std::string{})
                  << " gracz=" << ev.data.value("player_name", std::string{}) << "\n";
    } else if (ev.type == "heartbeat") {
        std::cout << "[brain] heartbeat prędkość=" << ev.data.value("speed", 0.0) << " m/s\n";
    } else if (ev.type == "chat_message") {
        const std::string text = ev.data.value("text", std::string{});
        std::cout << "[brain] chat_message: \"" << text << "\" -> echo testowe\n";
        commands.write_radio_message("TEST", "Echo: " + text, "white", 0);
    } else if (ev.type == "combat_hit") {
        std::cout << "[brain] combat_hit: frakcja=" << data_str(ev, "faction")
                  << " dmg=" << data_num(ev, "damage") << " trafień=" << data_num(ev, "hits")
                  << " broń=" << data_str(ev, "weapon") << "\n";
    } else if (ev.type == "grid_destroyed") {
        std::cout << "[brain] grid_destroyed: frakcja=" << data_str(ev, "faction")
                  << " siatka=\"" << data_str(ev, "grid") << "\"\n";
    } else if (ev.type == "proximity") {
        std::cout << "[brain] proximity: frakcja=" << data_str(ev, "faction")
                  << " stan=" << data_str(ev, "state") << " dystans=" << data_num(ev, "dist")
                  << " m\n";
    } else {
        std::cout << "[brain] pominięto zdarzenie typu \"" << ev.type << "\" (obsługa w kolejnych etapach)\n";
    }
}

} // namespace

int main(int argc, char** argv) {
    std::string config_path = "configs/rules.toml";
    bool once = false;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--config" && i + 1 < argc) {
            config_path = argv[++i];
        } else if (arg == "--once") {
            once = true;
        } else {
            std::cerr << "nieznany argument: " << arg << "\n";
            return 1;
        }
    }

#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif

    std::signal(SIGINT, handle_signal);
    std::signal(SIGTERM, handle_signal);

    anchor_to_config_dir(argv[0], config_path);

    try {
        const zf::Config config = zf::load_config(config_path);
        zf::Db db(config.db_path);
        zf::EventReader events(config.storage_dir, db);
        zf::CommandWriter commands(config.storage_dir, db, config.rotate_bytes);

        std::cout << "[brain] start, storage=" << config.storage_dir
                  << " poll_ms=" << config.poll_ms << "\n";

        do {
            for (const zf::Event& ev : events.poll()) {
                handle_event(ev, commands);
            }
            if (!once) {
                std::this_thread::sleep_for(std::chrono::milliseconds(config.poll_ms));
            }
        } while (!once && !g_should_stop.load());

        std::cout << "[brain] koniec pracy\n";
    } catch (const std::exception& e) {
        std::cerr << "[brain] błąd krytyczny: " << e.what() << "\n";
        return 1;
    }

    return 0;
}
