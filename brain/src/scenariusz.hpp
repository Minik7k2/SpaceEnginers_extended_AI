#pragma once

#include <cstdint>
#include <iosfwd>
#include <string>

#include "config.hpp"

namespace zf {

// Scenariusz: plik JSONL, w którym obok ZDARZEŃ z gry stoją OCZEKIWANIA. Dzięki temu
// brainowa połowa mechaniki (ceny, okup, kontrakty, reputacja) ma testy regresji bez
// odpalania Space Engineers — do tej pory CI puszczało --replay i robiło `grep` na
// stdout, czyli sprawdzało tylko tyle, że brain się nie wywrócił.
//
// Rodzaje linii (po jednym obiekcie JSON na linię):
//   {"opis": "..."}                      — nagłówek sekcji w logu (nic nie sprawdza)
//   {"type": "...", "data": {...}, "ts": N}  — zdarzenie, dokładnie jak w events.jsonl
//   {"restart": true}                    — nowy Engine na TEJ SAMEJ bazie (test trwałości:
//                                          co przeżyje restart brainu, a co siedziało w RAM)
//   {"oczekuj": "<rodzaj>", ...}         — asercja na tym, co wyprodukowało OSTATNIE zdarzenie
//
// Okno widoczności: bufory wyjść są czyszczone przed każdym zdarzeniem, więc wszystkie
// oczekiwania stojące po zdarzeniu widzą dokładnie jego skutki (i nic starszego).
//
// Rodzaje oczekiwań i ich pola — patrz brain/tests/scenariusze/*.jsonl oraz
// implementacja w scenariusz.cpp (funkcja sprawdz_oczekiwanie).
struct WynikScenariusza {
    int sprawdzen = 0;
    int bledow = 0;
    bool plik_wczytany = false;
};

// Odtwarza scenariusz na świeżej bazie w pamięci. Log leci na `out`; zwraca licznik
// sprawdzeń i błędów (0 błędów = PASS). Plik BEZ linii "oczekuj" zachowuje się jak
// dawny --replay: odtwarza zdarzenia i wypisuje, co brain z nimi zrobił.
WynikScenariusza uruchom_scenariusz(const std::string& sciezka, const Config& cfg,
                                    const std::string& fallback_path, std::ostream& out);

// Kwota okupu wyłuskana z wiadomości gracza: pierwszy ciąg 2–12 cyfr
// ("biorę okup, oto 4000 sztabek" -> 4000). 0 = brak kwoty (okup symboliczny).
// Bez wyjątków (reguła pętli mostka): akumulacja ręczna, absurdalnie długie ciągi pomijane.
std::int64_t parse_ransom_amount(const std::string& text);

// Ocena oferty okupu w kredytach. Wydzielona z main.cpp, żeby ta sama decyzja szła
// do gry i do testów — obserwacja z gry była taka, że model potrafi zapętlić targ
// i NIGDY nie ustawić odpuszcza=true, więc konkretna oferta pokryta saldem musi
// kończyć rajd niezależnie od niego.
enum class OcenaOkupu {
    Brak,          // bramka wyłączona configiem albo saldo gracza nieznane
    ZaMalo,        // oferta poniżej progu — targ trwa
    BezPokrycia,   // oferta >= próg, ale gracz tyle nie ma — pusta obietnica
    Wiazaca,       // oferta >= próg i pokryta saldem — pokój niezależnie od modelu
};

OcenaOkupu ocen_oferte_okupu(std::int64_t oferta, std::int64_t prog, std::int64_t saldo);

} // namespace zf
