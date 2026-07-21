# Testy ręczne w grze — Etapy 1–4

Przygotowanie: uruchom świat z modami SE_ZyweFrakcje + MES, obok odpal
`brain\cmake-build-debug\zf_brain.exe` (konsola musi pokazać `[brain] start,
storage=...` ze ścieżką TEGO świata — jeśli nie, popraw
`brain/configs/rules.local.toml`). Po starcie brain wypisuje stan relacji.

Przy każdym teście patrz na DWA miejsca: czat w grze i konsolę braina.

## A. Most (regresja Etapów 1–2)

- [x] **A1. Echo:** napisz cokolwiek na czacie (bez `@` i `/`) → w <3 s wraca
  `[RADIO | TEST] Echo: ...`.
- [x] **A2. Proximity:** `/zf spawn`, podleć <3 km do statku → w konsoli
  `proximity ... enter`; odleć >4 km → `exit`. Krążenie w pasie 3–4 km nie
  generuje kolejnych zdarzeń (histereza).
- [x] **A3. Combat:** ostrzelaj statek NPC → w konsoli `combat_hit` z bronią
  i frakcją, paczki co ~3 s (nie pojedyncze strzały).
- [x] **A4. Zniszczenie:** rozwal statek do końca → `grid_destroyed` z nazwą siatki.
- [x] **A5. Kontrtest despawnu (zaległość Etapu 2):** spawnij statek, NIE
  strzelaj, odleć bardzo daleko i poczekaj aż MES go zdespawni → w konsoli
  braina NIE MOŻE pojawić się `grid_destroyed`.

## B. Silnik relacji (Etap 3)

- [x] **B1. Raport:** `/zf rel` → `[RADIO | SYSTEM] HEL +0 (spokoj) | KRW +0
  (spokoj) | WGR +0 (spokoj)` (SPRT dojdzie po pierwszym kontakcie).
- [x] **B2. Rozmowa adresowana:** `@KRW witam` → odpowiedź szablonem Krwawej
  Ręki (czerwona ramka danych: kolor to na razie pole w JSON, czat SE i tak
  pisze biało). `@HEL` i `@WGR` analogicznie. Literówka w tagu (`@KRWA`) →
  brak odpowiedzi, samo echo.
- [x] **B3. Cooldown radia:** dwa razy `@KRW ...` szybko po sobie → druga
  odpowiedź NIE przychodzi (limit 1/min); po minucie znów działa.
- [x] **B4. Relacje spadają:** ostrzelaj statek SPRT, potem `/zf rel` → SPRT
  na minusie. Konsola pokazuje każdą deltę (`relacja SPRT->gracz -6 ...`).
- [x] **B5. Stany:** doprowadź SPRT poniżej -30 → konsola `stan SPRT: spokoj
  -> napiecie`; poniżej -60 (zniszcz statek) → `-> wojna`. `/zf rel`
  pokazuje stan.
- [x] **B6. Wymuszony tick:** `/zf tick` → `[RADIO | SYSTEM] Tick wymuszony.`
  (czasem dodatkowo losowe radio którejś frakcji — to celowe, szansa 35%).
- [x] **B7. Trwałość:** zamknij brain (Ctrl+C), odpal ponownie → start pokazuje
  te same relacje (stan w brain/state/zf_state.sqlite3, przeżywa restart).
  Stare zdarzenia nie są przetwarzane drugi raz (brak podwójnych delt).
- [x] **B8. Restart świata:** wyjdź do menu i wczytaj świat ponownie → echo
  dalej działa, relacje bez zmian.

## C. Hot-reload (bez restartu braina)

- [x] **C1. Reguły:** w trakcie działania braina zmień w
  `brain/configs/rules.toml` np. `ostrzal_max` na `-50`, zapisz → konsola
  `config przeładowany`; ostrzał teraz zdejmuje dużo więcej (widać w `/zf rel`).
  Cofnij zmianę po teście.
- [x] **C2. Szablony:** zmień tekst w `brain/personas/fallback.toml`, zapisz →
  konsola `szablony fallback: 3 frakcji`; `@KRW ...` odpowiada nowym tekstem.
  Cofnij zmianę po teście.

## D. Głos LLM (Etap 4)

- [x] **D1. Start modelu:** konsola braina przy starcie pokazuje `LLM gotowy:
  models/qwen2.5-3b-instruct-q4_k_m.gguf (CPU)`. Jeśli zamiast tego jest
  `model LLM nieobecny` — brak pliku modelu w brain/models/.
