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
  Ścieżki per maszyna (np. `[bridge].storage_dir`) w `brain/configs/rules.local.toml`
  (poza gitem) — nadpisuje wartości z `rules.toml`; każdy komputer ma własny.
  Gdy `storage_dir` jest puste albo wskazuje nieistniejący katalog, brain sam
  znajduje storage: szuka najświeższego `events.jsonl` w zapisach SE
  (`%APPDATA%/SpaceEngineers/Saves`, korzeń nadpisywalny przez `ZF_SAVES_DIR`).
  Baza stanu jest PER ŚWIAT: z `[bridge].db_path` powstaje
  `state/zf_state_<świat>_<hash storage>.sqlite3` (wyłącznik: `db_per_swiat = false`).
  Wspólna baza sprawiała, że nowy zapis dziedziczył relacje po poprzednim, a hybryda
  reputacji wpisywała je od razu do okna frakcji. Kluczem jest ścieżka storage, nie
  nazwa świata z `session_start` — bazę trzeba otworzyć przed pierwszym zdarzeniem.
  Starej wspólnej bazy NIE migrujemy automatycznie (to właśnie było źródłem błędu).
- **Most:** pliki JSONL w storage moda (append-only).
  - `events.jsonl` — mod pisze, brain czyta.
  - `commands.jsonl` — brain pisze, mod czyta co ~60 tików.
  - Offsety przetworzonych linii: brain w SQLite, mod w swoim storage (sandbox XML).
  - Rotacja po 5 MB (`events-0002.jsonl`...), stare pliki kasuje odbiorca.
  - Każda linia ma pole `"v":1`. Niepełną/uszkodzoną linię pomiń i czekaj.
  - Po wczytaniu świata mod pisze `session_start`.
  - **PUŁAPKA — nazwa świata musi być identyczna z nazwą katalogu w `Saves`**
    (ustalone 2026-08-01 dekompilacją): gra składa storage moda z NAZWY świata
    (`MySession.WorldSavePath = SavesPath + SessionName.Replace(':','-')`, potem
    `WriteFileInWorldStorage` dokłada `\Storage\<mod>`), a nie ze ścieżki zapisu
    (`CurrentPath`) — i przy zmianie nazwy NIE przemianowuje katalogu (w `MySession`
    nie ma `Directory.Move`). Rozjazd daje dwa objawy: nazwa z końcową spacją (katalog
    takiej mieć nie może, bo Win32 ją obcina) → `DirectoryNotFoundException` i BŁĄD
    ładowania świata; zwykłe przemianowanie → cicha strata, bo mod pisze do świeżego
    katalogu OBOK zapisu i kontrakty/ceny/offsety wyglądają na skasowane. Dotyka
    każdego moda — MES w tej samej sesji nie zapisał żadnego swojego configu.
    Mod ostrzega o rozjeździe na czacie (`CheckWorldNameMatchesFolder`), a błąd
    zapisu wyłącza sam mostek (`EventWriter.Failed`), nie świat: wyjątek z `LoadData()`
    zabija ładowanie świata, więc nic stamtąd nie może lecieć wyżej.
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
  Vanilla reputacja — HYBRYDA (zmiana decyzji, 2026-07-29): źródłem prawdy jest nasz
  silnik relacji, ale jego wynik jest rzutowany na natywną reputację SE, żeby gracz
  widział stan w oknie frakcji, a wieżyczki/ceny/strefy reagowały. Szczegóły niżej.
- **Floty:** na start gotowe paczki statków z Workshopu podpięte pod nasze
  frakcje w spawn groups; własne flagowce w Etapie 7.

## Silnik relacji (brain)

- Skala -100..+100, frakcja↔gracz i frakcja↔frakcja. Polityka między frakcjami
  jest zasiana raz na świat (HEL/KRW -70, KRW/WGR -50, HEL/WGR +10) — bez tego
  „atak na wroga frakcji" nie miał jak zadziałać. Podgląd: `/zf rel` po `||`.
- Progi: >=+40 sojusznik; -30 wrogi; -60 wojna; wyjście z wojny dopiero >-50
  (histereza). Wartości w `brain/configs/rules.toml`.
- Zmiany bazowe: ostrzał -5..-15 (wg dmg), zniszczenie statku -30, stacji -50
  (+trwały sufit relacji = 20, świat mściwy), handel +1..+3, kontrakt +20 /
  -10 za zawalony, atak na wroga frakcji: +5 u niej, de-eskalacja +15.
- Tick świata co 3-5 min: dryf → maszyna stanów per frakcja
  (spokój/napięcie/wojna, budżet akcji chroni przed spamem patroli) →
  oferta kontraktu (jeśli relacja i cooldown pozwalają) → zdarzenie losowe
  ważone stanem → decyzje do kolejki LLM/komend.
- Dryf dotyczy WYŁĄCZNIE relacji do gracza. Wojny między frakcjami nie kończy
  upływ czasu, tylko zdarzenia.
- Stan długiego oddechu (aktywny rajd z TTL 60 min, cooldowny spawnu i
  kontraktów) siedzi w SQLite — restart brainu w trakcie rajdu nie gubi okupu.
- **Reputacja natywna (hybryda):** po każdej zmianie relacji brain wysyła
  `reputation_sync`, a mod zapisuje wartość przez `MyAPIGateway.Session.Factions`
  (`SetReputationBetweenPlayerAndFaction`, dla polityki `SetReputation`). Odwzorowanie
  odcinkowo-liniowe -100..+100 → -1500..+1500 z węzłami w progach gry (±500), więc
  etykieta „wróg/neutralny/sojusznik" w oknie frakcji zgadza się z `/zf rel`. Kierunek
  jednostronny: mod co ~5 s przywraca wartość z brainu, gdy gra ruszy ją sama (nagrody
  kontraktów vanilla) — bez tego to samo zdarzenie liczyłoby się dwa razy. Synchronizujemy
  tylko HEL/KRW/WGR; reputacja frakcji vanilla/MES zostaje grze. Config: `[reputacja]`.
