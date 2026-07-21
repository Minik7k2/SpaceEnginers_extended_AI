#include "llm.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <sstream>
#include <thread>

#include <nlohmann/json.hpp>

#ifdef ZF_WITH_LLM
#include <llama.h>
#endif

namespace zf {

std::string persona_path(const std::string& faction) {
    if (faction == "HEL") {
        return "personas/helion.md";
    }
    if (faction == "KRW") {
        return "personas/krwawa_reka.md";
    }
    if (faction == "WGR") {
        return "personas/gornicy.md";
    }
    return {};
}

namespace {

// Gramatyka GBNF wymuszająca dokładnie {"tresc":"...","ton":"..."} (CLAUDE.md).
// Limit znaków treści pilnuje walidacja (config llm.max_chars) — gramatyka daje
// twardy strop, żeby model nie mógł uciec w elaborat.
constexpr const char* kRadioGrammar = R"GBNF(
root ::= "{\"tresc\":\"" tresc "\",\"ton\":\"" ton "\"}"
tresc ::= znak{1,220}
ton ::= [a-zA-ZąćęłńóśźżA-ŻĄĆĘŁŃÓŚŹŻ ]{2,24}
znak ::= [^"\\\x0A\x0D] | "\\" ["\\nt]
)GBNF";

} // namespace

#ifdef ZF_WITH_LLM

struct LlmWorker::Impl {
    llama_model* model = nullptr;
    llama_context* ctx = nullptr;
    const llama_vocab* vocab = nullptr;
    int max_chars = 200;

    std::thread worker;
    std::mutex mutex;
    std::condition_variable cv;
    std::deque<LlmJob> jobs;
    std::vector<LlmResult> results;
    std::atomic<bool> stop{false};

    ~Impl() {
        stop.store(true);
        cv.notify_all();
        if (worker.joinable()) {
            worker.join();
        }
        if (ctx != nullptr) {
            llama_free(ctx);
        }
        if (model != nullptr) {
            llama_model_free(model);
        }
        llama_backend_free();
    }

    bool init(const Config& cfg) {
        if (cfg.llm_model_path.empty() || !std::filesystem::exists(cfg.llm_model_path)) {
            std::cout << "[brain] model LLM nieobecny (" << cfg.llm_model_path
                      << ") — radio zostaje na szablonach fallback\n";
            return false;
        }
        max_chars = cfg.llm_max_chars;

        // Wycisz wewnętrzne logi llama.cpp/ggml (zrzut tensorów przy ładowaniu,
        // "CUDA Graph id ... reused" co token) — zostaw tylko WARN/ERROR, żeby
        // konsola braina pokazywała nasze [brain] linie, a nie szum. GGML_LOG_CONT
        // to kontynuacja poprzedniej linii — pokazujemy ją tylko, jeśli pokazaliśmy
        // tamtą. llama_log_set ustawia też callback ggml (backendy CUDA).
        llama_log_set(
            [](ggml_log_level level, const char* text, void*) {
                static bool last_shown = false;
                if (level == GGML_LOG_LEVEL_CONT) {
                    if (last_shown) {
                        std::cerr << text;
                    }
                    return;
                }
                last_shown = level >= GGML_LOG_LEVEL_WARN;
                if (last_shown) {
                    std::cerr << text;
                }
            },
            nullptr);

        llama_backend_init();

        llama_model_params mparams = llama_model_default_params();
        mparams.n_gpu_layers = cfg.llm_use_gpu ? 99 : 0;
        model = llama_model_load_from_file(cfg.llm_model_path.c_str(), mparams);
        if (model == nullptr) {
            std::cerr << "[brain] nie udało się wczytać modelu " << cfg.llm_model_path << "\n";
            return false;
        }
        vocab = llama_model_get_vocab(model);

        llama_context_params cparams = llama_context_default_params();
        cparams.n_ctx = 2048;
        // n_batch=512 (domyślne, małe zużycie VRAM — ważne przy 8 GB dzielonym z SE).
        // Prompt dłuższy niż n_batch NIE może iść jednym llama_decode — wywala
        // GGML_ASSERT(n_tokens_all <= n_batch) i abortuje proces. Dłuższy prompt
        // (persony z przykładami few-shot, po polsku = dużo tokenów) dekodujemy więc
        // w porcjach po n_batch, patrz generate().
        cparams.n_batch = 512;
        // AUTO probuje Flash Attention realnym obliczeniem przy starcie (świeży,
        // niepewny kod w tej wersji llama.cpp) — wyłączamy jawnie zamiast zgadywać.
        cparams.flash_attn_type = LLAMA_FLASH_ATTN_TYPE_DISABLED;
        const unsigned hw = std::thread::hardware_concurrency();
        const int threads = cfg.llm_threads > 0 ? cfg.llm_threads
                                                 : static_cast<int>(hw > 2 ? hw / 2 : 2);
        cparams.n_threads = threads;
        cparams.n_threads_batch = threads;
        ctx = llama_init_from_model(model, cparams);
        if (ctx == nullptr) {
            std::cerr << "[brain] nie udało się utworzyć kontekstu LLM\n";
            return false;
        }

        std::cout << "[brain] LLM gotowy: " << cfg.llm_model_path
                  << (cfg.llm_use_gpu ? " (GPU)" : " (CPU)") << ", wątki=" << threads << "\n";
        worker = std::thread([this] { run(); });
        return true;
    }

