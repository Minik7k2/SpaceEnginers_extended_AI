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

- [x] **E1. Frakcje istnieją:** po wczytaniu NOWEGO świata otwórz listę frakcji
  (G / terminal) → są „Korporacja Helion", „Krwawa Ręka", „Wolni Górnicy".
- [x] **E2. Bezpośredni spawn:** `/zf spawn KRW` → statek pojawia się ~200 m
  przed tobą, na czacie `[ZF] spawn (vanilla) reczny: ... dla KRW`. To
  niskopoziomowy test vanilla PrefabManagera — celowo omija MES (patrz sekcja F).
- [x] **E3. Głos po walce (odblokowany D3):** ostrzelaj statek z E2 → konsola
  `combat_hit faction=KRW`, a KRW odpowiada groźbą **głosem persony** (LLM), nie
  szablonem. Zniszcz go → `grid_destroyed faction=KRW` i reakcja.
- [x] **E4. Pamięć (odblokowany D5):** po walce z KRW napisz `@KRW co o mnie
  myślisz?` → odpowiedź nawiązuje do ostrzału/zniszczenia.
- [x] **E5. Potok spawn_request:** `/zf raid KRW` → na czacie `[ZF] spawn …`
  (mod → brain → spawn_request → mod). Konsola braina: `spawn_request KRW ...`.
  (Od integracji MES statek stawia MES — pełny test w sekcji F.)
- [x] **E6. Auto-spawn wojny (naprawione):** doprowadź KRW do wojny (zniszcz 2
  statki) → rajd na KRAWĘDZI wejścia (`spawn_request KRW kind=raid`), a potem
  PONAWIANY co tick, dopóki trwa wojna — bramkowany `spawn_cooldown_min` (więc
  ~co 5 min, nie co tick) i `spawn_wlaczone`. Wcześniej: przy trwającej już
  wojnie nie było nowych rajdów (raid tylko na krawędzi).
- [x] **E7. Cooldown/wyłącznik:** w `rules.toml` ustaw `[spawn] wlaczone = false`
  (hot-reload) → auto-spawny milkną, ale `/zf raid KRW` dalej działa (force).

## F. Spawny przez MES (Etap 5b → MES)

WYMAGANE: MES (Modular Encounters Systems, 1521905890) zasubskrybowany i aktywny
w świecie. Brain z NOWEGO builda (bramka obcych frakcji) — po przebudowie
zrestartuj `zf_brain.exe`. Rajdy testuj w OTWARTEJ PRZESTRZENI / kosmosie: przy
terenie MES odrzuca miejsce (safety check, patrz F8).

- [x] **F1. MES aktywny vs fallback:** `/zf raid KRW` → komunikat zaczyna się od
  `[ZF] spawn (MES) ...`. Jeśli `spawn (vanilla) ...` — MES nieaktywny (sprawdź
  subskrypcję/kolejność modów).
- [x] **F2. Spawn MES:** `/zf raid KRW` w otwartej przestrzeni → `[ZF] spawn (MES)
  raid: ZF_Raid dla KRW`, statek(i) faktycznie się pojawia. `/zf raid HEL` i
  `/zf raid WGR` analogicznie.
- [x] **F3. Właściciel = frakcja (factionOverride):** zespawnowany statek nazywa
  się `KRW.Shakedown Drone` i należy do KRW (wrogi/czerwony), nie SPRT. HEL/WGR
  neutralni.
- [x] **F4. Bramka obcych frakcji (nowy fix):** `/zf raid UNIV` (albo inny tag
  vanilla) → konsola braina `spawn pominięty: frakcja UNIV spoza moda`, ŻADEN
  statek się nie pojawia. Przebywanie przy stacji vanilla (UNIV/RTSL/CLEN) nie
  generuje auto-spawnów tych frakcji.
- [x] **F5. Głos LLM na statku MES (jak E3):** ostrzelaj statek z F2 → konsola
  `combat_hit faction=KRW`, KRW odpowiada groźbą głosem persony (`RADIO | KRW: …`);
  zniszcz → `grid_destroyed faction=KRW` i reakcja.
- [x] **F6. kind → grupa MES:** raid używa `ZF_Raid` (widać w komunikacie). patrol
  (`ZF_Patrol`) i convoy (`ZF_Convoy`) wywołasz tylko maszyną stanów
  (napięcie→patrol), nie `/zf raid`.
- [x] **F7. Regresja despawnu MES (odziedziczone z A5):** zespawnuj statek MES,
  odleć bardzo daleko aż MES go zdespawni → w konsoli braina NIE MOŻE pojawić się
  `grid_destroyed`.
- [x] **F8. Safety check (to nie błąd):** przy zboczu/terenie planety część spawnów
  daje `[ZF] MES odrzucił spawn ... (safety check?)` — MES nie ma bezpiecznego
  miejsca na statek. Ten sam spawn w otwartej przestrzeni schodzi.
- [x] **F9. Agresja RivalAI (kosmos):** w KOSMOSIE `/zf raid KRW` → statki od razu
  lecą na gracza i atakują, BEZ prowokacji (nie wiszą jak przedtem). Jeśli nadal
  wiszą do ostrzelania — RivalAI nie podpięło się do drona (prefab bez Remote
  Control do podmiany → trzeba innego statku). Uwaga: HEL/WGR są neutralni, więc
  Fighter może NIE atakować neutralnego gracza — pełny wrogi rajd HEL/WGR to
  osobny krok; testuj agresję na KRW (wrogie).