- Reakcje na zdarzenia z gry: natychmiastowe, poza tickiem.

## Struktura repo

```
mod/Data/Scripts/ZyweFrakcje/   # C# ModAPI (sesja, mostek, zdarzenia, radio, MES)
mod/Data/ContractTypes.sbc      # definicja własnego typu zlecenia (MyContractCustom)
mod/Data/Prefabs/               # rekwizyty: skrzynka okupu, zgubka i wrak do zleceń
brain/src/                      # C++ (pętla, most, silnik, llm, sqlite)
brain/configs/rules.toml        # progi i reguły (hot-reload)
brain/personas/*.md             # karty osobowości frakcji (prompty)
brain/personas/fallback.toml    # szablony radiowe bez LLM
db/schema.sql                   # schemat SQLite
docs/protocol.md                # spec mostka JSONL
```

## Etapy implementacji

- **Etap 0** ✅ struktura repo, ten plik, spec protokołu, schema, persony.
  Submoduł llama.cpp ✅ (shallow, b10068) i model GGUF ✅ (brain/models/) —
  dodane 2026-07-20. Pozostało ręcznie: MES z Workshopu na każdej maszynie.
  Uwaga toolchain: CUDA wymaga MSVC — z MinGW (CLion) llama.cpp buduje się
  CPU-only; na laptopie brain gada z CPU, backend GPU ewentualnie na desktopie.
- **Etap 1 — most:** ✅ mod pisze `session_start`+`heartbeat`+`chat_message`;
  brain (bez LLM) czyta, loguje, odpisuje testowe `radio_message`; mod wyświetla.
  Kryterium: napisz coś na czacie → po <3 s wraca echo jako [RADIO | TEST].
  Zweryfikowane bez SE: `brain/tests` (ctest) + testy mostka moda uruchamiane
  przez mono/mcs + test międzyjęzykowy (prawdziwy `zf_brain` czytający plik
  napisany przez prawdziwy `EventWriter` moda). Zweryfikowane W GRZE 2026-07-19
  (echo ~1 s) po dwóch naprawach: DateTimeOffset poza whitelistą ModAPI
  (→ DateTime) oraz crash gry przez trzymany uchwyt zapisu commands.jsonl
  (ReadFileInWorldStorage nie otwiera pliku z cudzym uchwytem zapisu — brain
  otwiera plik tylko na czas dopisania linii, mod czyta w try/catch).
