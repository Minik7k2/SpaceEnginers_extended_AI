#pragma once

#include <cstdint>
#include <initializer_list>
#include <map>
#include <random>
#include <set>
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
    bool expect_decision = false; // rozmowa w trakcie wrogości: LLM decyduje o odpuszczeniu
    std::string player_msg = {};  // surowa wiadomość gracza — do pamięci dialogu (pusta poza czatem)
};

// Zlecenie spawnu statku frakcji do CommandWriter (Etap 5). Decyzje podejmuje
// maszyna stanów (napięcie->patrol, wojna->raid, zdarzenie->convoy) albo komenda
// /zf raid. Silnik zbiera je w buforze, który main opróżnia przez take_spawns().
struct SpawnOut {
    std::string faction;
    std::string kind;      // patrol/raid/convoy
    std::string context;   // opis sytuacji po polsku (log/atrybucja)
    bool near_player = true;
};

// Żądanie trybutu w surowcach do CommandWriter (B+). Brain dobiera surowiec/ilość/
// deadline z configu ([okup_surowce]); mod stawia skrzynkę zrzutu, wstrzymuje ogień
// i pilnuje terminu. Zebrane w buforze, który main opróżnia przez take_ransom_demands().
struct RansomDemandOut {
    std::string faction;
    std::string item;         // logiczny klucz surowca (mod tłumaczy na SubtypeId + nazwę PL)
    std::int64_t amount = 0;
    int deadline_s = 0;
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

    // Zlecenia spawnu nazbierane przez on_event/tick — zwraca i czyści bufor.
    // Radio wraca wartością z on_event/tick; spawny osobnym kanałem, żeby nie
    // zmieniać typu zwrotu tamtych (i nie ruszać testów silnika).
    std::vector<SpawnOut> take_spawns();

    // Reakcja na decyzję LLM o odpuszczeniu (okup/kapitulacja/rozejm), wołana z main
    // po odebraniu wyniku z wątku LLM. Samobramkuje się: jeśli frakcja nie ma
    // aktywnego rajdu, nic nie robi. W przeciwnym razie: delta relacji (deeskalacja_bonus),
    // pamięć, gaśnie flaga rajdu i leci stand_down do moda (take_standdowns()).
    // amount>0 = realny okup w kredytach (Etap 6): mod pobiera tyle z konta gracza na
    // konto frakcji przy stand_down. 0 = de-eskalacja symboliczna (/zf okup, brak kwoty).
    void apply_deescalation(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                            std::int64_t amount = 0);
    // Pary (frakcja, kwota_okupu) do wysłania jako stand_down.
    std::vector<std::pair<std::string, std::int64_t>> take_standdowns();

    // Reakcja na decyzję LLM (albo /zf okup-surowce) o zażądaniu trybutu w surowcach (B+),
    // wołana z main. Samobramkuje się: bez aktywnego rajdu nic nie robi; jeśli okup już
    // wisi dla tej frakcji, nie ponawia. Dobiera surowiec/ilość/deadline z configu, zapisuje
    // pamięć i wystawia ransom_demand (take_ransom_demands()). Relacji NIE rusza — nagroda
    // przychodzi dopiero przy dostawie (ransom_paid). Zawieszenie ognia realizuje mod.
    void request_goods_ransom(const std::string& faction, const Config& cfg, std::int64_t now_ms);
    // Żądania trybutu nazbierane przez decyzję/komendę — zwraca i czyści bufor.
    std::vector<RansomDemandOut> take_ransom_demands();

private:
    Db& db_;
    Fallback& fallback_;
    std::mt19937 rng_;
    std::map<std::string, std::int64_t> last_radio_ms_;
    std::map<std::string, std::int64_t> last_spawn_ms_;
    std::vector<SpawnOut> pending_spawns_;
    std::set<std::string> active_raids_;          // frakcje z aktywnym rajdem (można je odwołać)
    std::vector<std::pair<std::string, std::int64_t>> pending_standdowns_; // (frakcja, kwota okupu)
    std::set<std::string> pending_ransoms_;       // frakcje z wystawionym okupem surowcowym (anty-dublowanie)
    std::vector<RansomDemandOut> pending_ransom_demands_; // do wysłania jako ransom_demand

    void ensure_known_faction(const std::string& tag);
    // Czy rozmowa z frakcją ma pozwolić LLM zdecydować o odpuszczeniu — gdy trwa
    // aktywny rajd albo frakcja jest w napięciu/wojnie z graczem.
    bool chat_expects_decision(const std::string& faction) const;
    // Pierwszy istniejący szablon z listy kandydatów; pusty string gdy żadnego nie ma.
    std::string render_first(const std::string& faction, std::initializer_list<const char*> kinds,
                             const std::map<std::string, std::string>& vars = {}) const;
    // Emisja z limitem częstotliwości per frakcja (priority 1 = krótszy cooldown).
    // kind/context opisują intencję dla LLM; do context doklejany jest stan relacji.
    void emit(std::vector<RadioOut>& out, const std::string& faction, const std::string& text,
              int priority, const Config& cfg, std::int64_t now_ms,
              const std::string& kind = {}, std::string context = {}, bool expect_decision = false,
              std::string player_msg = {});
    // Przejścia spokoj/napiecie/wojna wg relacji do gracza (histereza wyjścia z wojny).
    void update_state(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                      std::vector<RadioOut>& out);
    // Dokłada SpawnOut do bufora z limitem częstotliwości per frakcja. force=true
    // (komenda /zf raid) omija cooldown i globalny włącznik spawn_wlaczone.
    void request_spawn(const std::string& faction, const std::string& kind, const Config& cfg,
                       std::int64_t now_ms, std::string context, bool force = false);

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
    // Etap 6 — ekonomia: handel podnosi relację (+1..+3), wykonany kontrakt (+kontrakt_max),
    // zawalony (-kontrakt_min); status kontraktu utrwalany w SQLite.
    void handle_trade(const Event& ev, const Config& cfg, std::int64_t now_ms,
                      std::vector<RadioOut>& out);
    void handle_contract_done(const Event& ev, const Config& cfg, std::int64_t now_ms,
                              std::vector<RadioOut>& out);
    // B+ — okup w surowcach: dostawa trybutu w oknie (relacja rośnie, wiarygodność
    // odbudowana czynem, rajd odwołany) i przekroczony deadline (trwała nieufność,
    // drobna kara relacji, ataki trwają dalej).
    void handle_ransom_paid(const Event& ev, const Config& cfg, std::int64_t now_ms,
                            std::vector<RadioOut>& out);
    void handle_ransom_expired(const Event& ev, const Config& cfg, std::int64_t now_ms,
                               std::vector<RadioOut>& out);
};

// Kolor czatu frakcji (CLAUDE.md): HEL niebieski, KRW czerwony, WGR żółty.
std::string faction_color(const std::string& tag);

} // namespace zf
