#include "llm.hpp"

#include <atomic>
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
        cparams.n_batch = 512;
        // AUTO probuje Flash Attention realnym obliczeniem przy starcie (świeży,
        // niepewny kod w tej wersji llama.cpp) — wyłączamy jawnie zamiast zgadywać.
        cparams.flash_attn_type = LLAMA_FLASH_ATTN_TYPE_DISABLED;
        const unsigned hw = std::thread::hardware_concurrency();
        const int threads = static_cast<int>(hw > 2 ? hw / 2 : 2);
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
            {
                std::unique_lock<std::mutex> lock(mutex);
                cv.wait(lock, [this] { return stop.load() || !jobs.empty(); });
                if (stop.load()) {
                    return;
                }
                job = std::move(jobs.front());
                jobs.pop_front();
            }

            LlmResult result{job.faction, job.fallback_text, job.color, job.priority, false};
            // 1 retry (CLAUDE.md), potem fallback.
            for (int attempt = 0; attempt < 2; ++attempt) {
                std::string text;
                if (generate(job.system_prompt, job.user_prompt, 0xC0FFEE + attempt, text)) {
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
        const std::string prompt = "<|im_start|>system\n" + system_prompt + "<|im_end|>\n" +
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

        llama_memory_clear(llama_get_memory(ctx), true);

        llama_sampler* chain = llama_sampler_chain_init(llama_sampler_chain_default_params());
        llama_sampler_chain_add(chain, llama_sampler_init_grammar(vocab, kRadioGrammar, "root"));
        llama_sampler_chain_add(chain, llama_sampler_init_top_p(0.9f, 1));
        llama_sampler_chain_add(chain, llama_sampler_init_temp(0.7f));
        llama_sampler_chain_add(chain, llama_sampler_init_dist(seed));

        bool ok = true;
        std::string raw;
        if (llama_decode(ctx, llama_batch_get_one(tokens.data(), static_cast<int32_t>(tokens.size()))) != 0) {
            ok = false;
        }

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
            if (llama_decode(ctx, llama_batch_get_one(&tok, 1)) != 0) {
                ok = false;
            }
        }
        llama_sampler_free(chain);
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