- **Etap 2 — zdarzenia bojowe:** ✅ handler MyDamageInformation, resolver
  atrybucji (broń→siatka→BigOwners→gracz), agregacja combat_hit 3 s,
  grid_destroyed (MarkedForClose + świeże dmg; odróżnić od despawnu MES),
  proximity z histerezą 3/4 km. Zweryfikowane W GRZE 2026-07-19: proximity
  enter/exit, combat_hit (broń ręczna i Explosion), grid_destroyed.
  Kontrtest despawnu (zaległość od Etapu 2) zautomatyzowany 2026-08-08 jako
  `/zf autotest despawn`: siatka, której gracz NIE ostrzelał, po `Close()` NIE MOŻE
  dać `grid_destroyed` — plus kontrola dodatnia (świeże trafienie nadal się liczy),
  bez której reguła „zawsze nie" przeszłaby na zielono, i granica okna 30 s
  (postrzelałeś, odleciałeś, MES posprzątał minutę później). Decyzję podejmuje jeden
  predykat `CombatTracker.CzyDespawnZglosiZniszczenie` używany i przez `OnEntityRemove`,
  i przez test — gdyby test miał własną kopię warunku, potwierdzałby swoje założenia.
  Granica: to kontrtest REGUŁY MODA, nie integracji z MES (samego MES w teście nie ma).
  Po drodze naprawiony mostek braina: offsety linii
  kluczowane pełną ścieżką (offset starego świata przesłaniał krótszy
  events.jsonl nowego → „brak odczytów") + config per maszyna
  (rules.local.toml).
- **Etap 3 — silnik:** ✅ (kod + testy + --replay na prawdziwej sesji; do
  weryfikacji w grze wg docs/testy-reczne.md) SQLite wg schema.sql, reguły
  relacji, tick, maszyna stanów, tryb `--mock-llm` (same szablony fallback).
- **Etap 4 — głos:** ✅ (kod + smoke test na sucho: @KRW → odpowiedź modelu
  w commands.jsonl; do weryfikacji w grze wg docs/testy-reczne.md sekcja D)
  integracja llama.cpp, GBNF, karty person, walidacja+retry. Wątek LlmWorker,
  prompt = persona + pamięć frakcji + kontekst z silnika, fallback na szablony.
- **Etap 5 — ręce:** ✅ radio na czacie (format `[RADIO | NAZWA]`, kolejka
  priorytetowa, TTL 2 min; kolor frakcji jedzie w JSON, ale czat SE rysuje biało —
  `MyVisualScriptLogicProvider` poza whitelistą), spawn_request → MES API,
  adresowanie czatu (@frakcja / zasięg / szum), de-eskalacja z realnym okupem.
  **Floty per frakcja (2026-08-01):** 9 grup `ZF_<Rodzaj>_<TAG>` + 3 zapasowe dla obcych
  tagów; `TestSpawner.GroupForKind(tag, kind)` dokleja tag tylko dla HEL/KRW/WGR. HEL leci
  wojskiem (`C33_Military_Enforcer`, `C22_Trade_Merchant`), KRW pirackim (`C40_Pirate_Vulture`,
  `C42_Pirate_SalvageCarrier`), WGR górniczym (`C12_Mining_Armed_Tender`, `C10_Mining_Carriage`).
  MES jest tylko silnikiem spawnu — statków ani stacji NIE zawiera (jego prefaby to atrapy
  i wzorce symetrii), za to daje bloki NPC, 11 profili autopilota, 33 profile łupów.
  **Pilot dla dużych kadłubów:** RivalAI potrafi prowadzić grid tylko z blokiem zdalnego
  sterowania, a `[RivalAiReplaceRemoteControl]` wg wiki MES tylko PODMIENIA istniejący —
  duże frachtowce i okręty vanilli nie mają go wcale (dlatego konwoje wcześniej dryfowały
  po prostej, a rajdy szły małymi dronami). Zamiast kopiować prefaby Keena do moda,
  `mod/Data/ZF_Manipulations.sbc` zamienia kostkę pancerza w `RivalAIRemoteControlLarge`
  (`[ReplaceArmorBlocksWithModules]` + `[ModulesForArmorReplacement]`), a grupa podpina to
  tagiem `[ManipulationGroups:ZF_ManipulacjaGrupa_Pilot]`. KLUCZOWE: grupa z tą manipulacją
  musi być JEDNOROZMIAROWA (same duże siatki) — mała dostałaby blok nie na swój rozmiar.
  Konwoje jadą zachowaniem `ZF_Konwoj` (`BehaviorName:CargoShip` + gotowy autopilot MES),
  czyli lecą trasą, zamiast dryfować.
- **Etap 6 — kontrakty i ceny:** kod gotowy, DO WERYFIKACJI W GRZE (sekcja I
  w docs/testy-reczne.md). Frakcja wystawia zlecenie w ticku (`[kontrakty]`
  w rules.toml) → mod tworzy je przez `MyAPIGateway.ContractSystem` na bloku
  kontraktów/sklepu frakcji → `contract_created` z prawdziwym ID ląduje w SQLite →
  wykonanie/porażka wraca jako `contract_done`.
  **Sześć typów zleceń** (2026-07-30, siódmy usunięty 2026-08-09), po jednej klasie
  z `Sandbox.ModAPI.Contracts`: `dostawa` (Acquisition), `nagroda` (Bounty), `transport`
  (Hauling), `naprawa` (Repair), `poszukiwania` (Search), `wlasne` (Custom +
  `mod/Data/ContractTypes.sbc`). Typ losuje brain wagami z `[kontrakty.typy]`
  (nadpisania per frakcja: piraci wolą nagrody za głowę, górnicy naprawy), a
  `[kontrakty.mnoznik]` skaluje nagrodę I zmianę relacji wg trudności typu.
  KLUCZOWE: mod ma ostatnie słowo — gdy nie znajdzie w świecie celu (wrogiej
  tożsamości, drugiego bloku, uszkodzonej siatki…) albo gra odrzuci kontrakt,
  wystawia DOSTAWĘ i to ona wraca w `contract_created` (brain utrwala typ, który
  naprawdę powstał). **`wlasne` WYŁĄCZONE (waga 0, 2026-08-01):** ten typ NIE MA warunku
  wykonania. `IMyContractCustom` niesie tylko `DefinitionId`/`EndBlockId`/`Name`/`Description`,
  a podpięty `MyContractConditionCustom` kończy WYŁĄCZNIE mod przez
  `IMyContractSystem.TryFinishCustomContract(id)` — czego nie robimy, więc gracz dostawał
  zlecenie bez zadania, wygasające na karę relacji i przepadek kaucji. Do włączenia dopiero
  razem z własnym warunkiem po stronie moda. Tamże: `EndBlockId` custom kontraktu musi być
  BLOKIEM KONTRAKTÓW (`as MyContractBlock` → `Fail_BlockNotFound`), a `FindHaulTarget`
  schodzi na sklep — osobna przyczyna odrzuceń.
  **PUŁAPKA — nadpisanie frakcji WYGRYWA z wartością domyślną (2026-08-08).** Powyższe
  „waga 0" było prawdą tylko dla `[kontrakty.typy]`; `[kontrakty.typy.KRW]` w TYM SAMYM
  PLIKU miało `wlasne = 2`, więc piraci przez tydzień losowali typ opisany w całym repo
  jako wyłączony (~27% zleceń KRW) i gracz dostawał zadanie bez warunku wykonania.
  Scenariusze tego nie łapały, bo sprawdzają typ WYMUSZONY (`/zf kontrakt KRW wlasne`) —
  ścieżkę, która celowo omija wagi. Od teraz pilnują tego dwie bramki: `dlug_test`
  (waga 0 u KAŻDEJ frakcji i w KAŻDYM stanie + 1200 losowań kontrolnych) oraz
  `tools/waliduj_sbc.py` (wagę wolno podnieść dopiero, gdy `Contracts.cs` zawiera
  `TryFinishCustomContract`). Ta druga jest jedynym miejscem widzącym JEDNOCZEŚNIE config
  brainu i kod moda — brain nie czyta C#, mod nie czyta `rules.toml`.
  **Rekwizyty (2026-07-30):** frakcja sama przygotowuje robotę — `poszukiwania`
  i `naprawa` stawiają prefab z `mod/Data/Prefabs/ZF_ContractProps.sbc` (zgubiony
  moduł / uszkodzony wrak, właściciel = frakcja) i dopiero w callbacku spawnu tworzą
  kontrakt. Cel poszukiwań to WYŁĄCZNIE nasz moduł: vanillowe zlecenie każe przywieźć
  znaleziony grid pod stację, a stacji nikt nie przywiezie.
  **Przyjęcie zlecenia (`contract_taken`):** gracz bierze robotę → konwój do eskorty
  dopiero teraz wyrusza, cel nagrody dostaje ochronę, a każdy WRÓG wystawcy traci do
  gracza `kontrakt_przyjety_u_wroga` (-3). Kara raz na kontrakt (status `taken`
  w SQLite, liczy się do `max_otwartych` jak `open`).
  **`eskorta` — TYP USUNIĘTY W CAŁOŚCI (2026-08-09).** Gra nie ma definicji
  `ContractTypeEscort`: `Content/Data` wozi osiem typów (Deliver, Find, GridHauling, Hunt,
  ObtainAndDeliver, PvEBounty, Repair, Salvage). `CreateCustomEscortContract` wychodzi na
  pierwszym warunku (`GetDefinition() is MyContractTypeEscortDefinition`) i zwraca `Error`
  BEZ WPISU DO LOGU — stąd „gra odrzuciła kontrakt" bez śladu, którego szukaliśmy trzy
  przebiegi. Do 2026-08-09 typ stał z wagą 0, a implementacja czekała „na wypadek
  przywrócenia przez Keena" — kosztowało to gałąź w silniku, dwa case'y w `Contracts.cs`,
  wpis w `contract_kinds()`, dwa klucze w configu i trzy warstwy testów, których jedynym
  zadaniem było pilnowanie, żeby martwy kod pozostał martwy. Wycięty end-to-end; kod
  i pełne ustalenie z dekompilacji zostają w historii gita, bo to jest archiwum.
  Usunięcie jest GŁOŚNE: `eskorta` w `[kontrakty.typy]` wywala config brainu
  (`require_contract_kind`), więc stary `rules.toml` mówi wprost, którą linię skasować.
  Wart zapamiętania jest sam pomysł, który przy okazji zniknął: konwój wyrusza dopiero po
  `contract_taken`, nie przy wystawieniu zlecenia — do wskrzeszenia na innym typie.
  `nagroda` ma wagę 0 z innego powodu (vanilla liczy zabicia GRACZY, nie NPC).
  Handel wykrywany heurystycznie (zmiana salda + sklep frakcji <300 m), bo ModAPI
  nie ma zdarzenia transakcji. UWAGA: kaucję ściąganą przy PRZYJĘCIU zlecenia ta sama
  heurystyka brała za zakup (darmowe +1..+3 relacji), stąd `_trade.Suppress()` także
  w `Taken()`, nie tylko w `Finish()`.
  **Własne stacje frakcji (2026-08-01):** `StationSpawner` stawia GOTOWE stacje z vanilli
  8-15 km od gracza, każdej frakcji, która nie ma żadnego bloku ekonomicznego — HEL
  `GE_LogisticsFacility` (3362 bloki), KRW `RE19_PirateDepot`, WGR `RE05_StagingStation`.
  Pierwsza wersja stawiała ręcznie napisany prefab (płyta 3x2 z czterema blokami) — działała,
  ale wyglądała jak prototyp; vanilla ma gotowe, zaprojektowane stacje i to one idą do gry.
  ŻADNA vanillowa stacja nie ma terminala zleceń ani sklepu (sprawdzone: 0 i 0 w każdej
  kandydatce), więc mod dokłada oba po spawnie przez `IMyCubeGrid.AddBlock` — wolną kratkę
  stykającą się z kadłubem znajduje `CanAddCubes` na ŻYWEJ siatce (przy edycji XML prefabu
  trzeba by ją zgadywać), z preferencją najwyższej, czyli dachu. Kierunek spawnu jest
  rozłożony per frakcja co ~120°, bo przy losowaniu z samych tików trzy stacje potrafiły
  wylądować kilkaset metrów od siebie. Warunek postawienia jest STANEM ŚWIATA, nie zapisem
  w storage — stąd idempotencja po wczytaniu świata i samodzielna odbudowa po zburzeniu
  (karencja ~5 min). `/zf stacja` zostaje jako rusztowanie testowe, ale nie jest konieczne.
  **PUŁAPKA — vanillowy prefab to REKWIZYT ENCOUNTERU, nie stacja frakcji (2026-08-16).**
  Objaw zgłoszony przez gracza: „stacja powstaje zniszczona". Przyczyna nie leży w spawnie,
  tylko w tym, CO stawiamy. `GE_LogisticsFacility` (HEL) to placówka Factorum z GŁOWICAMI
  spiętymi z automatyką: na widok gracza nadaje ostrzeżenie i ODPALA SAMOZNISZCZENIE
  (opis encounteru na oficjalnej wiki — gracz ma zestrzelić głowice przed końcem odliczania).
  Stacja powstaje CAŁA i rozpada się dopiero przy pierwszym dolocie, czyli dokładnie wtedy,
  gdy gracz pierwszy raz na nią patrzy — stąd wrażenie, że przychodzi już zniszczona.
  Prefaby `RE*` to z kolei Random Encounters, czyli PORZUCONE WRAKI: w tym samym prefabie
  jadą siatki „Debris" i „Dead Engineer", a poszycie jest podziurawione z założenia.
  Poprawka: po spawnie (i raz po wczytaniu świata, bo stare zapisy mają stację uzbrojoną)
  `StationSpawner.Rozbroj` usuwa głowice z CAŁEGO kompleksu i wyłącza automatykę
  (`EventControllerBlock`/`TimerBlock`/`BroadcastController`), a `ObsluzRemont` dospawuje
  uszkodzone bloki porcjami po 200 na tik (3 tys. bloków w jednym tiku widać jako zwis).
  GRANICA: welder naprawia blok, który ISTNIEJE — dziur po blokach, których w prefabie nie
  ma, nie wypełni nic. Dlatego wartością jest rozbrojenie, a remont tylko sprząta po nim.
  Zakres rozbrojenia i remontu jest zawężony do siatek STATYCZNYCH tej frakcji w promieniu
  1,5 km (`KompleksWokol`) — bez tego mod naprawiałby za darmo bazę gracza obok i leczył
  świeżo ostrzelane rajdery. Strażnicy: `/zf autotest stacje` liczy głowice (twardo, ma być
  0) i procent uszkodzonych bloków (miękko) przez `StationSpawner.StanStacji`.
  **PUŁAPKA — `result[0]` ze `SpawnPrefab` to nie stacja (2026-08-04).** Prefaby encounterów
  wożą po kilka siatek i pierwsza bywa dekoracją: `RE19_PirateDepot[0]` to „Debris" (18 bloków,
  stacja jest pod `[2]`, 425 bloków), `RE05_StagingStation[0]` to „Dead Engineer" (1 blok,
  stacja pod `[2]`, 549). Mod przykręcał więc terminal zleceń KRW do gruzu, a WGR do zwłok —
  potwierdzone co do bloku w zapisie świata (18+3=21, 1+3=4). HEL działał PRZEZ PRZYPADEK, bo
  u niego `[0]` jest tą właściwą siatką. Bierzemy NAJWIĘKSZĄ siatkę z `result`; indeksów per
  prefab nie wpisujemy, bo zmieniają się z aktualizacjami gry, a błąd byłby znów cichy.
  Tamże: `ChangeGridOwnership` musi lecieć PO `AddBlock`, nie przed — `BigOwners` (wg
  dekompilacji `MyCubeGridOwnershipManager`: właściciele o maksymalnej liczbie FUNKCJONALNYCH
  bloków) inaczej nie mają się z czego przeliczyć i `FactionGrids` nie widzi stacji, choć
  bloki na niej stoją. Objaw: `/zf stations` mówi „BRAK bloku kontraktów", a spawner w kółko
  melduje „stacja istnieje, ale była niekompletna".
  **Cennik (`price_update`, 2026-07-31):** relacja rusza nie tylko liczbę w oknie frakcji,
  ale i to, ile płacisz przy ladzie. Brain liczy mnożnik (`[ceny]` w rules.toml, odcinkowo
  liniowo: -100 → `mnoznik_wrog`, 0 → dokładnie 1.0, +100 → `mnoznik_sojusznik`), mod
  przepisuje `PricePerUnit` ofertom na blokach sklepu frakcji. Klucz: MODOWY
  `Sandbox.ModAPI.IMyStoreBlock` ma `GetStoreItems` (wszystkie oferty bloku), a
  `VRage.Game.ModAPI.IMyStoreItem.PricePerUnit`/`Amount` są ZAPISYWALNE — ten z `Ingame`
  daje tylko Insert/Cancel/GetPlayerStoreItems i to on stał za wcześniejszą oceną, że
  cennika „nie da się ruszyć". Mnożnik liczony ZAWSZE od ceny bazowej (baza w
  `prices_mod_state.txt`), inaczej składałby się przy każdym wczytaniu świata. Poniżej
  `prog_embarga` frakcja nie handluje wcale: mod zeruje `Amount`, pamiętając stan magazynu
  z chwili embarga (odtwarzanie ilości bazowej byłoby darmową dostawą dla gracza, który
  wykupi stację tuż przed). Diagnostyka: `/zf ceny`.
  **Kontrakt opłaca WŁAŚCICIEL BLOKU** (ustalone 2026-07-29 dekompilacją `Sandbox.Game.dll`):
  `GenerateCustomContract` sprawdza `MyBankingSystem.GetBalance(startBlock.OwnerId)` i przy
  tworzeniu ŚCIĄGA z niego nagrodę. To konto tożsamości założyciela, a NIE konto frakcji
  (`IMyFaction.RequestChangeBalance` idzie pod `FactionId` i nic nie daje). Mod dosypuje
  właścicielowi bloku dokładnie obiecaną kwotę tuż przed `AddContract`. Tamże: `Duration`
  jest w MINUTACH, nie sekundach. Diagnostyka: `/zf stations`, `/zf kontrakty`,
  `/zf kontrakt-test <frakcja>`.
  **Metoda:** gdy ModAPI odmawia bez powodu, dekompiluj zamiast bisekcji —
  `dotnet tool install -g ilspycmd --version 8.2.0.7535`, potem
  `DOTNET_ROLL_FORWARD=LatestMajor ilspycmd -t <TypPelnaNazwa> <dll>` na `Bin64`.
