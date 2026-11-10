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

// parse_ransom_amount / ocen_oferte_okupu siedzą w engine.hpp — to logika decyzji
// silnika (Engine::on_llm_reply), nie scenariusza.


} // namespace zf
