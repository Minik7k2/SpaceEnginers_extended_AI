#pragma once

#include <memory>
#include <string>
#include <vector>

#include "config.hpp"

namespace zf {

// Zadanie dla LLM: gotowe prompty + tekst zapasowy (szablon fallback), który
// wraca gdy model zawiedzie (brak modelu, zły JSON po retry, za długa treść).
struct LlmJob {
    std::string faction;
    std::string system_prompt;
    std::string user_prompt;
    std::string fallback_text;
    std::string color;
    int priority = 0;
    // Rozmowa w trakcie wrogości: model dodatkowo decyduje o odpuszczeniu (okup/
    // kapitulacja/rozejm) — używa rozszerzonej gramatyki z polem "odpuszcza".
    bool expect_decision = false;
    std::string player_msg = {}; // surowa wiadomość gracza (do pamięci dialogu); pusta poza czatem
};

struct LlmResult {
    std::string faction;
    std::string text;
    std::string color;
    int priority = 0;
    bool from_llm = false;   // false = poszedł fallback
    bool deescalate = false; // model zdecydował odpuścić (tylko gdy expect_decision)
    bool demand_goods = false; // model zażądał trybutu w surowcach zamiast odpuścić (B+, tylko gdy expect_decision)
    std::string player_msg = {}; // przeniesione z zadania — main dopisuje turę do pamięci dialogu
};

// Głos frakcji (Etap 4): llama.cpp w osobnym wątku, żeby generacja (sekundy na CPU)
// nie blokowała pętli mostka. Zadania wchodzą przez submit(), wyniki wychodzą przez
// poll_results() w pętli głównej. Bez zbudowanego llama.cpp (ZF_WITH_LLM) albo bez
// pliku modelu worker jest nieaktywny — enabled()==false, main pisze fallback sam.
class LlmWorker {
public:
    explicit LlmWorker(const Config& cfg);
    ~LlmWorker();

    LlmWorker(const LlmWorker&) = delete;
    LlmWorker& operator=(const LlmWorker&) = delete;

    bool enabled() const;
    void submit(LlmJob job);
    std::vector<LlmResult> poll_results();

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

// Ścieżka karty persony dla frakcji ("personas/krwawa_reka.md"); pusta gdy frakcja
// nie ma persony (SPRT, SYSTEM itd.) — wtedy radio zostaje przy szablonach.
std::string persona_path(const std::string& faction);

// --- Sanityzacja wyjścia modelu (Etap 5c) ---
// Publiczne, bo (a) mają własne testy (zf_llm_test) — to czysta logika tekstowa,
// jedyna część LLM-u testowalna bez modelu, (b) w budowie bez llama.cpp
// (ZF_WITH_LLM=OFF) funkcje w anonimowym namespace były martwe i wywalały
// -Werror=unused-function, przez co udokumentowany build "bez LLM" się nie kompilował.

// Usuwa z widocznej treści wyciek markera decyzji ("odpuszcza=true"/"odpuszcza: false"),
// który mały model wkleja do pola "tresc" mimo osobnego pola JSON. Sprząta też
// zawisłe separatory na końcu (spacje, przecinki, myślniki ASCII i UTF-8).
std::string strip_decision_leak(std::string s);

// Obcina artefakty wypowiedzi: podpis tagiem frakcji na końcu ("... — KRW."),
// prefiks nazwą nadawcy na początku ("KRW: ...") i uchwyt "@Gracz". Trymuje brzegi.
std::string sanitize_reply(std::string s, const std::string& faction);

} // namespace zf