- **Załogi NPC (AiEnabled, 2026-08-01) — ZALEŻNOŚĆ MIĘKKA.** Bez moda AiEnabled
  (Workshop 2596208372) nikt się nie pojawia, reszta działa bez zmian. MES ma wbudowaną
  integrację: wozi klienta `AiEnabledApi.cs` i sam stawia boty, a KLUCZOWE — nadaje botowi
  tożsamość członka frakcji i dopisuje go do niej (`BotSpawner.cs`:
  `SetPlayersFaction(botIdentity, faction.Tag)`). Dzięki temu wrogość bota leci po
  natywnej reputacji, czyli po naszej hybrydzie: ten sam bot macha przy dobrej relacji
  i strzela przy złej, BEZ respawnu.
  Dwie drogi, obie w użyciu: **deklaratywna** dla statków (`mod/Data/ZF_Boty.sbc` —
  profile `[MES Bot Spawn]`, akcje `[AddBotsToGrid]`, triggery `PlayerNear` 1,5 km,
  zachowania `ZF_Fighter_<TAG>` podpięte pod kadłuby rajdowe) i **programowa** dla stacji
  (`Crew.cs` + skopiowany `AiEnabledApi.cs`), bo stacje stawia nasz `PrefabManager`,
  a nie MES — profile MES ich nie obejmują. Programowa daje przy okazji imiona botów
  i uchwyty `entityId` pod rozkazy z brainu (Etap C/D: postacie w SQLite, radio od osoby,
  pamięć imienna). Boty NIE chodzą po małych siatkach — stąd załogi tylko na dużych.
  **PUŁAPKA — węzeł spawnu to domyślnie TAKŻE POSZYCIE ZEWNĘTRZNE (2026-08-16).** Objaw:
  „boty latają po prostu w przestrzeni". `GetAvailableGridNodes` z `onlyAirtightNodes=false`
  (domyślne) zwraca również kratki przy zewnętrznej ścianie, a w kompleksie stacji pierwsza
  pod ręką bywa 57-blokowa ładownia, która wnętrza nie ma wcale — `/zf zaloga` meldował wtedy
  „2 wolne węzły" i bot powstawał na burcie, w zerowej grawitacji, skąd odpływał. Drugą
  połową był SPOSÓB stawiania: dawaliśmy `Vector3.Forward/Up`, czyli osie ŚWIATA, choć API
  mówi wprost przy `GetGridMapMatrix` — „HINT: Use this as the orientation for bots spawned
  on this grid!" — więc bot stawał przekręcony względem pokładu. Teraz `Crew.SprobujKompleks`
  robi DWA przebiegi po siatkach kompleksu (posortowanych OD NAJWIĘKSZEJ): najpierw pyta
  o węzły HERMETYCZNE, a na poszycie schodzi dopiero, gdy żadna siatka wnętrza nie ma —
  i mówi o tym na czacie, bo taki bot faktycznie może odpłynąć. Orientacja idzie z macierzy
  mapy siatki. Gdyby ostrzeżenie o poszyciu padało regularnie (wraki bywają nieszczelne,
  a hala z rusztowań nie trzyma ciśnienia), następnym krokiem jest `GetInteriorNodes`
  (`enclosureRating`, nie ciśnienie) — asynchroniczne, więc wymaga przejęcia wyniku
  w tiku głównym.
  DO WERYFIKACJI: wartości `[BotType]`/`[BotBehavior]` wzięte z opisu na Workshopie,
  nie z plików moda (nie było go na dysku); wiki MES ostrzega, że `BotType` to pole
  `Name` z SBC, a nie SubtypeId.
