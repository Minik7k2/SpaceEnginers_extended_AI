#include <atomic>
#include <chrono>
#include <csignal>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <random>
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
#include "engine.hpp"
#include "fallback.hpp"

namespace {

constexpr const char* kFallbackPath = "personas/fallback.toml";

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

std::int64_t now_unix_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

// Log obserwacyjny zdarzenia (relacje i radio robi Engine).
void log_event(const zf::Event& ev) {
    const auto str = [&ev](const char* key) {
        return ev.data.contains(key) && ev.data[key].is_string() ? ev.data[key].get<std::string>()
                                                                 : std::string{};
    };
    const auto num = [&ev](const char* key) {
        return ev.data.contains(key) && ev.data[key].is_number() ? ev.data[key].get<double>() : 0.0;
    };

    if (ev.type == "session_start") {
        std::cout << "[brain] session_start: świat=" << str("world") << " gracz=" << str("player_name")
                  << "\n";
    } else if (ev.type == "chat_message") {
        std::cout << "[brain] chat_message: \"" << str("text") << "\"\n";
    } else if (ev.type == "combat_hit") {
        std::cout << "[brain] combat_hit: frakcja=" << str("faction") << " dmg=" << num("damage")
                  << " trafień=" << num("hits") << " broń=" << str("weapon") << "\n";
    } else if (ev.type == "grid_destroyed") {
        std::cout << "[brain] grid_destroyed: frakcja=" << str("faction") << " siatka=\"" << str("grid")
                  << "\"\n";
    } else if (ev.type == "proximity") {
        std::cout << "[brain] proximity: frakcja=" << str("faction") << " stan=" << str("state")
                  << " dystans=" << num("dist") << " m\n";
    } else if (ev.type == "debug_command") {
        std::cout << "[brain] debug_command: " << str("cmd") << "\n";
    }
    // heartbeat celowo bez logu — od Etapu 3 tylko zaśmiecał konsolę.
}

// Tryb --replay: odtwarza zdarzenia z pliku JSONL bez gry i bez trwałego stanu
// (baza w pamięci). Czas bierzemy z pola ts linii, więc dryf/tick liczą się
// jak w prawdziwej sesji.
int run_replay(const std::string& file, const zf::Config& cfg) {
    std::ifstream in(file, std::ios::binary);
    if (!in) {
        std::cerr << "[brain] nie można otworzyć pliku replay: " << file << "\n";
        return 1;
    }

    zf::Db db(":memory:");
    zf::Fallback fallback(kFallbackPath);
    zf::Engine engine(db, fallback, /*rng_seed=*/1337);

    const auto print_radio = [](const std::vector<zf::RadioOut>& msgs) {
        for (const zf::RadioOut& msg : msgs) {
            std::cout << "  [RADIO | " << msg.faction << "] " << msg.text << "\n";
        }
    };

    std::string line;
    std::int64_t last_ts = 0;
    while (std::getline(in, line)) {
        if (line.empty()) {
            continue;
        }
        nlohmann::json parsed;
        try {
            parsed = nlohmann::json::parse(line);
        } catch (const nlohmann::json::exception& e) {
            std::cerr << "[brain] pominięta uszkodzona linia: " << e.what() << "\n";
            continue;
        }
        zf::Event ev;
        ev.type = parsed.value("type", std::string{});
        ev.data = parsed.value("data", nlohmann::json::object());
        last_ts = parsed.value("ts", last_ts);

        log_event(ev);
        print_radio(engine.on_event(ev, cfg, last_ts));
        print_radio(engine.tick(cfg, last_ts));
    }

    std::cout << "[brain] replay zakończony. Relacje: " << engine.relations_report() << "\n";
    return 0;
}

} // namespace

int main(int argc, char** argv) {
    std::string config_path = "configs/rules.toml";
    std::string replay_path;
    bool once = false;
    bool mock_llm = false;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--config" && i + 1 < argc) {
            config_path = argv[++i];
        } else if (arg == "--once") {
            once = true;
        } else if (arg == "--mock-llm") {
            mock_llm = true;
        } else if (arg == "--replay" && i + 1 < argc) {
            replay_path = argv[++i];
        } else {
            std::cerr << "nieznany argument: " << arg
                      << " (dostępne: --config <plik>, --once, --mock-llm, --replay <plik.jsonl>)\n";
            return 1;
        }
    }

#ifdef _WIN32
    SetConsoleOutputCP(CP_UTF8);
#endif

    std::signal(SIGINT, handle_signal);
    std::signal(SIGTERM, handle_signal);

    anchor_to_config_dir(argv[0], config_path);

    // Do Etapu 4 głos to zawsze szablony fallback; flaga --mock-llm już istnieje,
    // żeby skrypty testowe nie musiały się zmieniać, gdy dojdzie prawdziwy LLM.
    if (!mock_llm) {
        std::cout << "[brain] uwaga: LLM jeszcze niepodłączony (Etap 4) — działam jak --mock-llm\n";
    }

    try {
        zf::ConfigWatcher watcher(config_path);

        if (!replay_path.empty()) {
            return run_replay(replay_path, watcher.get());
        }

        zf::Db db(watcher.get().db_path);
        zf::EventReader events(watcher.get().storage_dir, db);
        zf::CommandWriter commands(watcher.get().storage_dir, db, watcher.get().rotate_bytes);
        zf::Fallback fallback(kFallbackPath);
        zf::Engine engine(db, fallback, std::random_device{}());

        std::cout << "[brain] start, storage=" << watcher.get().storage_dir
                  << " poll_ms=" << watcher.get().poll_ms << "\n";
        std::cout << "[brain] relacje: " << engine.relations_report() << "\n";

        const auto send_all = [&commands](const std::vector<zf::RadioOut>& msgs) {
            for (const zf::RadioOut& msg : msgs) {
                commands.write_radio_message(msg.faction, msg.text, msg.color, msg.priority);
                std::cout << "[brain] radio [" << msg.faction << "]: " << msg.text << "\n";
            }
        };

        do {
            watcher.poll();   // hot-reload rules.toml / rules.local.toml
            fallback.poll();  // hot-reload szablonów person
            const zf::Config& cfg = watcher.get();

            const std::int64_t now = now_unix_ms();
            for (const zf::Event& ev : events.poll()) {
                log_event(ev);
                send_all(engine.on_event(ev, cfg, now));

                // Echo [RADIO | TEST] dla niezaadresowanych wiadomości — kryterium
                // Etapu 1, zostaje jako szybki test życia mostka do czasu Etapu 4.
                if (ev.type == "chat_message" && (!ev.data.contains("target") || ev.data["target"].is_null())) {
                    const std::string text = ev.data.value("text", std::string{});
                    commands.write_radio_message("TEST", "Echo: " + text, "white", 0);
                }
            }
            send_all(engine.tick(cfg, now));

            if (!once) {
                std::this_thread::sleep_for(std::chrono::milliseconds(cfg.poll_ms));
            }
        } while (!once && !g_should_stop.load());

        std::cout << "[brain] koniec pracy\n";
    } catch (const std::exception& e) {
        std::cerr << "[brain] błąd krytyczny: " << e.what() << "\n";
        return 1;
    }

    return 0;
}
