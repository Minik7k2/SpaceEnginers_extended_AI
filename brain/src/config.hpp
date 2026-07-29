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
    int llm_threads = 0; // 0 = auto (połowa wątków sprzętowych)

    // [relacje]
    double prog_sojusznik = 40;
    double prog_wrogi = -30;
    double prog_wojna = -60;
    double histereza_wyjscie_z_wojny = -50;
    double dryf_pkt = 1;
    int dryf_co_minut = 120;

    // [zmiany] — delty relacji; ujemne = pogorszenie.
    double ostrzal_min = -5;
    double ostrzal_max = -15;
    double zniszczenie_statku = -30;
    double zniszczenie_stacji = -50;
    double sufit_po_zniszczeniu_stacji = 20;
    double handel_min = 1;
    double handel_max = 3;
    double kontrakt_min = 10;
    double kontrakt_max = 20;
    double atak_na_wroga_bonus = 5;
    // Bonus "wróg mojego wroga" leciał przy KAŻDYM combat_hit, a mod agreguje trafienia co
    // 3 s — jedna strzelanina dawała +15 i więcej u wszystkich wrogów ostrzelanej frakcji.
    // Cooldown liczony per para (obserwator, ostrzelany).
    int atak_na_wroga_cooldown_min = 10;
    double deeskalacja_bonus = 15;   // przyjęty okup/kapitulacja/rozejm: relacja rośnie,
                                     // ale trwałe modyfikatory (sufit) zostają — spokój, nie amnestia
    // Twarda bramka na okup w kredytach: model 4,5B potrafi w nieskończoność mówić „dawaj
    // więcej" i nigdy nie ustawić odpuszcza=true, więc oferta >= progu wymusza pokój bez
    // pytania modelu o zdanie. 0 = wyłączone (decyduje wyłącznie LLM).
    std::int64_t deeskalacja_prog_kredyty = 3000;
    // Im gorzej was widzą, tym drożej: próg rośnie o ten ułamek za każdy punkt relacji
    // poniżej zera (0.01 => przy relacji -80 próg jest 1,8x wyższy).
    double deeskalacja_prog_za_punkt = 0.01;

    // [tick]
    int tick_co_minut = 4;
    double szansa_zdarzenia_losowego = 0.35;
    int budzet_akcji_na_tick = 1;

    // [radio]
    int radio_limit_na_frakcje_na_min = 1;
    int radio_ttl_sekund = 120;
    bool radio_wymagaj_zasiegu = true; // 5c: @FRAKCJA odpowiada tylko gdy w zasięgu (mod liczy)

    // [spawn] — spawny statków frakcji sterowane maszyną stanów (Etap 5).
    bool spawn_wlaczone = true;    // false wyłącza auto-spawny; /zf raid działa niezależnie
    int spawn_cooldown_min = 5;    // min. odstęp między auto-spawnami TEJ SAMEJ frakcji

    // [okup_surowce] — okup w SUROWCACH (B+): w trakcie rajdu frakcja może zażądać
    // trybutu (konkretny surowiec dostarczony do skrzynki zrzutu w oknie czasu).
    std::string okup_towary = "Iron,Nickel,Silicon,Cobalt"; // dozwolone surowce (klucze); brain losuje jeden, mod tłumaczy na SubtypeId + nazwę PL
    int okup_ilosc_min = 300;         // dolna granica losowanej ilości trybutu
    int okup_ilosc_max = 800;         // górna granica
    int okup_deadline_s = 900;        // 15 min na dostarczenie
    double okup_bonus_dostawa = 20;   // relacja += po dostarczeniu (jak wykonany kontrakt)
    double okup_kara_zlamanie = 5;    // relacja -= za złamaną obietnicę (główna kara: trwała nieufność)

    // [reputacja] — rzutowanie naszej relacji (-100..+100) na NATYWNĄ reputację SE
    // (-1500..+1500). Silnik relacji zostaje źródłem prawdy; mod tylko zapisuje wynik
    // przez SetReputationBetweenPlayerAndFaction, żeby gracz widział tę samą liczbę
    // w oknie frakcji, a wieżyczki/ceny/strefy reagowały na nasze wojny i sojusze.
    bool reputacja_sync = true;
    int reputacja_zakres = 1500;     // |reputacja| w vanilli przy naszym ±100
    int reputacja_prog = 500;        // vanilla: ≤ -500 wróg, ≥ +500 sojusznik
    bool reputacja_polityka = true;  // synchronizuj też relacje frakcja↔frakcja (SetReputation)

    // [kontrakty] — Etap 6: frakcja sama wystawia zlecenia (mod tworzy je przez
    // MyAPIGateway.ContractSystem). To jedyna realna droga ODKUPIENIA: dryf leczy
    // 1 pkt / 2 h, a wykonany kontrakt daje +kontrakt_max od ręki.
    bool kontrakty_wlaczone = true;
    double kontrakty_prog_relacji = -55;  // poniżej tej relacji frakcja nie da ci roboty
    int kontrakty_cooldown_min = 20;      // min. odstęp między ofertami TEJ SAMEJ frakcji
    int kontrakty_max_otwartych = 1;      // ile otwartych zleceń naraz na frakcję
    int kontrakty_nagroda_min = 15000;    // widełki nagrody w kredytach (skalowane relacją)
    int kontrakty_nagroda_max = 60000;
    int kontrakty_czas_min = 45;          // czas na wykonanie (minuty)
};

// Rzuca std::runtime_error gdy plik nie istnieje lub brakuje wymaganego pola
// [bridge].storage_dir. Po wczytaniu pliku głównego nakłada wartości z pliku
// lokalnego maszyny (rules.local.toml obok rules.toml, poza gitem), jeśli istnieje.
Config load_config(const std::string& path);

// "configs/rules.toml" -> "configs/rules.local.toml"
std::string local_config_path(const std::string& path);

// Hot-reload configu: pamięta mtime rules.toml + rules.local.toml i przeładowuje
// przy zmianie któregokolwiek (CLAUDE.md: mtime check co tick pętli).
class ConfigWatcher {
public:
    explicit ConfigWatcher(std::string path);

    const Config& get() const { return config_; }
    // Zwraca true gdy config został właśnie przeładowany. Błąd parsowania po
    // zmianie pliku nie wywala pętli — zostaje poprzedni config, błąd na stderr.
    bool poll();

private:
    std::string path_;
    Config config_;
    std::int64_t mtime_main_ = 0;
    std::int64_t mtime_local_ = 0;
    void read_mtimes(std::int64_t& main_out, std::int64_t& local_out) const;
};

} // namespace zf