- **Etap 7 — polish:** Kult, LCD na stacjach, emisariusze (AiEnabled API),
  własne flagowce, A/B Bielik. Dalej: QLoRA fine-tune radia, RL zachowań.

## Testowanie

- Komendy czatu w modzie: `/zf rel` (relacje + polityka frakcji), `/zf rep`
  (natywna reputacja w grze vs cel z brainu — kontrola hybrydy), `/zf ceny`
  (mnożnik cen per frakcja + ile ofert objął), `/zf tick`
  (wymuś tick), `/zf spawn <frakcja> [prefab]` (vanilla prefab, dowolny subtype — podgląd
  kadłubów bez AI), `/zf raid <frakcja> [patrol|raid|convoy]` (pełny potok MES; bez rodzaju
  brain dobiera flotę do nastroju frakcji), `/zf okup <frakcja>` (de-eskalacja bez LLM), `/zf kontrakt
  <frakcja> [typ]` (wymuszone zlecenie; typ opcjonalny — dostawa, nagroda, transport,
  naprawa, poszukiwania, wlasne), `/zf stations` (stacje i blok kontraktów),
  `/zf daj <surowiec> [ilość]` (towar do inwentarza — do testu trybutu bez trybu
  eksperymentalnego), `/zf stacja <frakcja>` (oddaje wskazaną siatkę frakcji NPC —
  jedyny sposób, by mieć blok kontraktów frakcji przed Etapem 7),
  `/zf event <json>` (wstrzyknij zdarzenie).
