#pragma once

#include <cstdint>
#include <initializer_list>
#include <map>
#include <random>
#include <string>
#include <vector>

#include "bridge.hpp"
#include "config.hpp"
#include "db.hpp"
#include "fallback.hpp"

namespace zf {

// Wiadomość radiowa do wysłania przez CommandWriter (albo na stdout w --replay).
// text to zawsze gotowy szablon fallback; kind+context to intencja dla LLM (Etap 4):
// gdy model działa, main podmienia text na wypowiedź wygenerowaną z person.
// Pusty kind (np. raport /zf rel) = nigdy nie przechodzi przez LLM.
struct RadioOut {
    std::string faction;
    std::string text;
    std::string color = "white";
    int priority = 0;   // 1 = bojowe/wojna (krótszy cooldown radia)
    std::string kind;      // grozba/kpina/neutral/... — rodzaj wypowiedzi
    std::string context;   // opis sytuacji po polsku do promptu LLM
};

// Silnik relacji (Etap 3): reguły zmian z configu, maszyna stanów frakcji
// (spokoj/napiecie/wojna z histerezą), tick świata z dryfem i zdarzeniem losowym,
// głos przez szablony fallback (do Etapu 4 zawsze "mock LLM").
class Engine {
public:
    Engine(Db& db, Fallback& fallback, std::uint32_t rng_seed);

    // Natychmiastowa reakcja na zdarzenie z gry (poza tickiem).
    std::vector<RadioOut> on_event(const Event& ev, const Config& cfg, std::int64_t now_ms);

    // Tick świata co cfg.tick_co_minut (force = komenda /zf tick). Kolejność:
    // dryf -> maszyna stanów -> budżet akcji -> zdarzenie losowe ważone stanem.
    std::vector<RadioOut> tick(const Config& cfg, std::int64_t now_ms, bool force = false);

    // Raport do /zf rel: relacja frakcja->gracz i stan każdej frakcji.
    std::string relations_report() const;

private:
    Db& db_;
    Fallback& fallback_;
    std::mt19937 rng_;
    std::map<std::string, std::int64_t> last_radio_ms_;

    void ensure_known_faction(const std::string& tag);
    // Pierwszy istniejący szablon z listy kandydatów; pusty string gdy żadnego nie ma.
    std::string render_first(const std::string& faction, std::initializer_list<const char*> kinds,
                             const std::map<std::string, std::string>& vars = {}) const;
    // Emisja z limitem częstotliwości per frakcja (priority 1 = krótszy cooldown).
    // kind/context opisują intencję dla LLM; do context doklejany jest stan relacji.
    void emit(std::vector<RadioOut>& out, const std::string& faction, const std::string& text,
              int priority, const Config& cfg, std::int64_t now_ms,
              const std::string& kind = {}, std::string context = {});
    // Przejścia spokoj/napiecie/wojna wg relacji do gracza (histereza wyjścia z wojny).
    void update_state(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                      std::vector<RadioOut>& out);

    void handle_combat_hit(const Event& ev, const Config& cfg, std::int64_t now_ms,
                           std::vector<RadioOut>& out);
    void handle_grid_destroyed(const Event& ev, const Config& cfg, std::int64_t now_ms,
                               std::vector<RadioOut>& out);
    void handle_proximity(const Event& ev, const Config& cfg, std::int64_t now_ms,
                          std::vector<RadioOut>& out);
    void handle_chat(const Event& ev, const Config& cfg, std::int64_t now_ms,
                     std::vector<RadioOut>& out);
    void handle_debug(const Event& ev, const Config& cfg, std::int64_t now_ms,
                      std::vector<RadioOut>& out);
};

// Kolor czatu frakcji (CLAUDE.md): HEL niebieski, KRW czerwony, WGR żółty.
std::string faction_color(const std::string& tag);

} // namespace zf
