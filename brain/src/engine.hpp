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

// Rzutowanie relacji na NATYWNĄ reputację SE (hybryda). Silnik zostaje źródłem prawdy
// (bo tylko on ma sufity relacji, histerezę i pamięć), a mod zapisuje `vanilla` przez
// MyAPIGateway.Session.Factions — gracz widzi jedną liczbę w oknie frakcji, a wieżyczki
// i ceny reagują na nasze wojny. other pusty = relacja frakcja→gracz; niepusty = tag
// drugiej frakcji (polityka frakcja↔frakcja).
struct ReputationOut {
    std::string faction;
    std::string other;
    double value = 0;   // nasza skala -100..+100 (log/diagnostyka)
    int vanilla = 0;    // skala gry, -zakres..+zakres
};

// Cennik sklepu frakcji przepisany z relacji (Etap 6). Druga strona hybrydy: reputacja
// zmienia liczbę w oknie frakcji, cennik zmienia to, ile gracz płaci przy ladzie. Mod
// mnoży PricePerUnit ofert przez `modifier` (patrz Prices.cs); przy `embargo` w ogóle
// zdejmuje oferty ze sklepu — frakcja nie chce z tobą handlować.
struct PriceOut {
    std::string faction;
    double value = 0;       // nasza skala -100..+100 (log/diagnostyka)
    double modifier = 1.0;  // mnożnik CEN BAZOWYCH (nie bieżących — mod pamięta bazę)
    bool embargo = false;
};

// Zlecenie wystawienia kontraktu (Etap 6). Silnik decyduje KIEDY i ZA ILE, mod
// tworzy kontrakt przez MyAPIGateway.ContractSystem na bloku swojej frakcji i
// odsyła contract_created z prawdziwym ID (dopiero wtedy trafia do SQLite).
struct ContractOut {
    std::string faction;
    // dostawa | nagroda | transport | naprawa | poszukiwania | eskorta | wlasne
    // (Config::contract_kinds(); każdy ma swoją klasę w Sandbox.ModAPI.Contracts).
    // Silnik wybiera typ wagami z [kontrakty.typy], ale OSTATNIE słowo ma mod: jeśli
    // nie znajdzie w świecie celu (wrogiego pilota, drugiego bloku, uszkodzonej
    // siatki...), wystawia dostawę i to ona wraca w contract_created.
    std::string kind;
    std::int64_t reward = 0;   // kredyty (po mnożniku trudności typu)
    int duration_min = 45;
    // Tylko dla "nagroda": frakcja, na której głowę idzie zlecenie (najgorsza relacja
    // wystawcy wg polityki). Mod tłumaczy tag na identity właściciela jej siatki.
    std::string target_faction;
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

    // Raport do /zf rel: relacja frakcja->gracz i stan każdej frakcji, a po "||"
    // polityka między frakcjami.
    std::string relations_report() const;

    // Same relacje frakcja↔frakcja ("HEL/KRW -70 | ..."), bez gracza.
    std::string politics_report() const;

    // Zlecenia spawnu nazbierane przez on_event/tick — zwraca i czyści bufor.
    // Radio wraca wartością z on_event/tick; spawny osobnym kanałem, żeby nie
    // zmieniać typu zwrotu tamtych (i nie ruszać testów silnika).
    std::vector<SpawnOut> take_spawns();

    // Zlecenia kontraktów nazbierane w ticku — analogicznie do take_spawns().
    std::vector<ContractOut> take_contracts();

    // Zmiany reputacji do przepisania na stronę gry — analogicznie do take_spawns().
    std::vector<ReputationOut> take_reputations();

    // Zmiany cennika sklepów do przepisania na stronę gry — analogicznie do take_spawns().
    std::vector<PriceOut> take_prices();

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