- [x] **F10. Bramka środowiska:** na PLANECIE (jest grawitacja) `/zf raid KRW` →
  `[ZF] frakcja KRW: rajd na razie tylko w kosmosie … (spawn pominięty)`, żaden
  statek nie spada. W kosmosie ten sam rajd spawnuje.
- [ ] **F11. De-eskalacja deterministyczna (`/zf okup`):** w kosmosie `/zf raid KRW`,
  potem `/zf okup KRW` → konsola braina `deeskalacja KRW: przyjęto (relacja +15…)`
  i `stand_down [KRW]`; na czacie `[ZF] stand_down KRW: N statk(i) wstrzymuje ogień
  i despawnuje` → statki KRW **przestają strzelać** i po ~10 s **znikają**. `/zf rel`
  pokazuje relację wyższą o 15. `/zf okup KRW` bez aktywnego rajdu → „nie prowadzi rajdu".
- [x] **F12. Anteny wyciszone:** po `/zf raid KRW` **nie ma** angielskiego gadania
  z anten („Engineer, fill up the collector…"). Nasze polskie radio (`[RADIO | KRW]`)
  działa normalnie.
- [ ] **F13. De-eskalacja przez LLM (`@KRW`):** UWAGA — wymaga dobrego modelu PL
  (qwen-3B odmawia i pisze bełkot; po podmianie na Bielika). W trakcie rajdu
  `@KRW biorę okup, oto 1000 sztabek` → jeśli model odpuści: `deeskalacja` + `stand_down`
  jak w F11. Rozszerzona gramatyka dokłada pole `odpuszcza`.

## G. Radio: kolor / kolejka / TTL (Etap 5a)

Radio przechodzi teraz przez `RadioDisplay`: kolor wg frakcji, kolejka priorytetowa,
odstęp między wyświetleniami i TTL 2 min. Limit 1/min/frakcję poza walką dalej
narzuca brain (to nie ta sekcja).

- [ ] **G1. Kolor frakcji:** `@KRW witam` → odpowiedź **czerwona**; `@HEL witam`
  → **niebieska**; `@WGR witam` → **żółta**. `/zf rel` oraz echo zwykłej wiadomości
  (`[RADIO | TEST]`) → **białe**. Nazwa nadawcy dalej `RADIO | TAG`.
- [ ] **G2. Bez zalania czatu:** wymuś kilka wiadomości na raz (np. `/zf tick`
  kilka razy pod rząd albo rajd, który generuje serię) → linie radia pojawiają się
  **jedna po drugiej** (~co 0.3 s), nie wszystkie w jednej klatce.
- [ ] **G3. Priorytet bojowy:** w trakcie rajdu KRW, gdy równocześnie czeka zwykła
  gadka i groźba bojowa (priority=1) → **bojowa wychodzi pierwsza**. Obserwacyjne;
  trudne do wymuszenia deterministycznie.
- [ ] **G4. TTL 2 min:** doprowadź do zaległego radia (brain pisze `radio_message`,
  gdy świat nie jest wczytany), wróć do świata po **>2 min** → stare radio **NIE**
  wyskakuje (bez TTL wyskakiwało jako „zaległe echo"). Świeże radio dalej działa.

## Znane zachowania (to nie błędy)

- Po pierwszym starcie braina mogą przyjść zaległe echa wiadomości z
  poprzedniej sesji (kolejka commands.jsonl) — ale tylko młodsze niż 2 min;
  starsze ucina TTL RadioDisplay (Etap 5a).
- SPRT (vanilla piraci) nie nadaje radia — nie ma szablonów; jego reakcje
  widać tylko w relacjach i konsoli. Gadają HEL/KRW/WGR.
- Losowe radio z ticku pojawia się średnio co ~3 tick (35% × tick 4 min),
  częściej gdy frakcja jest w napięciu/wojnie.
- Dryf relacji to 1 pkt / 2 h gry — niemierzalny w krótkim teście.
- Statki to na razie PLACEHOLDER `DS_Pirate_ShakedownDrone` (kosmiczny, jonowy):
  - na PLANECIE spada — brak silników atmosferycznych/wodorowych; rajdy testuj w kosmosie;
  - angielskie groźby („Engineer, fill up the collector…", „You have 5 minutes!") to
    wbudowana antena prefaba, NIE nasz mod — nasze radio jest po polsku (`RADIO | KRW: …`);
  - jest PASYWNY do sprowokowania: wisi i namierza wieżyczkami, ale po ostrzelaniu
    normalnie leci i atakuje. Aktywne rajdy od spawnu oraz statki atmo/naziemne
    dojdą z RivalAI (w toku: RivalAI na vanilla, najpierw kosmos).
- Bramka obcych frakcji: relacje z frakcjami vanilla (SPRT/UNIV/…) nadal są śledzone
  i widać je w `/zf rel` — blokujemy im tylko spawny, nie samo śledzenie relacji.

Wynik zgłoś jako: numer testu + PASS/FAIL + (przy FAIL) log z konsoli braina.
