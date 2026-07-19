# SE_ZyweFrakcje

Żywe frakcje NPC dla Space Engineers: pamięć, osobowości, radio z lokalnego LLM.
Mod C# (ModAPI) + mózg C++ (llama.cpp, SQLite). Szczegóły i etapy: CLAUDE.md.

Przed Etapem 1 (ręcznie):
1. Space Engineers + mod MES (Workshop) w świecie testowym
2. git submodule add https://github.com/ggml-org/llama.cpp brain/third_party/llama.cpp
3. Model GGUF: qwen2.5-3b-instruct Q4_K_M -> brain/models/
4. Ścieżka storage moda -> brain/configs/rules.toml [bridge]