    void run() {
        while (true) {
            LlmJob job;
            std::size_t queued = 0;
            {
                std::unique_lock<std::mutex> lock(mutex);
                cv.wait(lock, [this] { return stop.load() || !jobs.empty(); });
                if (stop.load()) {
                    return;
                }
                job = std::move(jobs.front());
                jobs.pop_front();
                queued = jobs.size();
            }

            std::cout << "[brain] LLM: generuję odpowiedź dla " << job.faction;
            if (queued > 0) {
                std::cout << " (w kolejce jeszcze " << queued << ")";
            }
            std::cout << "...\n" << std::flush;

            LlmResult result{job.faction, job.fallback_text, job.color, job.priority, false};
            // 1 retry (CLAUDE.md), potem fallback. Ziarno LOSOWE
            // (LLAMA_DEFAULT_SEED) — inaczej ten sam prompt daje w kółko tę samą
            // odpowiedź (bug D2: „model pisze tą samą wiadomość"). Nowy chain w
            // generate() losuje ziarno przy każdym wywołaniu, więc i retry różni się.
            for (int attempt = 0; attempt < 2; ++attempt) {
                std::string text;
                if (generate(job.system_prompt, job.user_prompt, LLAMA_DEFAULT_SEED, text)) {
                    result.text = text;
                    result.from_llm = true;
                    break;
                }
            }
            if (!result.from_llm) {
                std::cerr << "[brain] LLM nie dał poprawnej odpowiedzi — fallback dla " << job.faction
                          << "\n";
            }
            if (result.text.empty()) {
                continue; // brak i LLM, i szablonu — nic nie nadajemy
            }

            std::lock_guard<std::mutex> lock(mutex);
            results.push_back(std::move(result));
        }
    }

