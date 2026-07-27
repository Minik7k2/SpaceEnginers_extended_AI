// Testy sanityzacji wyjścia modelu (Etap 5c) — jedyna część LLM-u, którą da się
// sprawdzić bez modelu i bez GPU. Każdy przypadek pochodzi z realnego artefaktu
// zaobserwowanego w grze na qwen2.5-3b (patrz docs/testy-reczne.md, F13/H7).
#include <cassert>
#include <iostream>
#include <string>

#include "llm.hpp"

int main() {
    // --- strip_decision_leak: marker decyzji nie może trafić do treści radia ---
    assert(zf::strip_decision_leak("Niech ci będzie, spadaj. odpuszcza=true") ==
           "Niech ci będzie, spadaj.");
    assert(zf::strip_decision_leak("Nie ma mowy odpuszcza: false") == "Nie ma mowy");
    assert(zf::strip_decision_leak("Bierzemy kasę odpuszcza true.") == "Bierzemy kasę");
    // Wielkość liter bez znaczenia + sprzątanie zawisłego myślnika UTF-8.
    assert(zf::strip_decision_leak("Niech będzie — ODPUSZCZA=TRUE") == "Niech będzie");
    // Marker w środku zdania też znika, reszta zostaje.
    assert(zf::strip_decision_leak("Dobra odpuszcza=true, spadaj stąd.") == "Dobra, spadaj stąd.");
    // Zwykła wypowiedź bez markera ma przejść bez zmian (żadnego obcinania treści).
    assert(zf::strip_decision_leak("Płać albo giń.") == "Płać albo giń.");
    // Samo słowo "odpuszcza" bez true/false to normalna polszczyzna — nie ruszamy.
    assert(zf::strip_decision_leak("Krwawa Ręka nie odpuszcza nikomu.") ==
           "Krwawa Ręka nie odpuszcza nikomu.");

    // --- sanitize_reply: podpis, prefiks, uchwyt gracza ---
    assert(zf::sanitize_reply("Spadaj stąd. - KRW", "KRW") == "Spadaj stąd.");
    assert(zf::sanitize_reply("Spadaj stąd. — KRW.", "KRW") == "Spadaj stąd.");
    assert(zf::sanitize_reply("KRW: Spadaj stąd.", "KRW") == "Spadaj stąd.");
    assert(zf::sanitize_reply("  Spadaj stąd.  ", "KRW") == "Spadaj stąd.");
    assert(zf::sanitize_reply("@Gracz, płać.", "KRW") == "Gracz, płać.");
    // Tag w środku zdania NIE jest podpisem — treść zostaje nietknięta.
    assert(zf::sanitize_reply("Tu KRW mówi, słuchaj.", "KRW") == "Tu KRW mówi, słuchaj.");
    // Sama pusta/białoznakowa odpowiedź => pusto (main takiej nie nadaje).
    assert(zf::sanitize_reply("   ", "KRW").empty());
    // Bez tagu frakcji (SYSTEM/SPRT) obcinamy tylko brzegi i @Gracz.
    assert(zf::sanitize_reply(" Nic tu po tobie. ", "") == "Nic tu po tobie.");

    // --- persona_path: tylko nasze frakcje mają karty person ---
    assert(zf::persona_path("KRW") == "personas/krwawa_reka.md");
    assert(zf::persona_path("HEL") == "personas/helion.md");
    assert(zf::persona_path("WGR") == "personas/gornicy.md");
    assert(zf::persona_path("SPRT").empty() && "obca frakcja nie ma persony");

    std::cout << "zf_llm_test: OK\n";
    return 0;
}