- [x] **D2. Rozmowa z personą:** `@KRW oddaj wrak` → odpowiedź pisana przez
  model w stylu pirata (za każdym razem inna). Na CPU pierwsza odpowiedź może
  zająć do ~30 s, kolejne szybciej. `@HEL` — korpomowa, `@WGR` — górniczy luz.
- [x] **D3. Reakcje bojowe głosem LLM:** ostrzelaj statek HEL/KRW/WGR (spawn
  własnej siatki z ich frakcją albo poczekaj na Etap 5 spawny) — groźba
  z kontekstem sytuacji, nie sztywny szablon. Konsola: `radio (LLM) [...]`.
- [x] **D4. Fallback:** zatrzymaj brain, w `rules.local.toml` ustaw
  `[llm] model_path = "brak.gguf"`, odpal → `@KRW test` odpowiada sztywnym
  szablonem (konsola: `radio [KRW]` bez dopisku LLM). Cofnij zmianę.
- [x] **D5. Pamięć w prompcie:** po walce z KRW napisz `@KRW co o mnie
  myślisz?` → odpowiedź powinna nawiązywać do ostrzału/zniszczeń (model
  dostaje ostatnie 5 wpisów pamięci frakcji). Uwaga: pełny test dopiero po
  Etapie 5 — `/zf spawn` tworzy SPRT, nie KRW/HEL/WGR; do weryfikacji ze spawnem
  prawdziwej frakcji.

## E. Spawny frakcji (Etap 5b)

WYMAGANE: nowy świat testowy (frakcje z `Factions.sbc` powstają przy generowaniu
świata — na starym zapisie mogą nie istnieć). Sprawdź w grze menu frakcji: mają
być HEL/KRW/WGR.

- [ ] **E1. Frakcje istnieją:** po wczytaniu NOWEGO świata otwórz listę frakcji
  (G / terminal) → są „Korporacja Helion", „Krwawa Ręka", „Wolni Górnicy".
- [ ] **E2. Bezpośredni spawn:** `/zf spawn KRW` → statek pojawia się ~200 m
  przed tobą, na czacie `[ZF] spawn reczny: ... dla KRW`. (Dawniej padało na SPRT.)
- [ ] **E3. Głos po walce (odblokowany D3):** ostrzelaj statek z E2 → konsola
  `combat_hit faction=KRW`, a KRW odpowiada groźbą **głosem persony** (LLM), nie
  szablonem. Zniszcz go → `grid_destroyed faction=KRW` i reakcja.
- [ ] **E4. Pamięć (odblokowany D5):** po walce z KRW napisz `@KRW co o mnie
  myślisz?` → odpowiedź nawiązuje do ostrzału/zniszczenia.
- [ ] **E5. Potok spawn_request:** `/zf raid KRW` → na czacie `[ZF] spawn raid/…`
  (mod → brain → spawn_request → mod). Konsola braina: `spawn_request KRW ...`.
- [ ] **E6. Auto-spawn z maszyny stanów:** doprowadź KRW do wojny (zniszcz 2
  statki) → konsola `spawn_request KRW kind=raid`, w grze pojawia się statek.
- [ ] **E7. Cooldown/wyłącznik:** w `rules.toml` ustaw `[spawn] wlaczone = false`
  (hot-reload) → auto-spawny milkną, ale `/zf raid KRW` dalej działa (force).

## Znane zachowania (to nie błędy)

- Po pierwszym starcie braina mogą przyjść zaległe echa wiadomości z
  poprzedniej sesji (kolejka commands.jsonl).
- SPRT (vanilla piraci) nie nadaje radia — nie ma szablonów; jego reakcje
  widać tylko w relacjach i konsoli. Gadają HEL/KRW/WGR.
- Losowe radio z ticku pojawia się średnio co ~3 tick (35% × tick 4 min),
  częściej gdy frakcja jest w napięciu/wojnie.
- Dryf relacji to 1 pkt / 2 h gry — niemierzalny w krótkim teście.
- Spawn (Etap 5b) to STUB: vanilla prefab `DS_Pirate_ShakedownDrone` dla każdej
  frakcji, bez AI/patroli/despawnu MES i bez rozróżnienia patrol/raid/convoy
  (kind widać tylko w komunikacie). Prawdziwe floty z MES podmienimy później.

Wynik zgłoś jako: numer testu + PASS/FAIL + (przy FAIL) log z konsoli braina.
