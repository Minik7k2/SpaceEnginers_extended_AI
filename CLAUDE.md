# SE_ZyweFrakcje

Mod do Space Engineers (SE1): żywe frakcje NPC z osobowościami, pamięcią i radiem
napędzanym lokalnym LLM. Odpowiednik idei FS25_ZywiSasiedzi, ale w kosmosie.

## Architektura (DECYZJE — nie zmieniaj bez zgody użytkownika)

- **Środowisko:** single player, Windows 11, RTX 3070 8GB. Czysty mod ModAPI
  (Workshop-ready), BEZ pluginów. Plan B (nieużywany): Pulsar / pluginy dedyka.
- **Spawny/zachowania statków:** Modular Encounters Systems (MES) przez jego API
  mod-to-mod (plik MESApi.cs kopiowany do naszego moda, mod ID 1521905890).
  Kluczowe: CustomSpawnRequest, RegisterSuccessfulSpawnAction,
  RegisterDespawnWatcher, SendBehaviorCommand.
- **Mózg:** osobny proces **C++20** (`brain/`), llama.cpp linkowany jako biblioteka
  (submoduł, backend CUDA), SQLite przez C API, nlohmann/json, toml++ do configów.
  Configi i persony hot-reloadowane (mtime check co tick pętli).
- **Most:** pliki JSONL w storage moda (append-only).
  - `events.jsonl` — mod pisze, brain czyta.
  - `commands.jsonl` — brain pisze, mod czyta co ~60 tików.
  - Offsety przetworzonych linii: brain w SQLite, mod w swoim storage (sandbox XML).
  - Rotacja po 5 MB (`events-0002.jsonl`...), stare pliki kasuje odbiorca.
  - Każda linia ma pole `"v":1`. Niepełną/uszkodzoną linię pomiń i czekaj.
  - Po wczytaniu świata mod pisze `session_start`.
  - Pełna spec: `docs/protocol.md`.
- **LLM:** qwen2.5-3b-instruct Q4_K_M (GGUF). Wyjście wymuszone gramatyką GBNF:
  JSON `{"tresc": str, "ton": str}`. Max ~200 znaków treści, 1 retry, potem
  fallback na szablony z `brain/personas/fallback.toml`. Model w configu —
  później test A/B z Bielikiem. Uwaga: SE zajmuje 3-5 GB VRAM; jeśli ciasno,
  brain ma działać też na CPU (flaga w configu).
- **Frakcje:** Korporacja Helion (HEL, niebieski), Krwawa Ręka (KRW, czerwony,
  piraci — klną, patrz persona), Wolni Górnicy (WGR, żółty). Kult — Etap 7.
- **Świat mściwy:** wolny dryf relacji (1 pkt / 2h gry), ciężkie zdarzenia dają
  trwałe modyfikatory (np. sufit relacji), odkupienie przez czyny (okup,
  kontrakty), nie przez czas.
- **Kontrakty:** oficjalny terminal przez MyAPIGateway.ContractSystem.
  ID kontraktów utrwalane w SQLite (wymóg: odtworzenie po wczytaniu świata).
  Blok kontraktów stawiamy na WŁASNYCH stacjach frakcji (znany bug: kontrakty
  z API nie pokazują się na stacjach spawnowanych przez vanilla Economy).
  Vanilla reputacja — ignorowana, mamy własną.
- **Floty:** na start gotowe paczki statków z Workshopu podpięte pod nasze
  frakcje w spawn groups; własne flagowce w Etapie 7.

## Silnik relacji (brain)

- Skala -100..+100, frakcja↔gracz i frakcja↔frakcja.
- Progi: >=+40 sojusznik; -30 wrogi; -60 wojna; wyjście z wojny dopiero >-50
  (histereza). Wartości w `brain/configs/rules.toml`.
- Zmiany bazowe: ostrzał -5..-15 (wg dmg), zniszczenie statku -30, stacji -50
  (+trwały modyfikator), handel +1..+3, kontrakt +10..+20, atak na wroga
  frakcji: +5 u niej.
- Tick świata co 3-5 min: dryf → maszyna stanów per frakcja
  (spokój/napięcie/wojna, budżet akcji chroni przed spamem patroli) →
  zdarzenie losowe ważone stanem → decyzje do kolejki LLM/komend.
- Reakcje na zdarzenia z gry: natychmiastowe, poza tickiem.

## Struktura repo

```
mod/Data/Scripts/ZyweFrakcje/   # C# ModAPI (sesja, mostek, zdarzenia, radio, MES)
brain/src/                      # C++ (pętla, most, silnik, llm, sqlite)
brain/configs/rules.toml        # progi i reguły (hot-reload)
brain/personas/*.md             # karty osobowości frakcji (prompty)
brain/personas/fallback.toml    # szablony radiowe bez LLM
db/schema.sql                   # schemat SQLite
docs/protocol.md                # spec mostka JSONL
```

## Etapy implementacji

- **Etap 0** ✅ struktura repo, ten plik, spec protokołu, schema, persony.
  Pozostało ręcznie: instalacja MES z Workshopu, model GGUF, submoduł llama.cpp.
- **Etap 1 — most:** mod pisze `session_start`+`heartbeat`+`chat_message`;
  brain (bez LLM) czyta, loguje, odpisuje testowe `radio_message`; mod wyświetla.
  Kryterium: napisz coś na czacie → po <3 s wraca echo jako [RADIO | TEST].
- **Etap 2 — zdarzenia bojowe:** handler MyDamageInformation, resolver
  atrybucji (broń→siatka→BigOwners→gracz), agregacja combat_hit 3 s,
  grid_destroyed (MarkedForClose + świeże dmg; odróżnić od despawnu MES),
  proximity z histerezą 3/4 km.
- **Etap 3 — silnik:** SQLite wg schema.sql, reguły relacji, tick, maszyna
  stanów, tryb `--mock-llm` (same szablony fallback).
- **Etap 4 — głos:** integracja llama.cpp, GBNF, karty person, walidacja+retry.
- **Etap 5 — ręce:** radio na czacie (format `[RADIO | NAZWA]`, kolor frakcji,
  limit 1/min/frakcję poza walką, kolejka priorytetowa, TTL 2 min),
  spawn_request → MES API, adresowanie czatu (@frakcja / zasięg / szum).
- **Etap 6 — kontrakty i ceny:** ContractSystem + własne stacje frakcji z
  blokiem kontraktów, price_update, zdarzenia handlowe do silnika.
- **Etap 7 — polish:** Kult, LCD na stacjach, emisariusze (AiEnabled API),
  własne flagowce, A/B Bielik. Dalej: QLoRA fine-tune radia, RL zachowań.

## Testowanie

- Komendy czatu w modzie: `/zf rel` (relacje), `/zf event <json>` (wstrzyknij
  zdarzenie), `/zf tick` (wymuś tick), `/zf spawn <frakcja>` (test MES).
- Brain: `--mock-llm`, `--replay <plik.jsonl>` (odtworzenie zdarzeń bez gry).
- Mostek testowalny bez SE: dopisuj linie do events.jsonl ręcznie.

## Konwencje

- C#: ModAPI whitelist! Zero System.Net, zero plików poza
  MyAPIGateway.Utilities.*FileInStorage. Jeden MySessionComponentBase.
- C++: CMake, warnings-as-errors, brak wyjątków w pętli głównej mostka.
- Wszystkie stringi widoczne dla gracza — po polsku.
- Commituj po każdym etapie; wiadomości commitów po polsku.
