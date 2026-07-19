# Testy ręczne w grze — Etapy 1–3

Przygotowanie: uruchom świat z modami SE_ZyweFrakcje + MES, obok odpal
`brain\cmake-build-debug\zf_brain.exe` (konsola musi pokazać `[brain] start,
storage=...` ze ścieżką TEGO świata — jeśli nie, popraw
`brain/configs/rules.local.toml`). Po starcie brain wypisuje stan relacji.

Przy każdym teście patrz na DWA miejsca: czat w grze i konsolę braina.

## A. Most (regresja Etapów 1–2)

- [ ] **A1. Echo:** napisz cokolwiek na czacie (bez `@` i `/`) → w <3 s wraca
  `[RADIO | TEST] Echo: ...`.
- [ ] **A2. Proximity:** `/zf spawn`, podleć <3 km do statku → w konsoli
  `proximity ... enter`; odleć >4 km → `exit`. Krążenie w pasie 3–4 km nie
  generuje kolejnych zdarzeń (histereza).
- [ ] **A3. Combat:** ostrzelaj statek NPC → w konsoli `combat_hit` z bronią
  i frakcją, paczki co ~3 s (nie pojedyncze strzały).
- [ ] **A4. Zniszczenie:** rozwal statek do końca → `grid_destroyed` z nazwą siatki.
- [ ] **A5. Kontrtest despawnu (zaległość Etapu 2):** spawnij statek, NIE
  strzelaj, odleć bardzo daleko i poczekaj aż MES go zdespawni → w konsoli
  braina NIE MOŻE pojawić się `grid_destroyed`.

## B. Silnik relacji (Etap 3)

- [ ] **B1. Raport:** `/zf rel` → `[RADIO | SYSTEM] HEL +0 (spokoj) | KRW +0
  (spokoj) | WGR +0 (spokoj)` (SPRT dojdzie po pierwszym kontakcie).
- [ ] **B2. Rozmowa adresowana:** `@KRW witam` → odpowiedź szablonem Krwawej
  Ręki (czerwona ramka danych: kolor to na razie pole w JSON, czat SE i tak
  pisze biało). `@HEL` i `@WGR` analogicznie. Literówka w tagu (`@KRWA`) →
  brak odpowiedzi, samo echo.
- [ ] **B3. Cooldown radia:** dwa razy `@KRW ...` szybko po sobie → druga
  odpowiedź NIE przychodzi (limit 1/min); po minucie znów działa.
- [ ] **B4. Relacje spadają:** ostrzelaj statek SPRT, potem `/zf rel` → SPRT
  na minusie. Konsola pokazuje każdą deltę (`relacja SPRT->gracz -6 ...`).
- [ ] **B5. Stany:** doprowadź SPRT poniżej -30 → konsola `stan SPRT: spokoj
  -> napiecie`; poniżej -60 (zniszcz statek) → `-> wojna`. `/zf rel`
  pokazuje stan.
- [ ] **B6. Wymuszony tick:** `/zf tick` → `[RADIO | SYSTEM] Tick wymuszony.`
  (czasem dodatkowo losowe radio którejś frakcji — to celowe, szansa 35%).
- [ ] **B7. Trwałość:** zamknij brain (Ctrl+C), odpal ponownie → start pokazuje
  te same relacje (stan w brain/state/zf_state.sqlite3, przeżywa restart).
  Stare zdarzenia nie są przetwarzane drugi raz (brak podwójnych delt).
- [ ] **B8. Restart świata:** wyjdź do menu i wczytaj świat ponownie → echo
  dalej działa, relacje bez zmian.

## C. Hot-reload (bez restartu braina)

- [ ] **C1. Reguły:** w trakcie działania braina zmień w
  `brain/configs/rules.toml` np. `ostrzal_max` na `-50`, zapisz → konsola
  `config przeładowany`; ostrzał teraz zdejmuje dużo więcej (widać w `/zf rel`).
  Cofnij zmianę po teście.
- [ ] **C2. Szablony:** zmień tekst w `brain/personas/fallback.toml`, zapisz →
  konsola `szablony fallback: 3 frakcji`; `@KRW ...` odpowiada nowym tekstem.
  Cofnij zmianę po teście.

## Znane zachowania (to nie błędy)

- Po pierwszym starcie braina mogą przyjść zaległe echa wiadomości z
  poprzedniej sesji (kolejka commands.jsonl).
- SPRT (vanilla piraci) nie nadaje radia — nie ma szablonów; jego reakcje
  widać tylko w relacjach i konsoli. Gadają HEL/KRW/WGR.
- Losowe radio z ticku pojawia się średnio co ~3 tick (35% × tick 4 min),
  częściej gdy frakcja jest w napięciu/wojnie.
- Dryf relacji to 1 pkt / 2 h gry — niemierzalny w krótkim teście.

Wynik zgłoś jako: numer testu + PASS/FAIL + (przy FAIL) log z konsoli braina.
