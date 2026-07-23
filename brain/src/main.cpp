#include <atomic>
#include <chrono>
#include <csignal>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <random>
#include <string>
#include <thread>
#include <utility>

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
#include "llm.hpp"

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

// Prompt systemowy: karta persony z pliku + świeża pamięć frakcji z SQLite.
std::string build_system_prompt(const std::string& faction, zf::Db& db) {
    std::string prompt;
    std::ifstream in(zf::persona_path(faction), std::ios::binary);
    if (in) {
        std::ostringstream buf;
        buf << in.rdbuf();
        prompt = buf.str();
    }
    const std::vector<std::string> memories = db.recent_memories(faction, 5);
    if (!memories.empty()) {
        prompt += "\n\nOstatnie wydarzenia, które pamiętasz:\n";
        for (const std::string& m : memories) {
            prompt += "- " + m + "\n";
        }
    }
    return prompt;
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
    const auto print_spawns = [&engine]() {
        for (const zf::SpawnOut& sp : engine.take_spawns()) {
            std::cout << "  [SPAWN | " << sp.faction << "] kind=" << sp.kind << " — " << sp.context << "\n";
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
        print_spawns();
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
    // Logi bez buforowania: widoczne natychmiast, także gdy stdout jest
    // przekierowany do pliku (pełne buforowanie gubi ostatnie linie przy ubiciu
    // procesu). Wolumen logów jest mały, więc narzut flush-a jest pomijalny.
    std::cout << std::unitbuf;

    std::signal(SIGINT, handle_signal);
    std::signal(SIGTERM, handle_signal);

    anchor_to_config_dir(argv[0], config_path);

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

        // --mock-llm = celowe pominięcie modelu (testy silnika bez kosztu generacji).
        zf::LlmWorker llm(mock_llm ? zf::Config{} : watcher.get());

        std::cout << "[brain] start, storage=" << watcher.get().storage_dir
                  << " poll_ms=" << watcher.get().poll_ms << "\n";
        std::cout << "[brain] relacje: " << engine.relations_report() << "\n";

        // Intencje z personą idą do LLM (fallback w zadaniu na wypadek porażki);
        // reszta (SYSTEM, frakcje bez persony, brak modelu) — od razu szablonem.
        // Zlecenia spawnu z silnika (osobny kanał od radia) -> spawn_request w commands.jsonl.
        const auto flush_spawns = [&commands, &engine]() {
            for (const zf::SpawnOut& sp : engine.take_spawns()) {
                commands.write_spawn_request(sp.faction, sp.kind, sp.near_player, sp.context);
                std::cout << "[brain] spawn_request [" << sp.faction << "] kind=" << sp.kind << " — "
                          << sp.context << "\n";
            }
        };

        // Pamięć dialogu (5c): ostatnie tury Gracz<->frakcja per frakcja, wstrzykiwane
        // do promptu, żeby frakcja trzymała wątek rozmowy, a nie odpowiadała z jednej
        // wiadomości (feedback z gry: „nie trzyma wątku"). Ephemeralna — na sesję braina.
        std::map<std::string, std::deque<std::pair<std::string, std::string>>> dialog;
        constexpr std::size_t kDialogTurns = 4;

        const auto send_all = [&commands, &llm, &db, &dialog](const std::vector<zf::RadioOut>& msgs) {
            for (const zf::RadioOut& msg : msgs) {
                if (llm.enabled() && !msg.kind.empty() && !zf::persona_path(msg.faction).empty()) {
                    std::string user_prompt = msg.context;
                    const auto it = dialog.find(msg.faction);
                    if (it != dialog.end() && !it->second.empty()) {
                        std::string hist = "Wcześniejsza rozmowa z graczem (najstarsze u góry):\n";
                        for (const auto& turn : it->second) {
                            hist += "- Gracz: " + turn.first + "\n- Ty: " + turn.second + "\n";
                        }
                        user_prompt = hist + "\n" + msg.context;
                    }
                    zf::LlmJob job;
                    job.faction = msg.faction;
                    job.system_prompt = build_system_prompt(msg.faction, db);
                    job.user_prompt = user_prompt + " Rodzaj wypowiedzi: " + msg.kind + ".";
                    job.fallback_text = msg.text;
                    job.color = msg.color;
                    job.priority = msg.priority;
                    job.expect_decision = msg.expect_decision;
                    job.player_msg = msg.player_msg;
                    llm.submit(std::move(job));
                    continue;
                }
                if (msg.text.empty()) {
                    continue;
                }
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
            flush_spawns();   // spawny z on_event (w tym /zf raid) i z ticka

            // Gotowe wypowiedzi z wątku LLM (albo fallbacki po nieudanej generacji).
            for (const zf::LlmResult& res : llm.poll_results()) {
                commands.write_radio_message(res.faction, res.text, res.color, res.priority);
                std::cout << "[brain] radio" << (res.from_llm ? " (LLM)" : " (fallback)") << " ["
                          << res.faction << "]: " << res.text << "\n";
                // Dopisz turę do pamięci dialogu — tylko realne wypowiedzi LLM (nie fallback/
                // placeholder), żeby nie zaśmiecać historii. player_msg pusty poza czatem.
                if (res.from_llm && !res.player_msg.empty()) {
                    auto& dq = dialog[res.faction];
                    auto clip = [](std::string t) { if (t.size() > 200) t.resize(200); return t; };
                    dq.emplace_back(clip(res.player_msg), clip(res.text));
                    while (dq.size() > kDialogTurns) {
                        dq.pop_front();
                    }
                }
                if (res.deescalate) {
                    // Frakcja zdecydowała odpuścić: silnik nalicza okup i (jeśli był rajd)
                    // buforuje stand_down, który zaraz zejdzie do moda.
                    engine.apply_deescalation(res.faction, cfg, now);
                }
            }
            for (const std::string& faction : engine.take_standdowns()) {
                commands.write_stand_down(faction);
                std::cout << "[brain] stand_down [" << faction << "] — statki rajdu odwołane\n";
            }

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
