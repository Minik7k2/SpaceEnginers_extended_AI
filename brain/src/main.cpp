#include <atomic>
#include <chrono>
#include <csignal>
#include <cstring>
#include <iostream>
#include <string>
#include <thread>

#include "bridge.hpp"
#include "config.hpp"
#include "db.hpp"

namespace {

std::atomic<bool> g_should_stop{false};

void handle_signal(int) {
    g_should_stop.store(true);
}

// Etap 1: mózg bez LLM. Loguje zdarzenia i odpowiada na chat_message testowym
// echem [RADIO | TEST] — kryterium: <3 s od wiadomości na czacie do echa.
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

    std::signal(SIGINT, handle_signal);
    std::signal(SIGTERM, handle_signal);

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