- **`/zf autotest [sekcja]`** (2026-08-01, rozszerzone 2026-08-02 i 2026-08-08):
  samosprawdzanie W GRZE.
  Sekcje bezpieczne (lecą bez argumentu): `stacje`, `ceny`, `rekwizyt`, `despawn`,
  `kontrakty`, `reputacja`, `okup`. Osobno `floty` i `boty` (spawnują prawdziwe rajdy — kosmos, świat
  testowy) oraz `wszystko`. Odpowiada na pytania, których nie da się zadać poza grą: czy
  `GetStoreItems` w ogóle zwraca oferty, czy `PricePerUnit` jest zapisywalne, czy mnożnik
  nie składa się po reloadzie, czy `SetNpcSpawnedGrid` ustawia flagę, czy MES stawia kadłub
  z naszej grupy i czy ten kadłub NAPRAWDĘ leci (dystans w oknie 30 s, bo sam blok zdalnego
  sterowania niczego nie dowodzi).
  **Sekcja `kontrakty` jest najważniejsza:** zamawia po kolei wszystkie sześć typów zleceń
  i porównuje typ ZAMÓWIONY z tym, który powstał. Mod ma przy zleceniach ostatnie słowo
  i przy braku celu po cichu wystawia dostawę — dotąd nie było jak zauważyć, że typ od
  tygodni degraduje, bo `contract_created` wraca poprawne i wszystko wygląda zdrowo.
  Hak: `ContractManager.OstatniTyp`/`OstatniPowod`/`LicznikRozstrzygniec`.
  **Sekcja `reputacja`** pokrywa hybrydę (M1–M3, M5, M7) — łącznie z tym, czy mod przywraca
  swój cel po tym, jak gra ruszy reputację sama.
  **Krok „FUNDAMENT custom"** (2026-08-09) jest jedynym w sekcji kontraktów TWARDYM dla
  zejścia na dostawę: sprawdza, czy gra przyjmuje `MyContractCustom` z naszej definicji
  `ZF_Zlecenie`. Od tego zależy CAŁA planowana rodzina własnych rodzajów zleceń
  (`docs/zlecenia-custom.md`), a do tej pory brak działającego custom kontraktu przechodził
  jako łagodne ostrzeżenie.
  **Sekcja `despawn`** (2026-08-08) spłaca kontrtest zaległy od Etapu 2 — patrz Etap 2 wyżej.
  Jest jedyną sekcją, której sedno jest NEGATYWNE („nic się nie stało"), więc ma kontrolę
  dodatnią: bez niej reguła zwracająca zawsze „nie" przechodziłaby na zielono, a zestrzelenie
  statku przestałoby cokolwiek znaczyć dla relacji.
  Autotest podaje mechanikom dokładnie takie ładunki, jakie przysłałby brain (`PriceManager
  .Handle`, `ReputationSync.Handle`, `RansomManager.HandleDemand`), więc **działa też bez
  uruchomionego `zf_brain.exe`**.
  KAŻDY krok zmieniający świat rejestruje przywrócenie — podsumowanie odwija je nawet wtedy,
  gdy sekcja padnie w połowie (cennik do x1.00, reputacja do wartości sprzed testu, zlecenia
  i rekwizyty skasowane, żądanie trybutu odwołane). Test, który zostawia po sobie embargo
  albo wrogą reputację, jest gorszy niż brak testu.
  Kroki monotoniczne („coś się pojawiło") są pollowane co 0,25 s zamiast czekać sztywne
  okno — sprawdzeń negatywnych pollować NIE WOLNO (przeszłyby w pierwszym tiku).
  Wynik na czat ORAZ do `events.jsonl` (`autotest_result` per krok, `autotest_summary`
  z bilansem na koniec — po tym drugim poznasz, że przebieg się skończył, a nie urwał).
  NIE zastąpi tego, co wymaga człowieka za sterami (dolot, złapanie rekwizytu, PRZYJĘCIE
  zlecenia w terminalu, ocena brzmienia radia i sylwetki kadłuba).
- Brain: `--mock-llm`, `--replay <plik.jsonl>` (odtworzenie zdarzeń bez gry).
- Mostek testowalny bez SE: dopisuj linie do events.jsonl ręcznie.
- `ctest --test-dir brain/build`: mostek, silnik (relacje/stany/kontrakty/typy
  zleceń/polityka/cennik), sanityzacja wyjścia LLM, config z auto-wykrywaniem storage
  i wagami typów kontraktów. Testy wymuszają
  asserty także w Release (`-UNDEBUG`) — bez tego przechodziły nic nie sprawdzając.
- **Strażnicy długu technicznego** (2026-08-08). Osobna kategoria: nie bronią działającej
  mechaniki, tylko decyzji „tego jeszcze NIE WŁĄCZAMY, bo brakuje drugiej połowy". Taka
  decyzja żyła dotąd wyłącznie w komentarzu i dzieliła ją od cofnięcia jedna cyfra w configu.
  `brain/tests/dlug_test.cpp` czyta PRAWDZIWY `rules.toml` (nie syntetyczny — inaczej nie
  mówiłby nic o tym, co dostaje gracz) i sprawdza: waga 0 typów zablokowanych u każdej
  frakcji i w każdym stanie, 1200 losowań kontrolnych, brak typów wyłączonych po cichu bez
  wpisu w tabeli `kBlokady`, jawny mnożnik trudności dla każdego typu. Każdy wpis `kBlokady`
  niesie POWÓD i WARUNEK ODBLOKOWANIA, które lądują wprost w komunikacie FAIL — a spłacenie
  długu wymaga skreślenia wpisu, czyli świadomego potwierdzenia przez człowieka.
  Drugą bramkę (waga kontra kod moda) trzyma `tools/waliduj_sbc.py` — patrz Etap 6.
- **Scenariusze** (`brain/tests/scenariusze/*.jsonl`, 2026-08-02): plik JSONL, w którym
  obok zdarzeń z gry stoją OCZEKIWANIA (`{"oczekuj":"ceny","frakcja":"WGR","mnoznik":1.18}`).
  `--replay` wraca kodem 1, gdy któreś nie wyjdzie, więc `ctest` uruchamia je wprost.
  Wcześniej CI robiło `grep` na stdout, czyli sprawdzało tylko, że brain się nie wywrócił.
  Cztery pliki, 99 sprawdzeń, pokrywają brainową połowę mechanik pilnowanych dotąd
  wyłącznie ręcznie:
  `ceny.jsonl` (N2–N8, N12), `kontrakty.jsonl` (I2/I5/I6/I8/I9/I14/I19/I20/I21/I22, M4),
  `okup.jsonl` (K2–K6, L1–L3), `reputacja.jsonl` (M1–M3, M7).
  Wartości nie są przepisane z przebiegu, tylko WYLICZONE z `rules.toml` — np. zlecenie
  wymuszone idzie po `prog_relacji`, czyli kosztuje dokładnie `nagroda_min × mnożnik typu`,
  co czyni tabelę `[kontrakty.mnoznik]` sprawdzalną co do złotówki i niezależną od losowania.
  Linia `{"restart":true}` tworzy nowy `Engine` na TEJ SAMEJ bazie — to test, co siedzi
  w SQLite, a co tylko w RAM. Uwaga na granicę: scenariusz mówi, co brain LICZY i WYSYŁA,
  nigdy czy gra to przyjmie — od tego jest `/zf autotest`. Tam, gdzie granica przecina
  jeden test ręczny (K5: „po pokoju nie ma kary" jest gwarancją MODU, bo to on przestaje
  wysyłać `ransom_expired`), scenariusz mówi o tym wprost zamiast udawać pokrycie.
- **PUŁAPKA — `*.jsonl` w `.gitignore` zjadło całą tę warstwę** (znalezione 2026-08-02):
  scenariusze nigdy nie trafiły do repo, bo globalna reguła dla danych runtime mostka
  łapała też je. `file(GLOB)` nie znajdował ani jednego pliku, pusty glob to w CMake
  CISZA (nie błąd), więc `ctest` pokazywał 4 testy zamiast 8, a CI świeciło na zielono,
  nie uruchamiając NICZEGO z tej warstwy — kod `scenariusz.cpp` był martwy przez tydzień.
  Zabezpieczenia: wyjątek `!brain/tests/scenariusze/*.jsonl` w `.gitignore` ORAZ
  `FATAL_ERROR` w `brain/tests/CMakeLists.txt`, gdy glob nic nie zwróci. Glob ma
  `CONFIGURE_DEPENDS`, bo bez tego świeżo dopisany scenariusz nie pojawiał się w `ctest`
  aż do ręcznego `cmake` — i łatwo było uznać, że przechodzi.
- **`tools/waliduj_sbc.py`** (2026-08-01): walidator danych moda. Gra nie mówi, że grupa
  spawnu wskazuje na nieistniejące zachowanie — po prostu nic się nie spawnuje albo statek
  dryfuje. Skrypt sprawdza referencje SpawnGroups ↔ RivalAiBehaviors ↔ ZF_Manipulations ↔
  ZF_Boty ↔ kod (`GroupForKind`, nazwy prefabów, tablice w `Stations.cs`), wymagany
  `[RivalAiSpawn:true]`, jednorozmiarowość grup z manipulacją pilota oraz duże kadłuby bez
  zdalnego sterowania (przyczyna dryfujących konwojów). Vanillowych prefabów nie da się
  potwierdzić bez plików gry, więc trzyma jawną tabelę `ZNANE_PREFABY` — prefab spoza niej
  to błąd z prośbą o dopisanie. `tools/test_waliduj_sbc.py` psuje dane na kopii i wymaga
  wykrycia każdej usterki (walidator, który zawsze mówi „czysto", byłby bezwartościowy);
  23 przypadki, w tym ostrzeżeniowe — ostrzeżenie, które nigdy nie pada, jest tak samo
  bezwartościowe jak test bez asercji.
  **Dług kontraktów (2026-08-08, `--rules`):** walidator jest JEDYNYM miejscem widzącym
  jednocześnie `brain/configs/rules.toml` i kod C# moda. Dwie reguły. (1) Typ zlecenia wolno
  włączyć dopiero, gdy mod ma to, czego typ potrzebuje: `wlasne` wymaga
  `TryFinishCustomContract` w `Contracts.cs`, `nagroda` daje samo ostrzeżenie (kod jest
  gotowy, wątpliwa jest mechanika gry). (2) KAŻDY typ z wagą > 0 musi mieć swój
  `case` w `Contracts.cs` — bez niego `switch (kind)` schodzi na `default`, po cichu wystawia
  DOSTAWĘ i melduje sukces, a brain zapisuje w SQLite typ, o który prosił. Dokładnie ten
  kształt błędu miała eskorta przez trzy przebiegi. Reguła (2) zastąpiła wpis o eskorcie:
  jedna zasada ogólna zamiast wyjątku na każdy martwy typ.
- CI (`.github/workflows/brain.yml`): build Debug+Release BEZ llama.cpp, ctest
  (z scenariuszami) i smoke test `--replay`; osobna praca puszcza walidator SBC i jego
  kontrtest. Wariant bez LLM łatwo psuje się niezauważenie.
- **Kompilacja moda BEZ wchodzenia do gry** (2026-08-01): błąd składni w C# kosztuje
  inaczej pełne przeładowanie świata. Roslyn z VS + zestawy z `Bin64` sprawdzają to
  w kilka sekund:
  `csc.exe -langversion:6 -target:library -out:<tmp>.dll -r:<Bin64>\{Sandbox,VRage,SpaceEngineers,protobuf}*.dll -r:<...>\Facades\netstandard.dll mod\Data\Scripts\ZyweFrakcje\*.cs`
  Pułapki: `csc.exe` z `Microsoft.NET\Framework64` umie tylko C# 5 i wywala się na
  `MESApi.cs` — trzeba Roslyna z `MSBuild\Current\Bin\Roslyn`. Natywnych DLL z `Bin64`
  (`VRage.Native`, `Havok`, `steam_api64`…) nie wolno podawać jako `-r:` (CS0009),
  a bez `netstandard.dll` z Facades sypie się CS0012 na `ValueType`.
  To sprawdza SKŁADNIĘ I TYPY, nie whitelistę ModAPI — tę weryfikuje dopiero gra.
  Gotowiec: `tools/sprawdz-mod.ps1` (sam znajduje Bin64 i Roslyna, filtruje natywne DLL).

## Konwencje

- C#: ModAPI whitelist! Zero System.Net, zero plików poza
  MyAPIGateway.Utilities.*FileInStorage. Jeden MySessionComponentBase.
- C++: CMake, warnings-as-errors, brak wyjątków w pętli głównej mostka.
- Wszystkie stringi widoczne dla gracza — po polsku.
- Commituj po każdym etapie; wiadomości commitów po polsku.
