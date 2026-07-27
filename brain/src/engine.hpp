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

// Zlecenie wystawienia kontraktu (Etap 6). Silnik decyduje KIEDY i ZA ILE, mod
// tworzy kontrakt przez MyAPIGateway.ContractSystem na bloku swojej frakcji i
// odsyła contract_created z prawdziwym ID (dopiero wtedy trafia do SQLite).
struct ContractOut {
    std::string faction;
    std::string kind;      // dostawa (na razie jedyny rodzaj: MyContractAcquisition)
    std::int64_t reward = 0;   // kredyty
    int duration_min = 45;
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

    // Zlecenia kontraktów nazbierane w ticku — analogicznie do take_spawns().
    std::vector<ContractOut> take_contracts();

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

private:
    Db& db_;
    Fallback& fallback_;
    std::mt19937 rng_;
    // Cooldown radia zostaje w pamięci celowo: liczy się w sekundach, więc jego utrata
    // przy restarcie brainu jest niezauważalna (najwyżej jedna wiadomość więcej).
    std::map<std::string, std::int64_t> last_radio_ms_;
    std::vector<SpawnOut> pending_spawns_;
    std::vector<ContractOut> pending_contracts_;
    std::vector<std::pair<std::string, std::int64_t>> pending_standdowns_; // (frakcja, kwota okupu)

    // Stan gry długiego oddechu (aktywny rajd, cooldowny spawnu i kontraktów) siedzi
    // w SQLite, nie w pamięci: restart brainu w trakcie rajdu nie może kończyć się tym,
    // że statki dalej atakują, a frakcja "nie prowadzi rajdu" i nie da się zapłacić okupu.
    static std::string raid_key(const std::string& tag) { return "__raid__" + tag; }
    static std::string spawn_key(const std::string& tag) { return "__last_spawn__" + tag; }
    static std::string contract_key(const std::string& tag) { return "__last_contract__" + tag; }
    // Rajd starszy niż to uznajemy za wygasły (MES i tak w końcu despawnuje statki) —
    // inaczej flaga z wczorajszej sesji wisiałaby w bazie w nieskończoność.
    static constexpr std::int64_t kRaidTtlMs = 60 * 60 * 1000;
    bool has_active_raid(const std::string& faction, std::int64_t now_ms) const;
    void set_active_raid(const std::string& faction, std::int64_t now_ms); // 0 = odwołaj

    void ensure_known_faction(const std::string& tag);
    // Czy rozmowa z frakcją ma pozwolić LLM zdecydować o odpuszczeniu — gdy trwa
    // aktywny rajd albo frakcja jest w napięciu/wojnie z graczem.
    bool chat_expects_decision(const std::string& faction, std::int64_t now_ms) const;
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
    // Mod potwierdza, że kontrakt naprawdę powstał w grze i podaje jego ID —
    // dopiero teraz zapisujemy go w SQLite (wymóg: przeżyć wczytanie świata).
    void handle_contract_created(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                 std::vector<RadioOut>& out);
    // Tick: czy frakcja wystawia teraz zlecenie (relacja, cooldown, limit otwartych).
    void maybe_offer_contract(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                              std::vector<RadioOut>& out);
};

// Kolor czatu frakcji (CLAUDE.md): HEL niebieski, KRW czerwony, WGR żółty.
std::string faction_color(const std::string& tag);

} // namespace zf