    // Qwen2.5 mówi ChatML-em; szablon składamy ręcznie (parse_special przy tokenizacji).
    bool generate(const std::string& system_prompt, const std::string& user_prompt,
                  std::uint32_t seed, std::string& out_text) {
        const auto t_start = std::chrono::steady_clock::now();
        // Anty-echo: małe modele (3B) lubią przepisywać zacytowaną wiadomość
        // gracza do odpowiedzi (bug: "@krw co o mnie myslisz? ..."). Dokładamy
        // twardą regułę do promptu systemowego. Gramatyka pilnuje JSON-a, ta
        // reguła — treści pola "tresc".
        const std::string prompt =
            "<|im_start|>system\n" + system_prompt +
            "\n\nZasady radia: odpowiadasz JEDNYM krótkim zdaniem. Nie powtarzaj ani "
            "nie cytuj słów gracza. Nie zaczynaj od znaku @ ani od nazwy nadawcy." +
            "<|im_end|>\n" +
            "<|im_start|>user\n" + user_prompt + "<|im_end|>\n" +
            "<|im_start|>assistant\n";

        std::vector<llama_token> tokens(prompt.size() + 16);
        const int n_tokens = llama_tokenize(vocab, prompt.c_str(), static_cast<int32_t>(prompt.size()),
                                            tokens.data(), static_cast<int32_t>(tokens.size()),
                                            /*add_special=*/true, /*parse_special=*/true);
        if (n_tokens < 0) {
            return false;
        }
        tokens.resize(static_cast<std::size_t>(n_tokens));

        // Bezpiecznik: prompt + miejsce na generację (do 160 tok) musi zmieścić się
        // w kontekście. Gdy nie — degradacja do fallbacku zamiast przepełnienia KV
        // (główna pętla nie może się wywrócić na zbyt długim prompcie).
        const int n_ctx = static_cast<int>(llama_n_ctx(ctx));
        if (n_tokens > n_ctx - 200) {
            std::cerr << "[brain] prompt LLM za długi (" << n_tokens << " tok, kontekst " << n_ctx
                      << ") — fallback\n";
            return false;
        }

        llama_memory_clear(llama_get_memory(ctx), true);

        llama_sampler* chain = llama_sampler_chain_init(llama_sampler_chain_default_params());
        llama_sampler_chain_add(chain, llama_sampler_init_grammar(vocab, kRadioGrammar, "root"));
        llama_sampler_chain_add(chain, llama_sampler_init_top_p(0.9f, 1));
        llama_sampler_chain_add(chain, llama_sampler_init_temp(0.7f));
        llama_sampler_chain_add(chain, llama_sampler_init_dist(seed));

        bool ok = true;
        std::string raw;
        // Prefill w porcjach po n_batch: pojedynczy llama_decode nie może dostać
        // więcej tokenów niż n_batch (inaczej GGML_ASSERT i abort). Pozycje w KV
        // liczą się dalej same (jak w pętli generacji), więc porcje kontynuują prompt.
        const int n_batch = static_cast<int>(llama_n_batch(ctx));
        for (int start = 0; ok && start < n_tokens; start += n_batch) {
            const int count = std::min(n_batch, n_tokens - start);
            if (llama_decode(ctx, llama_batch_get_one(tokens.data() + start, count)) != 0) {
                ok = false;
            }
        }
        const auto t_prefill = std::chrono::steady_clock::now();

        int generated_tokens = 0;
        for (int i = 0; ok && i < 160; ++i) {
            llama_token tok = llama_sampler_sample(chain, ctx, -1);
            if (llama_vocab_is_eog(vocab, tok)) {
                break;
            }
            char piece[256];
            const int len = llama_token_to_piece(vocab, tok, piece, sizeof(piece), 0, true);
            if (len > 0) {
                raw.append(piece, static_cast<std::size_t>(len));
            }
            ++generated_tokens;
            if (llama_decode(ctx, llama_batch_get_one(&tok, 1)) != 0) {
                ok = false;
            }
        }
        llama_sampler_free(chain);

        // Diagnostyka wydajności (tymczasowa, na potrzeby zbadania wolnych odpowiedzi
        // przy SE działającym równolegle) — usunąć albo wyciszyć po zebraniu liczb.
        const auto t_end = std::chrono::steady_clock::now();
        const double prefill_ms = std::chrono::duration<double, std::milli>(t_prefill - t_start).count();
        const double decode_ms = std::chrono::duration<double, std::milli>(t_end - t_prefill).count();
        const double tok_per_s = generated_tokens > 0 ? generated_tokens / (decode_ms / 1000.0) : 0.0;
        std::cerr << "[brain] LLM czas: prompt=" << n_tokens << " tok (prefill " << prefill_ms
                  << " ms), generacja=" << generated_tokens << " tok w " << decode_ms << " ms ("
                  << tok_per_s << " tok/s), razem=" << (prefill_ms + decode_ms) << " ms\n";

        if (!ok) {
            return false;
        }

        // Walidacja: musi być JSON {"tresc","ton"}, treść niepusta i w limicie.
        try {
            const nlohmann::json parsed = nlohmann::json::parse(raw);
            const std::string tresc = parsed.value("tresc", std::string{});
            if (tresc.empty() || tresc.size() > static_cast<std::size_t>(max_chars) * 2) {
                return false; // *2: max_chars liczy znaki, size() bajty (polskie znaki = 2 bajty)
            }
            out_text = tresc;
            return true;
        } catch (const nlohmann::json::exception&) {
            return false;
        }
    }
};

LlmWorker::LlmWorker(const Config& cfg) {
    auto impl = std::make_unique<Impl>();
    if (impl->init(cfg)) {
        impl_ = std::move(impl);
    }
}

LlmWorker::~LlmWorker() = default;

bool LlmWorker::enabled() const {
    return impl_ != nullptr;
}

void LlmWorker::submit(LlmJob job) {
    if (impl_ == nullptr) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(impl_->mutex);
        impl_->jobs.push_back(std::move(job));
    }
    impl_->cv.notify_one();
}

std::vector<LlmResult> LlmWorker::poll_results() {
    std::vector<LlmResult> out;
    if (impl_ != nullptr) {
        std::lock_guard<std::mutex> lock(impl_->mutex);
        out.swap(impl_->results);
    }
    return out;
}

#else // !ZF_WITH_LLM

struct LlmWorker::Impl {};

LlmWorker::LlmWorker(const Config&) {
    std::cout << "[brain] zbudowano bez llama.cpp — radio na szablonach fallback\n";
}
LlmWorker::~LlmWorker() = default;
bool LlmWorker::enabled() const { return false; }
void LlmWorker::submit(LlmJob) {}
std::vector<LlmResult> LlmWorker::poll_results() { return {}; }

#endif

} // namespace zf