    // Ile kredytów gracz musi położyć na stole, żeby frakcja MUSIAŁA odwołać atak
    // niezależnie od decyzji modelu (0 = bramka wyłączona configiem).
    std::int64_t cash_ransom_threshold(const std::string& faction, const Config& cfg) const;
    // Ostatnie znane saldo gracza (mod dosyła je w chat_message). <0 = nieznane, wtedy
    // bramka kredytowa milczy — nie kupujemy obietnic, których nie da się sprawdzić.
    std::int64_t player_balance() const { return player_balance_; }

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
    // Wiszące okupy surowcowe: anty-dublowanie + treść żądania, żeby frakcja umiała
    // odpowiedzieć na "ile mi zostało czasu?" (bez tego LLM zmyślał).
    struct PendingRansom {
        std::string item;
        std::int64_t amount = 0;
        std::int64_t deadline_ms = 0;
    };
    std::map<std::string, PendingRansom> pending_ransoms_;
    std::vector<RansomDemandOut> pending_ransom_demands_; // do wysłania jako ransom_demand
    // Ostatnio wysłane do gry wartości reputacji ("HEL" / "HEL|KRW" -> wartość vanilli).
    // W pamięci: przy starcie świata mod i tak dostaje pełny resync (session_start), a
    // wysłanie tej samej liczby drugi raz nic nie psuje.
    std::map<std::string, int> last_vanilla_;
    std::vector<ReputationOut> pending_reputations_;
    bool reputacja_byla_wlaczona_ = false; // hot-reload: włączenie synchronizacji = pełny resync
    // Ostatnio wysłany cennik per frakcja. W pamięci z tego samego powodu co reputacja:
    // mod dostaje pełny resync po session_start, a ceny bazowe pamięta u siebie.
    struct LastPrice {
        double modifier = 1.0;
        bool embargo = false;
    };
    std::map<std::string, LastPrice> last_price_;
    std::vector<PriceOut> pending_prices_;
    bool ceny_byly_wlaczone_ = false;
    // Cooldown bonusu "wróg mojego wroga": (obserwator, ostrzelany) -> ostatnia wypłata.
    // W pamięci jak cooldown radia — chodzi o minuty, restart brainu niczego nie psuje.
    std::map<std::pair<std::string, std::string>, std::int64_t> enemy_bonus_at_;
    std::int64_t player_balance_ = -1; // ostatnie saldo z chat_message; <0 = nieznane

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
    // Startowe relacje frakcja↔frakcja (raz na świat) — bez nich polityka nie istnieje.
    void seed_faction_politics();
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
    // Po każdej obsłudze zdarzenia i po ticku: przelicz relacje naszych frakcji na skalę
    // gry i wystaw do wysyłki te, które się zmieniły. force = wyślij wszystko (start
    // sesji, włączenie synchronizacji configiem) — wtedy mod nie zgaduje, co przegapił.
    void sync_reputations(const Config& cfg, bool force);
    // To samo dla cennika sklepów: przelicz relacje na mnożnik cen i wystaw do wysyłki te
    // frakcje, u których cennik naprawdę drgnął (prog_zmiany) albo zmienił się embargo.
    void sync_prices(const Config& cfg, bool force);

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
    // Mod potwierdza, że kontrakt naprawdę powstał w grze i podaje jego ID —
    // dopiero teraz zapisujemy go w SQLite (wymóg: przeżyć wczytanie świata).
    void handle_contract_created(const Event& ev, const Config& cfg, std::int64_t now_ms,
                                 std::vector<RadioOut>& out);
    // Gracz PRZYJĄŁ zlecenie w terminalu. To moment, w którym świat ma zareagować:
    // frakcja potwierdza przez radio, wrogowie wystawcy tracą zaufanie, a przy
    // eskorcie dopiero teraz rusza konwój (wcześniej nie było czego eskortować).
    // Idempotentne — status 'taken' w SQLite pilnuje, by zadziałało raz na kontrakt.
    void handle_contract_taken(const Event& ev, const Config& cfg, std::int64_t now_ms,
                               std::vector<RadioOut>& out);
    // Tick: czy frakcja wystawia teraz zlecenie (relacja, cooldown, limit otwartych).
    void maybe_offer_contract(const std::string& faction, const Config& cfg, std::int64_t now_ms,
                              std::vector<RadioOut>& out);
    // Losowanie typu zlecenia wagami z [kontrakty.typy] (nadpisania per frakcja, podbicie
    // "nagrody" w napięciu/wojnie). Typy bez sensownego celu są odsiewane: nagroda wymaga
    // frakcji, z którą wystawca jest na bakier. "" = brak choćby jednego typu z wagą > 0.
    std::string pick_contract_kind(const std::string& faction, const Config& cfg);
    // Wystawia zlecenie do bufora: nagroda po mnożniku trudności, cel dla nagrody,
    // a dla eskorty dorzuca konwój przez MES (nie ma czego eskortować bez statku).
    void queue_contract(const std::string& faction, const std::string& kind, double relation,
                        const Config& cfg, std::int64_t now_ms);
    // Frakcja, na której głowę wystawca może dać nagrodę: najgorsza relacja w polityce
    // i musi być poniżej prog_wrogi. "" = wystawca z nikim nie jest na wojennej stopie.
    std::string worst_enemy_of(const std::string& faction, const Config& cfg) const;
};

// Kolor czatu frakcji (CLAUDE.md): HEL niebieski, KRW czerwony, WGR żółty.
std::string faction_color(const std::string& tag);

// Nasza relacja (-100..+100) -> natywna reputacja SE (-zakres..+zakres). Odcinkowo
// liniowa, z węzłami w progach: prog_wrogi -> tuż poniżej -prog (w grze „wróg"),
// prog_sojusznik -> tuż powyżej +prog („sojusznik"), 0 -> 0, ±100 -> ±zakres.
// Dzięki temu etykieta w oknie frakcji zgadza się z tym, co mówi /zf rel.
int vanilla_reputation(double value, const Config& cfg);

// Nasza relacja (-100..+100) -> mnożnik cen w sklepie frakcji. Odcinkowo liniowa z węzłem
// w zerze: -100 -> ceny_mnoznik_wrog, 0 -> 1.0 (cennik nietknięty), +100 ->
// ceny_mnoznik_sojusznik. Wynik jest przycięty do sensownego pasma, żeby literówka
// w configu nie zrobiła ze sklepu rozdawnictwa ani ceny nie do zapłacenia.
double price_modifier(double value, const Config& cfg);

} // namespace zf
