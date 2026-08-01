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
#include "scenariusz.hpp"

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
    } else if (ev.type == "autotest_result") {
        // /zf autotest w grze: wynik ląduje też tutaj, żeby cały przebieg dało się
        // przeczytać w jednym miejscu (i wkleić z logu, zamiast przepisywać z czatu).
        std::cout << "[brain] autotest [" << str("sekcja") << "] " << str("wynik") << " "
                  << str("nazwa");
        if (!str("opis").empty()) {
            std::cout << " — " << str("opis");
        }
        std::cout << "\n";
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
    // B+: trwała nieufność po złamanych obietnicach okupu (świat mściwy) — karmi decyzję LLM.
    const int broken = db.ransom_broken(faction);
    if (broken > 0) {
        prompt += "\n\nWiarygodność gracza: już " + std::to_string(broken) +
                  " raz(y) obiecał wam okup i nie dostarczył żądanego trybutu na czas. Bądź wobec "
                  "niego nieufny — nie wierz łatwo kolejnym obietnicom; jeśli w ogóle godzisz się na "
                  "okup, żądaj konkretnego trybutu w surowcach, a nie samych słów.\n";
    }
    return prompt;
}

// Tryb --replay: odtwarza zdarzenia z pliku JSONL bez gry i bez trwałego stanu
// (baza w pamięci). Czas bierzemy z pola ts linii, więc dryf/tick liczą się jak
// w prawdziwej sesji. Gdy plik zawiera linie "oczekuj", pełni rolę TESTU regresji —
// całą robotę wykonuje scenariusz.cpp, żeby tę samą logikę dało się wołać z ctest.
int run_replay(const std::string& file, const zf::Config& cfg) {
    const zf::WynikScenariusza wynik = zf::uruchom_scenariusz(file, cfg, kFallbackPath, std::cout);
    if (!wynik.plik_wczytany) {
        return 1;
    }
    std::cout << "[brain] replay zakończony.\n";
    return wynik.bledow == 0 ? 0 : 1;
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
        // --replay/scenariusze nie dotykają mostka plikowego, więc brak storage SE nie
        // może ich blokować (na maszynie CI żadnego zapisu gry nie ma).
        zf::ConfigWatcher watcher(config_path, /*wymagaj_storage=*/replay_path.empty());

        if (!replay_path.empty()) {
            return run_replay(replay_path, watcher.get());
        }

        // Stan siedzi w bazie przypisanej do TEGO zapisu (klucz: ścieżka storage), więc
        // nowy świat startuje z czystymi relacjami. Mówimy wprost, czy baza jest nowa —
        // „skąd ta wojna w świeżym świecie" było wcześniej nie do odróżnienia od buga.
        const std::string db_path = watcher.get().db_path;
        std::error_code db_ec;
        const bool db_nowa = !std::filesystem::exists(db_path, db_ec);

        zf::Db db(db_path);
        std::cout << "[brain] baza: " << db_path << (db_nowa ? " (nowa, czyste relacje)" : " (wczytana)")
                  << "\n";
        if (db_nowa && watcher.get().db_per_swiat &&
            std::filesystem::exists(watcher.get().db_path_wspolna, db_ec)) {
            std::cout << "[brain] stara wspólna baza " << watcher.get().db_path_wspolna
                      << " istnieje, ale NIE jest używana (stan jest per świat)\n";
        }

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

        // Reputacja: nasza relacja przepisana na skalę gry (hybryda). Mod zapisuje ją
        // przez MyAPIGateway.Session.Factions, więc gracz widzi jedną liczbę — w oknie
        // frakcji, nie tylko po /zf rel.
        const auto flush_reputations = [&commands, &engine]() {
            for (const zf::ReputationOut& rep : engine.take_reputations()) {
                commands.write_reputation_sync(rep.faction, rep.other, rep.value, rep.vanilla);
                std::cout << "[brain] reputation_sync [" << rep.faction
                          << (rep.other.empty() ? "->gracz" : "->" + rep.other) << "] "
                          << rep.value << " => " << rep.vanilla << " (skala gry)\n";
            }
        };

        // Cennik sklepów (Etap 6): relacja przepisana na mnożnik cen. Mod przechodzi po
        // ofertach bloku sklepu frakcji i przelicza je z ceny bazowej.
        const auto flush_prices = [&commands, &engine]() {
            for (const zf::PriceOut& p : engine.take_prices()) {
                commands.write_price_update(p.faction, p.modifier, p.embargo, p.value);
                std::cout << "[brain] price_update [" << p.faction << "] relacja " << p.value
                          << " => ceny x" << p.modifier
                          << (p.embargo ? " (EMBARGO — frakcja nie handluje)" : "") << "\n";
            }
        };

        // Zlecenia kontraktów (Etap 6) — osobny kanał, tak jak spawny.
        const auto flush_contracts = [&commands, &engine]() {
            for (const zf::ContractOut& c : engine.take_contracts()) {
                commands.write_contract_create(c.faction, c.kind, c.reward, c.duration_min,
                                               c.target_faction);
                std::cout << "[brain] contract_create [" << c.faction << "] " << c.kind << " za "
                          << c.reward << " kr, " << c.duration_min << " min"
                          << (c.target_faction.empty() ? "" : ", cel " + c.target_faction) << "\n";
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
            }
            // Echo [RADIO | TEST] z Etapu 1 usunięte: od Etapu 5 każda zwykła wiadomość
            // na czacie wracała do gracza jako echo, czyli szum. Życie mostka widać teraz
            // po odpowiedziach frakcji i po komendzie /zf rel.
            send_all(engine.tick(cfg, now));
            flush_spawns();   // spawny z on_event (w tym /zf raid) i z ticka
            flush_contracts(); // zlecenia z ticka i z /zf kontrakt
            flush_reputations(); // zmiany relacji -> natywna reputacja w grze
            flush_prices();      // zmiany relacji -> cennik w sklepie frakcji

            // Gotowe wypowiedzi z wątku LLM (albo fallbacki po nieudanej generacji).
            for (const zf::LlmResult& res : llm.poll_results()) {
                commands.write_radio_message(res.faction, res.text, res.color, res.priority);
                std::cout << "[brain] radio" << (res.from_llm ? " (LLM)" : " (fallback)") << " ["
                          << res.faction << "]: " << res.text << "\n";
                // Dopisz turę do pamięci dialogu — tylko realne wypowiedzi LLM (nie fallback/
                // placeholder), żeby nie zaśmiecać historii. player_msg pusty poza czatem.
                if (res.from_llm && !res.player_msg.empty()) {
                    auto& dq = dialog[res.faction];
                    // Przycięcie po BAJTACH rozcinało polskie litery (2 bajty) i wstawiało
                    // do promptu śmieć — cofamy się do początku znaku UTF-8.
                    auto clip = [](std::string t) {
                        if (t.size() > 200) {
                            std::size_t n = 200;
                            while (n > 0 && (static_cast<unsigned char>(t[n]) & 0xC0) == 0x80) {
                                --n;
                            }
                            t.resize(n);
                        }
                        return t;
                    };
                    dq.emplace_back(clip(res.player_msg), clip(res.text));
                    while (dq.size() > kDialogTurns) {
                        dq.pop_front();
                    }
                }
                // Twarda bramka na okup w kredytach: konkretna oferta pokryta saldem gracza
                // kończy rajd, choćby model dalej mówił "dawaj więcej" (obserwacja z gry:
                // 4,5B potrafi zapętlić targ i NIGDY nie ustawić odpuszcza=true).
                const std::int64_t oferta = zf::parse_ransom_amount(res.player_msg);
                const std::int64_t prog = engine.cash_ransom_threshold(res.faction, cfg);
                const std::int64_t saldo = engine.player_balance();
                // Ta sama funkcja jedzie w testach (scenariusze + engine_test) — decyzja
                // o pokoju nie może się rozjechać między grą a tym, co sprawdza CI.
                const zf::OcenaOkupu ocena = zf::ocen_oferte_okupu(oferta, prog, saldo);
                const bool oferta_wiazaca = ocena == zf::OcenaOkupu::Wiazaca;
                if (oferta_wiazaca) {
                    std::cout << "[brain] okup kredytowy " << res.faction << ": oferta " << oferta
                              << " kr >= próg " << prog << " kr (saldo " << saldo
                              << ") — pokój niezależnie od decyzji modelu\n";
                } else if (ocena == zf::OcenaOkupu::BezPokrycia) {
                    std::cout << "[brain] okup kredytowy " << res.faction << ": oferta " << oferta
                              << " kr bez pokrycia (saldo " << saldo << ") — pusta obietnica\n";
                }

                if (res.demand_goods && !oferta_wiazaca) {
                    // B+: frakcja żąda trybutu w surowcach zamiast odpuścić — brain dobiera
                    // towar/ilość/deadline z configu i wystawia ransom_demand (drenaż niżej).
                    engine.request_goods_ransom(res.faction, cfg, now);
                } else if (res.deescalate || oferta_wiazaca) {
                    // Frakcja odpuszcza: kwota z wiadomości gracza (Etap 6) — mod pobierze ją
                    // z konta przy stand_down.
                    engine.apply_deescalation(res.faction, cfg, now, oferta);
                }
            }
            for (const auto& sd : engine.take_standdowns()) {
                commands.write_stand_down(sd.first, sd.second);
                std::cout << "[brain] stand_down [" << sd.first << "]"
                          << (sd.second > 0 ? " okup " + std::to_string(sd.second) + " kr" : "")
                          << " — statki rajdu odwołane\n";
            }
            // B+: żądania trybutu (z decyzji LLM albo /zf okup-surowce) -> ransom_demand do moda.
            for (const zf::RansomDemandOut& rd : engine.take_ransom_demands()) {
                commands.write_ransom_demand(rd.faction, rd.item, rd.amount, rd.deadline_s);
                std::cout << "[brain] ransom_demand [" << rd.faction << "] " << rd.amount << "x "
                          << rd.item << " deadline " << rd.deadline_s << "s\n";
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
