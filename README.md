# SE_ZyweFrakcje

Żywe frakcje NPC dla Space Engineers: pamięć, osobowości, radio z lokalnego LLM.
Mod C# (ModAPI) + mózg C++ (llama.cpp, SQLite). Szczegóły i etapy: CLAUDE.md.

Przygotowanie (ręcznie, raz na maszynę):
1. Space Engineers + mod MES (Workshop) w świecie testowym
2. `git submodule update --init brain/third_party/llama.cpp`
3. Model GGUF: qwen2.5-3b-instruct Q4_K_M -> `brain/models/`

Ścieżki storage moda NIE trzeba już wpisywać ręcznie: brain sam znajduje katalog
zapisu, w którym mod ostatnio pisał `events.jsonl` (`%APPDATA%/SpaceEngineers/Saves/…`).
Ręczne `[bridge].storage_dir` w `brain/configs/rules.local.toml` nadal działa i ma
pierwszeństwo, o ile taki katalog istnieje.

## Build i testy

```
cmake -S brain -B brain/build -DCMAKE_BUILD_TYPE=Release
cmake --build brain/build --parallel
ctest --test-dir brain/build --output-on-failure
```

Bez modelu i bez llama.cpp (sam mostek i silnik — tak chodzi CI):

```
cmake -S brain -B brain/build -DZF_WITH_LLM=OFF
```

Odtworzenie sesji z pliku zdarzeń, bez uruchamiania gry:

```
cd brain && ./build/zf_brain --replay ../jakis/events.jsonl
```
