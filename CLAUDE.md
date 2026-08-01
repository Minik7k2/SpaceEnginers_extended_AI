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
  Do zrobienia przy okazji: kontrtest, że despawn MES NIE generuje
  grid_destroyed. Po drodze naprawiony mostek braina: offsety linii
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
- **Etap 6 — kontrakty i ceny:** kod gotowy, DO WERYFIKACJI W GRZE (sekcja I
  w docs/testy-reczne.md). Frakcja wystawia zlecenie w ticku (`[kontrakty]`
  w rules.toml) → mod tworzy je przez `MyAPIGateway.ContractSystem` na bloku
  kontraktów/sklepu frakcji → `contract_created` z prawdziwym ID ląduje w SQLite →
  wykonanie/porażka wraca jako `contract_done`.
  **Siedem typów zleceń** (2026-07-30), po jednej klasie z `Sandbox.ModAPI.Contracts`:
  `dostawa` (Acquisition), `nagroda` (Bounty), `transport` (Hauling), `naprawa`
  (Repair), `poszukiwania` (Search), `eskorta` (Escort), `wlasne` (Custom +
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
  **Rekwizyty (2026-07-30):** frakcja sama przygotowuje robotę — `poszukiwania`
  i `naprawa` stawiają prefab z `mod/Data/Prefabs/ZF_ContractProps.sbc` (zgubiony
  moduł / uszkodzony wrak, właściciel = frakcja) i dopiero w callbacku spawnu tworzą
  kontrakt. Cel poszukiwań to WYŁĄCZNIE nasz moduł: vanillowe zlecenie każe przywieźć
  znaleziony grid pod stację, a stacji nikt nie przywiezie.
  **Przyjęcie zlecenia (`contract_taken`):** gracz bierze robotę → konwój do eskorty
  dopiero teraz wyrusza, cel nagrody dostaje ochronę, a każdy WRÓG wystawcy traci do
  gracza `kontrakt_przyjety_u_wroga` (-3). Kara raz na kontrakt (status `taken`
  w SQLite, liczy się do `max_otwartych` jak `open`).
  **Wagi 0 do czasu testów w grze:** `nagroda` (vanilla liczy zabicia GRACZY, nie NPC)
  i `eskorta` (typ usunięty z gry w 2026) — kod kompletny, wystarczy wpisać wagę.
  Handel wykrywany heurystycznie (zmiana salda + sklep frakcji <300 m), bo ModAPI
  nie ma zdarzenia transakcji. UWAGA: kaucję ściąganą przy PRZYJĘCIU zlecenia ta sama
  heurystyka brała za zakup (darmowe +1..+3 relacji), stąd `_trade.Suppress()` także
  w `Taken()`, nie tylko w `Finish()`.
  **Własne stacje frakcji (2026-08-01):** `StationSpawner` stawia prefab `ZF_Stacja`
  (`mod/Data/Prefabs/ZF_Stations.sbc`: blok kontraktów + sklep + bateria + radiolatarnia,
  siatka duża, statyczna) 6-12 km od gracza każdej frakcji, która nie ma żadnego bloku
  ekonomicznego. Warunek jest STANEM ŚWIATA, nie zapisem w storage — dzięki temu rzecz
  jest idempotentna po wczytaniu świata, a zburzona stacja odbudowuje się po karencji
  (~5 min). `/zf stacja` zostaje jako rusztowanie testowe, ale nie jest już konieczne.
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
- **Etap 7 — polish:** Kult, LCD na stacjach, emisariusze (AiEnabled API),
  własne flagowce, A/B Bielik. Dalej: QLoRA fine-tune radia, RL zachowań.

## Testowanie

- Komendy czatu w modzie: `/zf rel` (relacje + polityka frakcji), `/zf rep`
  (natywna reputacja w grze vs cel z brainu — kontrola hybrydy), `/zf ceny`
  (mnożnik cen per frakcja + ile ofert objął), `/zf tick`
  (wymuś tick), `/zf spawn <frakcja>` (vanilla prefab), `/zf raid <frakcja>`
  (potok MES), `/zf okup <frakcja>` (de-eskalacja bez LLM), `/zf kontrakt
  <frakcja> [typ]` (wymuszone zlecenie; typ opcjonalny — dostawa, nagroda, transport,
  naprawa, poszukiwania, eskorta, wlasne), `/zf stations` (stacje i blok kontraktów),
  `/zf daj <surowiec> [ilość]` (towar do inwentarza — do testu trybutu bez trybu
  eksperymentalnego), `/zf stacja <frakcja>` (oddaje wskazaną siatkę frakcji NPC —
  jedyny sposób, by mieć blok kontraktów frakcji przed Etapem 7),
  `/zf event <json>` (wstrzyknij zdarzenie).
- Brain: `--mock-llm`, `--replay <plik.jsonl>` (odtworzenie zdarzeń bez gry).
- Mostek testowalny bez SE: dopisuj linie do events.jsonl ręcznie.
- `ctest --test-dir brain/build`: mostek, silnik (relacje/stany/kontrakty/typy
  zleceń/polityka/cennik), sanityzacja wyjścia LLM, config z auto-wykrywaniem storage
  i wagami typów kontraktów. Testy wymuszają
  asserty także w Release (`-UNDEBUG`) — bez tego przechodziły nic nie sprawdzając.
- CI (`.github/workflows/brain.yml`): build Debug+Release BEZ llama.cpp, ctest
  i smoke test `--replay`. Wariant bez LLM łatwo psuje się niezauważenie.
- **Kompilacja moda BEZ wchodzenia do gry** (2026-08-01): błąd składni w C# kosztuje
  inaczej pełne przeładowanie świata. Roslyn z VS + zestawy z `Bin64` sprawdzają to
  w kilka sekund:
  `csc.exe -langversion:6 -target:library -out:<tmp>.dll -r:<Bin64>\{Sandbox,VRage,SpaceEngineers,protobuf}*.dll -r:<...>\Facades\netstandard.dll mod\Data\Scripts\ZyweFrakcje\*.cs`
  Pułapki: `csc.exe` z `Microsoft.NET\Framework64` umie tylko C# 5 i wywala się na
  `MESApi.cs` — trzeba Roslyna z `MSBuild\Current\Bin\Roslyn`. Natywnych DLL z `Bin64`
  (`VRage.Native`, `Havok`, `steam_api64`…) nie wolno podawać jako `-r:` (CS0009),
  a bez `netstandard.dll` z Facades sypie się CS0012 na `ValueType`.
  To sprawdza SKŁADNIĘ I TYPY, nie whitelistę ModAPI — tę weryfikuje dopiero gra.

## Konwencje

- C#: ModAPI whitelist! Zero System.Net, zero plików poza
  MyAPIGateway.Utilities.*FileInStorage. Jeden MySessionComponentBase.
- C++: CMake, warnings-as-errors, brak wyjątków w pętli głównej mostka.
- Wszystkie stringi widoczne dla gracza — po polsku.
- Commituj po każdym etapie; wiadomości commitów po polsku.
