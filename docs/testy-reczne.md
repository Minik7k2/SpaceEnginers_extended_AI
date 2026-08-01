# Testy ręczne w grze — Etapy 1–6

## Zanim wejdziesz do gry: co sprawdza się samo

Trzy bramki działają BEZ Space Engineers i wyłapują większość regresji, więc odpal je
najpierw — ręczna lista ma sens dopiero, gdy są zielone.

| Narzędzie | Co pokrywa | Jak uruchomić |
|---|---|---|
| `ctest --test-dir brain/build` | logika braina + **scenariusze** (`brain/tests/scenariusze/*.jsonl`): brainowa połowa I5/I6/I8/I20/I21/I22, K2–K5, L1–L3, M4, N2–N8, N12 | po `cmake --build brain/build` |
| `python3 tools/waliduj_sbc.py` | referencje w danych moda: grupy spawnu ↔ zachowania ↔ manipulacje ↔ boty ↔ kod (sekcje O i P niżej) | z korzenia repo |
| `tools/sprawdz-mod.ps1` | czy mod w ogóle się kompiluje (składnia i typy, NIE whitelista) | Windows z SE + Visual Studio |

**Uwaga o podziale.** Scenariusze sprawdzają, co brain LICZY i WYSYŁA — nie to, czy gra
to przyjmie. „N3 zielone w ctest" znaczy tylko tyle, że mnożnik wyszedł 1.324; czy cena
w terminalu naprawdę urosła, rozstrzyga `/zf autotest ceny` albo oko.

## W grze: `/zf autotest [sekcja]`

Jedna komenda zamiast przeklikiwania listy. Sprawdza to, czego nie da się sprawdzić poza
grą — czy ModAPI naprawdę robi to, co zakładamy. Wynik leci na czat i do `events.jsonl`
(typ `autotest_result`), więc widać go też w konsoli brainu.

- `/zf autotest` — sekcje bezpieczne: `stacje`, `ceny`, `rekwizyt` (nic nie spawnują wrogo).
- `/zf autotest ceny` — **bramka całej sekcji N**: czy `GetStoreItems` zwraca oferty, czy
  `PricePerUnit` jest zapisywalne, czy mnożnik liczy się od bazy (a nie składa), czy embargo
  zeruje `Amount` i czy zniesienie embarga wraca do ilości sprzed niego.
- `/zf autotest stacje` — I1a–I1c: każda frakcja ma blok kontraktów i sklep.
- `/zf autotest rekwizyt` — I19a: czy `SpawningOptions.SetNpcSpawnedGrid` faktycznie ustawia
  flagę (bez niej poszukiwania zawalają się sekundę po przyjęciu).
- `/zf autotest floty` — sekcja O: czy rajd każdej frakcji staje i czy kadłub ma pilota.
  **Tylko w kosmosie i na świecie testowym** — stawia prawdziwe statki rajdowe.
- `/zf autotest boty` — sekcja P: czy załoga się pojawia (miękko: bez AiEnabled to
  OSTRZEŻENIE, nie błąd).
- `/zf autotest wszystko` — wszystko po kolei, kilka minut.

Przygotowanie: uruchom świat z modami SE_ZyweFrakcje + MES, obok odpal
`brain\cmake-build-debug\zf_brain.exe` (konsola musi pokazać `[brain] start,
storage=...` ze ścieżką TEGO świata — jeśli nie, popraw
`brain/configs/rules.local.toml`). Po starcie brain wypisuje stan relacji.

Przy każdym teście patrz na DWA miejsca: czat w grze i konsolę braina.

## A. Most (regresja Etapów 1–2)

- [x] ~~**A1. Echo:** napisz cokolwiek na czacie → wraca `[RADIO | TEST] Echo: ...`~~
  NIEAKTUALNE: echo z Etapu 1 usunięte (od Etapu 5 było już tylko szumem w czacie).
  Życie mostka sprawdzasz teraz przez `/zf rel` albo `@KRW ...` — patrz J5.
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
- [x] **F11. De-eskalacja deterministyczna (`/zf okup`):** w kosmosie `/zf raid KRW`,
  potem `/zf okup KRW` → konsola braina `deeskalacja KRW: przyjęto (relacja +15…)`
  i `stand_down [KRW]`; na czacie `[ZF] stand_down KRW: N statk(i) wstrzymuje ogień
  i despawnuje` → statki KRW **przestają strzelać** i po ~10 s **znikają**. `/zf rel`
  pokazuje relację wyższą o 15. `/zf okup KRW` bez aktywnego rajdu → „nie prowadzi rajdu".
- [x] **F12. Anteny wyciszone:** po `/zf raid KRW` **nie ma** angielskiego gadania
  z anten („Engineer, fill up the collector…"). Nasze polskie radio (`[RADIO | KRW]`)
  działa normalnie.
- [x] **F13. De-eskalacja przez LLM (`@KRW`):** w trakcie rajdu `@KRW biorę okup…`
  → jeśli model odpuści: `deeskalacja` + `stand_down` jak w F11. Rozszerzona gramatyka
  dokłada pole `odpuszcza`. Zweryfikowane W GRZE 2026-07-23 na **qwen-3B** (odmówił
  przy 1000 sztabkach, odpuścił przy 4000 — wbrew wcześniejszej obawie, że potrzebny
  Bielik). Przy okazji naprawiony wyciek markera „odpuszcza=true" do treści radia
  widocznej dla gracza (sanityzacja w `llm.cpp` + rozdzielenie pola/wypowiedzi
  w prompcie) — do ponownego sprawdzenia, że gracz nie widzi już markera.

## G. Radio: kolor / kolejka / TTL (Etap 5a)

Radio przechodzi teraz przez `RadioDisplay`: kolejka priorytetowa, odstęp między
wyświetleniami i TTL 2 min. Limit 1/min/frakcję poza walką dalej narzuca brain
(to nie ta sekcja). UWAGA kolor: kolorowanego CZATU nie da się zrobić na whiteliście
ModAPI (`MyVisualScriptLogicProvider` poza whitelistą — potwierdzone logiem), więc
radio wyświetla się białym jak dotąd. Kolor frakcji ewentualnie później jako HUD.

- [x] **G1. Format radia:** `@KRW witam` → `RADIO | KRW: ...` (biało). `@HEL`/`@WGR`
  analogicznie, `/zf rel` → `RADIO | SYSTEM: ...`. Nazwa nadawcy niesie `RADIO | TAG`.
- [x] **G2. Bez zalania czatu:** wymuś kilka wiadomości na raz (np. `/zf tick`
  kilka razy pod rząd albo rajd, który generuje serię) → linie radia pojawiają się
  **jedna po drugiej** (~co 0.3 s), nie wszystkie w jednej klatce.
- [x] **G3. Priorytet bojowy:** w trakcie rajdu KRW, gdy równocześnie czeka zwykła
  gadka i groźba bojowa (priority=1) → **bojowa wychodzi pierwsza**. Obserwacyjne;
  trudne do wymuszenia deterministycznie.
- [x] **G4. TTL 2 min:** doprowadź do zaległego radia (brain pisze `radio_message`,
  gdy świat nie jest wczytany), wróć do świata po **>2 min** → stare radio **NIE**
  wyskakuje (bez TTL wyskakiwało jako „zaległe echo"). Świeże radio dalej działa.

## H. Adresowanie zasięgiem i szum (Etap 5c)

Mod liczy dystans do najbliższego statku frakcji i nadaje adresatowi jakość łączności:
`clear` ≤6 km, `weak` (szum) ≤15 km, dalej `none`. Brain bramkuje `@FRAKCJA` sygnałem.
`@frakcja` bez statku frakcji w pobliżu **nie** dostanie odpowiedzi — to nie błąd, to zasięg.
Wyłącznik na testy: `[radio] wymagaj_zasiegu = false` (hot-reload). Weryfikowane bez SE
na `--replay` (bramka none/clear/weak); w grze do odhaczenia:

- [x] **H1. Poza zasięgiem:** `@KRW witam`, gdy żaden statek KRW nie jest w pobliżu
  (lub >15 km) → `[RADIO | SYSTEM] Brak zasięgu — KRW nie odpowiada.`, konsola
  `chat: KRW poza zasięgiem`. KRW **nie** odpowiada.
- [x] **H2. Czysty odbiór:** statek KRW blisko (≤6 km, np. tuż po `/zf raid KRW`) →
  `@KRW witam` → normalna odpowiedź głosem persony (LLM), bez szumu.
- [x] **H3. Szum (słaby sygnał):** statek KRW daleko (~6–15 km) → `@KRW oddaj wrak` →
  KRW **odpowiada**, ale w konsoli braina zdarzenie `chat_message` ma treść w trzaskach
  (`o..aj w.ak`), a odpowiedź jest krótsza/„przez zakłócenia". W swoim czacie widzisz
  swój oryginał (to frakcja cię niedosłyszała).
- [x] **H4. Wyłącznik zasięgu:** `rules.toml` → `[radio] wymagaj_zasiegu = false`
  (hot-reload) → `@KRW` z dowolnej odległości znów odpowiada (zachowanie sprzed 5c).
  Cofnij po teście.
- [x] **H5. Reakcja na szum (ulepszone):** przy słabym sygnale (~6–15 km) frakcja
  ma **zareagować na zakłócenia** — powiedzieć, że rwie się/trzeszczy i kazać powtórzyć
  albo podejść bliżej, a NIE zgadywać treści ani (w trakcie rajdu) przyjmować okupu.
- [x] **H6. Pamięć dialogu:** rozmawiaj z KRW w zasięgu przez kilka wiadomości (np.
  negocjuj rozejm) → frakcja **trzyma wątek** (pamięta ostatnie ~4 tury), nie odpowiada
  za każdym razem od zera. Ephemeralne — restart braina czyści pamięć rozmowy.
- [x] **H7. Sanitizer wyjścia:** w wypowiedziach frakcji **nie ma** końcowego podpisu
  (`- KRW`), prefiksu nazwą (`KRW: ...`) ani `@Gracz` (co najwyżej „Gracz").

## I. Ekonomia: handel i kontrakty (Etap 6) — DO WERYFIKACJI

Ta sekcja jest nowa i **żaden test nie jest jeszcze odhaczony**. Kod kontraktów
opiera się na `MyAPIGateway.ContractSystem` (`MyContractAcquisition`), którego nie
da się skompilować poza grą — jeśli mod nie wstanie, log SE wskaże plik
`Contracts.cs` i konkretną linię; najbardziej podejrzane są sygnatura konstruktora
i jednostka `duration` (zakładamy SEKUNDY: `durationMin * 60`).

**ROZSTRZYGNIĘTE 2026-07-29 (dekompilacja, nie zgadywanie) — czemu zlecenia nie powstawały.**
`AddContract` zwracał `Success=false` bez powodu (`MyAddContractResultWrapper` niesie tylko
`Success`/`ContractId`/`ContractConditionId`), więc kod gry odczytano wprost —
`ilspycmd` na `Sandbox.Game.dll`, `MySessionComponentContractSystem.GenerateCustomContract`:

```csharp
if (startBlock != null && MyBankingSystem.GetBalance(startBlock.OwnerId) < contractData.MoneyReward)
    return MyContractCreationResults.Fail_NotEnoughFunds;
...
MyBankingSystem.ChangeBalance(startBlock.OwnerId, -moneyReward);   // nagroda ściągana przy tworzeniu
```

Dwa wnioski: (1) liczy się konto **właściciela bloku** (tożsamość założyciela frakcji), a NIE
konto frakcji — `IMyFaction.RequestChangeBalance` idzie pod `FactionId` i kontraktów nie
odblokuje; (2) nagroda jest z tego konta **wydawana**, więc startowe ~14 tys. kr wyczerpuje się
po kilku zleceniach (stąd mylący objaw „z każdą rundą testu przechodzą coraz mniejsze kwoty").
Mod dosypuje teraz właścicielowi bloku dokładnie tyle, ile frakcja obiecuje, tuż przed
`AddContract`. Z tego samego kodu: **`Duration` jest w MINUTACH**
(`RemainingTimeInS = MyTimeSpan.FromMinutes(Duration)`) — wcześniejsze `durationMin * 60`
zamawiało 45 godzin. Odrzucone hipotezy (nie marnować na nie czasu): `factionStationId`
(frakcje MAJĄ stacje — WGR 7, KRW 9 — i podanie id nic nie zmienia), `endBlockId`,
kaucja, ilość i wartość towaru, saldo gracza, limit slotów na bloku.
Diagnostyka: `/zf stations` (saldo frakcji + blok), `/zf kontrakty` (co gra trzyma na bloku
i na stacjach), `/zf kontrakt-test <frakcja>` (macierz wariantów `AddContract`).

- [x] **I0. Mod się ładuje:** świat startuje, w logu SE brak błędów kompilacji
  z `Contracts.cs` / `Economy.cs`. To bramka dla całej sekcji.
- [x] **I1. Diagnostyka bloków:** `/zf stations` → dla każdej frakcji widać albo
  `blok kontraktów: <nazwa> (id)`, albo `BRAK bloku kontraktów/sklepu`. Bez bloku
  zlecenia nie powstaną — to oczekiwane, nie błąd (potrzebna stacja frakcji, np.
  ze spawnu MES z blokiem sklepu/kontraktów).
**Jak w ogóle dojść do bloku kontraktów (2026-07-28).** Vanilla nie pozwala oddać
budowli frakcji NPC, a bez tego I2-I10 nie da się ruszyć. Droga na skróty:
1. tryb kreatywny / narzędzia kreatywne → postaw mały statyczny grid,
2. dostaw **Blok kontraktów** (albo Sklep) i włącz zasilanie,
3. wyceluj w grid i wpisz `/zf stacja WGR` → mod przepisuje siatkę na frakcję i od razu
   mówi, czy blok kontraktów został wykryty,
4. `/zf stations` musi teraz pokazać `blok kontraktów: <nazwa> (id)` zamiast `BRAK`.
Od 2026-08-01 nie jest to już konieczne — frakcje stawiają sobie stacje same (I1a-I1d
niżej), a `/zf stacja` zostaje wyłącznie jako rusztowanie do testów.

- [ ] **I1a. Stacja powstaje sama:** NOWY świat, nie ruszaj `/zf stacja` → w ciągu ~30 s
  na czacie `HEL/KRW/WGR postawiła stację X km stąd`, jedna frakcja na przebieg.
  `/zf stations` pokazuje dla każdej `blok kontraktów: Stacja <TAG>`.
- [ ] **I1b. Nie duplikuje się:** zapisz i wczytaj świat → NIE MA nowych stacji (warunek
  to stan świata: frakcja z blokiem ekonomicznym jest pomijana). To samo po `/zf stacja`
  — frakcja z ręcznie oddaną siatką nie dostaje drugiej stacji.
- [ ] **I1c. Stacja działa jak stacja:** dolec do niej → terminal zleceń pokazuje kontrakty
  tej frakcji (`/zf kontrakt <TAG>`), a `/zf ceny` widzi jej sklep (`ofert: N`).
  Jeśli `ofert: 0`, sklep NPC nie dostał asortymentu — patrz sekcja N, to osobny problem.
- [ ] **I1d. Odbudowa po zniszczeniu:** zburz stację frakcji → po ~5 min karencji frakcja
  stawia nową. Sprawdź, że w międzyczasie czat NIE spamuje komunikatem co 30 s.

- [x] **I2. Wymuszone zlecenie:** `/zf kontrakt WGR` → konsola braina
  `contract_create [WGR] dostawa za N kr`, a na czacie `[ZF] Nowe zlecenie WGR: …`.
  Jeśli zamiast tego „pominięty: frakcja nie ma bloku…" — patrz I1.
- [x] **I3. Zlecenie widać w grze:** w terminalu stacji tej frakcji (zakładka
  kontraktów) jest nowe zlecenie na dostawę, z nagrodą z I2.
- [x] **I4. Utrwalenie ID:** konsola braina `kontrakt <ID> (WGR, dostawa) wystawiony
  w grze`; po `/zf rel` frakcja bez zmian (samo wystawienie nie rusza relacji).
- [x] **I5. Wykonanie:** przyjmij i wykonaj zlecenie → `[ZF] Zlecenie WGR wykonane`,
  konsola: `relacja WGR->gracz +20 za wykonany kontrakt`. To ma być SZYBSZA droga
  do poprawy relacji niż dryf — o to w tym całym etapie chodzi.
  Zweryfikowane 2026-07-30 na typie `naprawa` (mnożnik 1.2 → `+24` zamiast bazowych
  `+20`, zgodnie z `[kontrakty.mnoznik]`).
- [x] **I6. Porażka:** przyjmij zlecenie i daj mu wygasnąć → `Zlecenie … zawalone`
  i `-10` w konsoli.
  Zweryfikowane 2026-07-31 na typie `poszukiwania`: `relacja WGR->gracz -9 za zawalony
  kontrakt (poszukiwania, mnożnik 0.9)` — ścieżka `MyCustomContractStateEnum.Failed`
  (odpytywanie w `Contracts.Update`) → `contract_done(success=false)` →
  `-kontrakt_min × mnożnik` działa, razem z `reputation_sync` i `price_update`.
  ⚠️ ALE porażka przyszła **1,2 s po przyjęciu** (tabela `events` w bazie braina:
  `zlecenie_przyjete` 18:20:32.9 → `kontrakt_fail` 18:20:34.1), więc to NIE był timeout.
  Przyczyna ustalona dekompilacją — patrz I19a. Test trzeba powtórzyć na zlecenie,
  które faktycznie się przedawni.
- [x] **I7. Po wczytaniu świata:** wystaw zlecenie, zapisz i wczytaj świat, dopiero
  potem je wykonaj → rozliczenie MIMO że callbacki nie przeżywają zapisu (mod
  dopytuje o stan co ~5 s, ID trzyma w `contracts_mod_state.txt`).
- [x] **I8. Limit i cooldown:** po wystawieniu jednego zlecenia frakcja nie wystawia
  drugiego (`max_otwartych = 1`), a po jego rozliczeniu następne dopiero po
  `cooldown_min` (20 min).
- [x] **I9. Wrogość zamyka kran:** doprowadź KRW do wojny → w ticku nie ma dla niej
  `contract_create` (próg `prog_relacji = -55`).
- [x] **I10. Handel:** sprzedaj/kup coś w sklepie frakcji (≤300 m od jej stacji) →
  na czacie `[ZF] Handel z <TAG>: N kr`, w konsoli `relacja … +1..+3 za handel`.
- [x] **I11. Fałszywe alarmy handlu:** zapłać okup (`/zf okup KRW` przy rajdzie) i
  odbierz nagrodę za kontrakt → **NIE MA** komunikatu o handlu (wyciszenie ~10 s).
- [x] **I12. Stacja to nie statek:** zniszcz statyczną siatkę frakcji (stację) →
  konsola `-50 za zniszczenie stacji` ORAZ `sufit relacji … obniżony na stałe do +20`;
  `/zf rel` pokazuje `sufit +20`. Potem nawet wykonane kontrakty nie podniosą
  relacji powyżej sufitu — to celowe (świat mściwy).

- [x] **I13. Typy zleceń — losowanie:** `/zf kontrakt WGR` kilka razy pod rząd →
  w konsoli braina `kontrakt: WGR wystawia zlecenie (<typ>)` z RÓŻNYMI typami
  (wagi z `[kontrakty.typy.WGR]`: najczęściej `naprawa`, potem `dostawa`/`poszukiwania`).
  Na czacie `[ZF] Nowe zlecenie WGR (<typ>): …`.
  Zweryfikowane 2026-07-31 przez `/zf tick` (nie `/zf kontrakt`): dwie rundy ofert dały
  HEL `naprawa` → `dostawa`, KRW `dostawa` → `transport`, WGR `poszukiwania` — typ jest
  losowany osobno przy każdym wystawieniu, nie przypisany frakcji na stałe. Wagi per
  frakcja trzymają: KRW nie dostało ani razu `naprawa` (waga 0), a mnożniki w logu
  zgadzają się z `[kontrakty.mnoznik]` (naprawa 1.2, dostawa 1.0, transport 1.1,
  poszukiwania 0.9). Rozkład na tak małej próbce nierozstrzygnięty.
- [x] **I14. Wymuszony typ:** `/zf kontrakt KRW nagroda` → brain loguje `cel HEL`
  (polityka KRW/HEL -70), mod tworzy `MyContractBounty`, a w terminalu stacji widać
  zlecenie na głowę pilota HEL. Analogicznie `transport`, `naprawa`, `poszukiwania`,
  `eskorta`, `wlasne`, `dostawa`.
- [x] **I15. Zejście na dostawę:** wymuś typ, dla którego w świecie NIE MA celu
  (np. `/zf kontrakt HEL naprawa`, gdy żadna siatka HEL nie jest uszkodzona) → na czacie
  `Zlecenie HEL typu "naprawa" niemożliwe (frakcja nie ma uszkodzonej siatki do naprawy)
  — wystawiam dostawę`, a `contract_created` w konsoli braina ma `dostawa`, NIE `naprawa`.
  To najważniejszy test całej rozbudowy: żadne zlecenie nie może przepaść po cichu.
- [x] **I16. Transport potrzebuje dwóch stacji:** przy jednej stacji frakcji
  `/zf kontrakt WGR transport` → komunikat „w świecie nie ma drugiej stacji…" i dostawa.
  Postaw drugą stację z blokiem kontraktów (`/zf stacja WGR` na drugiej siatce) i powtórz
  → tym razem powstaje `MyContractHauling` z opisem `transport ładunku do <nazwa>`.
- [x] **I17. Eskorta: konwój rusza PO PRZYJĘCIU:** `/zf kontrakt HEL eskorta`
  (waga 0 w configu, więc tylko wymuszona) → kontrakt powstaje, ale w konsoli braina
  NIE MA jeszcze `spawn_request`. Dopiero gdy przyjmiesz zlecenie w terminalu →
  `kontrakt <ID> (HEL, eskorta) przyjęty przez gracza` i `spawn_request [HEL] kind=convoy`.
- [x] **I18. Własny typ (eksperymentalny):** `/zf kontrakt KRW wlasne` → albo w terminalu
  jest zlecenie „Kontrabanda Krwawej Ręki" z polskim opisem, albo na czacie leci
  `niemożliwe (brak definicji …)` / `gra odrzuciła kontrakt` i dostajemy dostawę.
  Sprawdź log SE: jeśli narzeka na `ContractTypes.sbc`, kontener/pola definicji trzeba
  poprawić wg vanilla `Content/Data/ContractTypes.sbc`. Do czasu potwierdzenia można
  ustawić `wlasne = 0` w `[kontrakty.typy]`.
  UWAGA: gdy zlecenie POWSTANIE, ale nie da się go wykonać (gra nie wie, kiedy je
  zamknąć), po `czas_min` wygaśnie jako ZAWALONE i zabierze relację (`-kontrakt_min ×
  mnożnik`). Dlatego I18 rób na świecie testowym, a nie na tym, w którym się grasz.
  To samo dotyczy `eskorta` — jeśli okaże się, że gra nie potrafi jej rozliczyć.
- [x] **I19. Mnożnik trudności:** wykonaj `nagroda` (mnożnik 1.6) → w konsoli
  `relacja KRW->gracz +32 za wykonany kontrakt (nagroda, mnożnik 1.6)`, czyli więcej
  niż +20 z dostawy. Kwota nagrody też jest przemnożona.
- [x] **I19a. Poszukiwania stawiają rekwizyt:** `/zf kontrakt WGR poszukiwania` →
  ~8 km od gracza powstaje mały grid „Zgubiony modul" (beacon ZGUBA na HUD), a kontrakt
  celuje w NIEGO, nie w stację. Sprawdź w terminalu, że zlecenie da się wykonać:
  dolatujesz, łapiesz podwoziem magnetycznym, wieziesz pod stację frakcji.
  Powtórz komendę → drugi moduł NIE powstaje, jeśli pierwszy wciąż leży dość daleko.
  CZĘŚCIOWO 2026-07-31: `kontrakt 1007452380566239508 (WGR, poszukiwania) wystawiony
  w grze` — a że kontrakt tego typu powstaje dopiero w callbacku spawnu rekwizytu
  (`SpawnProp` w `Contracts.cs`), to prefab musiał się postawić. NIEZWERYFIKOWANE:
  nazwa i beacon gridu, GPS na HUD i samo wykonanie zlecenia.

  **BŁĄD ZNALEZIONY I NAPRAWIONY 2026-07-31 (dekompilacja, nie zgadywanie).**
  Zlecenie zawalało się 1,2 s po przyjęciu (patrz I6). `ilspycmd -t
  Sandbox.Game.Contracts.MyContractFind` na `Sandbox.Game.dll` pokazał w `Update`:

  ```csharp
  case MyContractStateEnum.Active:
      MyCubeGrid grid = Grid;
      if (grid != null && !grid.IsNpcSpawnedGrid)
          Fail();
  ```

  Poszukiwania wymagają, żeby CEL miał flagę `IsNpcSpawnedGrid` — w pierwszym ticku
  po aktywacji grid bez niej jest wywalany. Nasz `SpawnProp` wołał
  `PrefabManager.SpawnPrefab(..., SpawningOptions.None, ...)`, więc rekwizyt flagi nie
  dostawał. Vanilla stawia swój rekwizyt z
  `SpawnRandomCargo | SetAuthorship | UseOnlyWorldMatrix | SetNpcSpawnedGrid`
  (`MyContractWithSpawnableGrid.SpawnPrefab`). Poprawka: `SpawningOptions.SetNpcSpawnedGrid`
  w `SpawnProp`. Flaga jest w ModAPI tylko do ODCZYTU
  (`IMyCubeGrid.IsNpcSpawnedGrid { get; }`), więc ustawić ją można wyłącznie przy spawnie —
  nie da się tego załatać po fakcie.
  Odrzucone po drodze (nie marnować na nie czasu): `cooldown_min` i `czas_min`
  (`MyContract.Update` odlicza czas TYLKO w stanie `Active`, czyli dopiero od przyjęcia —
  zlecenie wiszące na tablicy nie tyka), brak zasilania rekwizytu (`ZF_Zgubka` ma baterię
  z `ProducerEnabled`), zły `gridId` (`TryFindProp` zwraca `grid.EntityId`).
  `MyContractRepair` tego warunku NIE MA, więc `naprawa` (I19b) działała mimo braku flagi.
  DO POWTÓRZENIA W GRZE: I19a i I6 na nowym buildzie moda.
- [x] **I19b. Naprawa stawia wrak tylko w razie potrzeby:** przy nieuszkodzonych
  siatkach WGR `/zf kontrakt WGR naprawa` → 2,5 km od stacji pojawia się „Uszkodzony
  modul frakcji" (beacon AWARIA) z niepełnymi blokami i to on jest celem. Gdy jakaś
  siatka frakcji JEST już uszkodzona (np. po rajdzie) → nic się nie respi, cel to ta
  siatka. Uwaga: rekwizyt należy do frakcji, więc zniszczenie go liczy się jak
  zniszczenie jej mienia.
  Zweryfikowane 2026-07-30 na świeżym świecie (bez uszkodzonych siatek WGR):
  brak komunikatu o nieudanym spawnie (na czacie cisza — sukces jest cichy, patrz
  `SpawnProp` w `Contracts.cs`), w terminalu pojawił się punkt GPS, kontrakt dało
  się przyjąć i wykonać. Nazwa/beacon rekwizytu nie zweryfikowane wprost na czacie.
- [x] **I19c. Rekwizyt przeżywa:** postaw rekwizyt (I19a), odleć >1 km, poczekaj kilka
  minut → grid MA przetrwać sprzątacz śmieci SE (chroni go własność frakcji).
- [x] **I20. Przyjęcie zlecenia rusza świat:** przyjmij dowolne zlecenie HEL → na czacie
  `[ZF] Zlecenie HEL przyjęte (<typ>)`, w konsoli braina `przyjęty przez gracza`,
  frakcja potwierdza przez radio, a `/zf rel` pokazuje KRW niżej o 3 punkty (wróg HEL
  nie lubi, gdy pracujesz dla HEL). WGR (neutralny wobec HEL) bez zmian.
  Zweryfikowane 2026-07-30 (mechanizm identyczny, wystawcą było WGR zamiast HEL):
  po przyjęciu zlecenia WGR konsola pokazała `relacja KRW->gracz -3 za przyjęcie
  zlecenia od WGR` (KRW jest wrogiem WGR w polityce, -50).
- [ ] **I21. Kara raz na kontrakt:** po I20 zapisz i wczytaj świat, potem wykonaj
  zlecenie → NIE MA drugiego `-3` u KRW (status `taken` w bazie braina).
- [ ] **I22. Zlecenie w trakcie blokuje kolejne:** po przyjęciu zlecenia HEL odczekaj
  cooldown (20 min) i wymuś tick → HEL nie wystawia drugiego (`max_otwartych` liczy
  także zlecenia przyjęte).
## J. Trwałość i wygoda (nowe)

- [x] **J1. Auto-ścieżka storage:** usuń (albo zostaw pusty) `storage_dir`
  w `rules.local.toml`, odpal brain przy działającym świecie → konsola
  `storage wykryty automatycznie: …` ze ścieżką TEGO świata. Ręczna, istniejąca
  ścieżka nadal ma pierwszeństwo.
- [x] **J2. Rajd przeżywa restart braina:** `/zf raid KRW`, ubij `zf_brain.exe`
  (Ctrl+C), odpal ponownie, potem `/zf okup KRW` → rajd zostaje odwołany
  (`stand_down`), statki odlatują. Wcześniej brain odpowiadał „nie prowadzi rajdu".
- [x] **J3. Polityka frakcji:** `/zf rel` → po `||` widać `polityka: HEL/KRW -70 |
  HEL/WGR +10 | KRW/WGR -50`.
- [x] **J4. Wróg mojego wroga:** ostrzelaj statek KRW → w konsoli obok kary dla KRW
  jest `relacja HEL->gracz +5 (wróg KRW ostrzelany)`.
  Zweryfikowane 2026-07-31 na parze odwrotnej (ostrzał WGR): `relacja WGR->gracz -15
  za ostrzał => -24` i w tej samej paczce `relacja KRW->gracz +5 (wróg WGR ostrzelany)
  => +2`. HEL (polityka z WGR +10, powyżej `prog_wrogi`) słusznie NIC nie dostał — bonus
  idzie tylko do frakcji, która naprawdę jest wroga ostrzelanej.
- [x] **J5. Koniec echa:** napisz zwykłą wiadomość na czacie (bez `@`) → **NIE MA**
  już `[RADIO | TEST] Echo: …` (test A1 jest tym samym unieważniony).
- [x] **J6. Nowy świat = czysty stan (regresja 2026-07-29):** załóż NOWY świat z modem
  i odpal brain → konsola `baza: state/zf_state_<świat>_<hash>.sqlite3 (nowa, czyste
  relacje)`, a `[brain] relacje:` pokazuje `HEL +0 | KRW +0 | WGR +0` i okno frakcji
  w grze jest neutralne. Wróć do starego świata → jego relacje wracają (osobny plik).
  Wcześniej: jedna baza `state/zf_state.sqlite3` na wszystkie światy, więc nowy zapis
  dziedziczył wojny po poprzednim i hybryda reputacji od razu wpisywała je do gry.

## K. Okup w surowcach — B+ (nowe)

Żeby było czym płacić: `/zf daj <surowiec> [ilość]` wrzuca towar prosto do inwentarza
postaci. Nie wymaga trybu eksperymentalnego ani narzędzi kreatywnych.

- [x] **K1. Towar do ręki:** `/zf daj nikiel 700` → `ZF: dodano 700x
  MyObjectBuilder_Ingot/Nickel (w inwentarzu: 700)`, sztabki widać w plecaku.
  Warianty: `/zf daj Ore/Ice 100`, `/zf daj Component/SteelPlate 50`, samo
  `/zf daj` → podpowiedź składni, `/zf daj bzdura 5` → „nic nie weszło".
- [ ] **K2. Żądanie trybutu:** `/zf raid KRW`, potem `/zf okup-surowce KRW` →
  `[KRW] Trybut za pokój: dostarcz N …` + GPS `ZRZUT KRW`, w świecie stoi skrzynka
  z beaconem ZRZUT, statki KRW wstrzymują ogień.
- [ ] **K3. Dostawa:** `/zf daj <żądany surowiec> <żądana ilość>`, przełóż towar do
  skrzynki → `[KRW] Trybut dostarczony`, `ransom_paid` w events.jsonl, relacja +20,
  skrzynka i GPS znikają, statki odlatują.
- [ ] **K4. Deadline:** to samo bez dostawy → po `deadline_s` `[KRW] Czas na trybut
  minął`, `ransom_expired`, skrzynka i GPS znikają, ataki wracają.
- [ ] **K5. Pokój kasuje trybut (regresja 2026-07-28):** przy wiszącym żądaniu
  `/zf okup KRW` → `[KRW] Żądanie trybutu odwołane — skrzynka zrzutu znika`,
  skrzynka i GPS znikają od razu, a po upływie deadline'u NIE ma `ransom_expired`
  ani kary za złamaną obietnicę. Wcześniej skrzynka wisiała do końca okna i pokój
  kończył się karą.
- [ ] **K6. Skrzynka NIE znika sama (regresja 2026-07-28):** po `/zf okup-surowce KRW`
  skrzynka stoi ~120 m przed graczem i **zostaje** — wcześniej zjadał ją sprzątacz śmieci
  SE (świat: `TrashRemovalEnabled=true`, `BlockCountThreshold=20`, `PlayerDistanceThreshold=500`;
  2-blokowy, niestatyczny, bezpański grid dalej niż 500 m = podręcznikowy śmieć). Teraz
  prefab jest statyczny i ma baterię, więc beacon `ZRZUT` świeci i grid jest nietykalny.
  Kontrtest: gdyby mimo to przepadła, ma przyjść `[KRW] Skrzynka zrzutu przepadła —
  żądanie trybutu anulowane (bez kary)` i BRAK kary w konsoli braina.
- [ ] **K7. Frakcja wie, ile zostało czasu:** przy wiszącym żądaniu napisz
  `@krw ile mi zostało czasu?` → odpowiedź podaje realną liczbę minut i ilość surowca
  (brain wstrzykuje to do promptu). Wcześniej model zmyślał.
- [ ] **K8. Sierota po wczytaniu świata:** przy wiszącym żądaniu zapisz i wczytaj świat
  → ~3 s po wczytaniu skrzynka i GPS znikają (`sprzątnięto porzucone skrzynki
  zrzutu: 1`). Pending nie przeżywa reloadu (zakres v1), więc do tej skrzynki i tak
  nie dałoby się już dostarczyć trybutu.

## L. Targ o okup i reputacja (2026-07-28)

- [ ] **L1. Kredyty kończą rajd:** w trakcie rajdu napisz `@krw dam ci 5000 kredytów`
  (musisz je MIEĆ na koncie) → konsola braina: `okup kredytowy KRW: oferta 5000 kr >=
  próg … — pokój niezależnie od decyzji modelu`, leci `stand_down`, kasa schodzi z konta.
  Wcześniej model potrafił w kółko odpowiadać „dawaj więcej" i nigdy nie odpuścić.
- [ ] **L2. Pusta obietnica nie kupuje pokoju:** to samo z kwotą większą niż saldo →
  `oferta … bez pokrycia (saldo …) — pusta obietnica`, rajd trwa.
- [ ] **L3. Próg rośnie z wrogością:** przy relacji -80 próg jest ~1,8x bazy
  (`deeskalacja_prog_kredyty` w rules.toml, hot-reload).
- [x] **L4. Koniec farmienia reputacji:** ostrzeliwuj jeden statek KRW przez minutę →
  HEL/WGR dostają bonus „wróg KRW ostrzelany" **raz**, nie co 3 s
  (`atak_na_wroga_cooldown_min = 10`). Regresja: wcześniej minuta ostrzału robiła
  z gracza sojusznika wszystkich pozostałych frakcji.
  Zweryfikowane 2026-07-31 na WGR: **osiem** paczek `combat_hit` (WGR z -9 do -100)
  i przy nich dokładnie **jeden** `relacja KRW->gracz +5 (wróg WGR ostrzelany)` —
  przy pierwszym trafieniu. Cooldown 10 min trzyma.
- [ ] **L5. Radio się nie zapętla:** dłuższy targ na czacie (kilka wiadomości pod rząd) →
  frakcja nie powtarza w kółko tego samego zdania (kara za powtórzenia w samplerze).

## M. Reputacja natywna — hybryda (2026-07-29) — DO WERYFIKACJI

Cel: to, co liczy brain, ma być widoczne w ZWYKŁYM oknie frakcji (F1 → Frakcje /
terminal → Frakcje), a nie tylko w `/zf rel`. Nowa komenda: `/zf rep` pokazuje
wartość, którą gra ma naprawdę, i cel przysłany przez brain (przy rozjeździe krzyczy
`ROZJAZD`). Świat testowy musi być NOWY (frakcje `IsDefault` powstają przy generowaniu).

- [x] **M1. Start świata:** wejdź do świata z działającym brainem → konsola braina
  wypisuje `reputation_sync [HEL->gracz] …`, `[KRW->gracz] …`, `[WGR->gracz] …` oraz
  pary polityki. `/zf rep` pokazuje 6 linii bez słowa `ROZJAZD`.
- [x] **M2. Zgodność z oknem frakcji:** otwórz listę frakcji w grze → HEL/WGR neutralni,
  KRW wrogo (polityka HEL/KRW -70 i KRW/WGR -50 są przepisane na skalę gry).
- [x] **M3. Strzelanina zmienia liczbę w grze:** ostrzelaj statek HEL do relacji poniżej
  -30 (`/zf rel`) → w oknie frakcji HEL robi się WRÓG, `/zf rep` pokazuje ≤ -500.
  To jest sedno zmiany: wcześniej brain ogłaszał wojnę, a gra dalej miała neutralność.
- [ ] **M4. Powrót:** wykonaj kontrakt tej frakcji (albo `/zf event` z `contract_done`)
  → relacja rośnie, reputacja w grze rośnie razem z nią.
- [x] **M5. Brak podwójnego liczenia:** po nagrodzie reputacyjnej z kontraktu vanilla
  `/zf rep` w ciągu ~5 s wraca do wartości z brainu (mod przywraca cel). Krótki
  `ROZJAZD` zaraz po rozliczeniu kontraktu jest OK, utrzymujący się — nie.
- [x] **M6. Wyłącznik:** `sync = false` w `[reputacja]` (hot-reload) → brain przestaje
  wysyłać, gra zostaje na ostatniej wartości; po `sync = true` leci pełny resync.
- [x] **M7. Nic nie psuje ekonomii:** reputacja frakcji vanilla (RTSL/UNIV itd.) w oknie
  frakcji nie zmienia się przez nasz mod — synchronizujemy tylko HEL/KRW/WGR.

## N. Cennik sklepów — price_update (2026-07-31) — DO WERYFIKACJI

Cała sekcja jest nowa i **nic nie jest odhaczone**. Potrzebna stacja ze sklepem należąca
do naszej frakcji — najprościej `/zf stacja <frakcja>` na siatce z blokiem sklepu
(patrz sekcja I). Kontrola przez `/zf ceny` i przez terminal sklepu.

Kod opiera się na MODOWYM `Sandbox.ModAPI.IMyStoreBlock.GetStoreItems` i zapisywalnym
`VRage.Game.ModAPI.IMyStoreItem.PricePerUnit`/`Amount`. Sygnatury wzięte z dokumentacji
ModAPI, ale — w odróżnieniu od kontraktów — NIE zostały potwierdzone dekompilacją, więc
pierwszy test rozstrzyga, czy w ogóle mamy dostęp do ofert.

- [ ] **N1. Sklep w zasięgu:** `/zf ceny` → dla frakcji ze stacją ma być `ofert: N` (N>0).
  `BRAK bloku sklepu frakcji` = mod nie widzi sklepu; sprawdź `/zf stations`.
- [ ] **N2. Neutralna relacja nie rusza cen:** na świeżym świecie (relacja 0) spisz kilka
  cen w terminalu sklepu → `/zf ceny` pokazuje `x1.00`, ceny w terminalu BEZ ZMIAN.
  To jest sedno węzła w zerze — bez tego nie ma z czym porównać zniżki ani kary.
- [ ] **N3. Wrogość podnosi ceny:** ostrzelaj statek tej frakcji do relacji ok. -50
  (`/zf rel`) → w konsoli braina `price_update [TAG] relacja -50 => ceny x1.3`,
  a ceny w terminalu rosną mniej więcej o tyle.
  POŁOWA ZROBIONA 2026-07-31: strona braina zgadza się co do kropki — ostrzał WGR dał
  ciąg `price_update [WGR]` -9 → x1.054, -24 → x1.144, -39 → x1.234, -54 → **x1.324**,
  -62 → x1.374 (dokładnie liniowo, `mnoznik_wrog = 1.6`, węzeł 1.00 w zerze).
  BRAKUJE potwierdzenia po stronie gry: czy `PricePerUnit` w terminalu sklepu naprawdę
  urosło. To wciąż otwarte pytanie całej sekcji N (sygnatury `IMyStoreBlock` nie były
  potwierdzone dekompilacją) — bez `/zf ceny` z `ofert: N > 0` mnożnik może lecieć
  w próżnię.
- [ ] **N4. Sojusz obniża:** wykonaj kilka zleceń tej frakcji do relacji dodatniej →
  mnożnik poniżej 1.00 i tańszy towar w terminalu.
- [ ] **N5. Mnożniki się NIE składają:** przy relacji -50 spisz cenę, zapisz i wczytaj
  świat, poczekaj ~30 s → cena ma być TA SAMA. Jeśli urosła drugi raz, baza cen nie
  wróciła ze storage (`prices_mod_state.txt`) — to najgroźniejszy błąd tej mechaniki.
- [ ] **N6. Powrót do bazy:** doprowadź relację z powrotem do ~0 → ceny wracają do
  wartości spisanych w N2 (a nie „gdzieś w pobliżu").
- [ ] **N7. Embargo:** zejdź poniżej `prog_embarga` (-70) → na czacie `… wstrzymuje handel`,
  `/zf ceny` pokazuje `EMBARGO`, a w terminalu sklepu nie da się nic kupić (ilości 0).
  POŁOWA ZROBIONA 2026-07-31: brain przełączył się na progu co do punktu — WGR przy
  -62.3 jeszcze `x1.374` bez dopisku, przy -74.4 już
  `price_update [WGR] … x1.4461 (EMBARGO — frakcja nie handluje)` i tak do -100.
  BRAKUJE strony gry: komunikatu na czacie, `/zf ceny` i zerowych ilości w terminalu.
- [ ] **N8. Embargo się cofa:** odbuduj relację powyżej progu → `… znów z tobą handluje`
  i towar wraca w TEJ SAMEJ ilości, jaka była w chwili embarga (nie w bazowej).
- [ ] **N9. Embargo przeżywa wczytanie świata:** przy wiszącym embargu zapisz i wczytaj
  świat → sklep dalej pusty, a po odbudowaniu relacji ilości wracają poprawnie.
- [ ] **N10. Odnowiony asortyment:** poczekaj, aż stacja NPC odświeży oferty (albo wymuś
  to grą) → po ≤30 s nowe oferty też mają nasz mnożnik, nie ceny z gry.
- [ ] **N11. Wyłącznik:** `sync = false` w `[ceny]` (hot-reload) → brain przestaje wysyłać,
  ceny zostają na ostatniej wartości; po `sync = true` leci pełny resync.
- [ ] **N12. Nie ruszamy cudzego:** ceny na stacjach vanilla/MES (RTSL, SPRT…) bez zmian —
  synchronizujemy wyłącznie sklepy HEL/KRW/WGR.
- [ ] **N13. Handel dalej się liczy:** kup coś w sklepie frakcji po zmianie cen →
  `trade` w konsoli braina i relacja rośnie (cennik nie może psuć heurystyki handlu).

## O. Floty per frakcja, pilot i konwoje (2026-08-01) — DO WERYFIKACJI

Cała sekcja jest nowa i **nic nie jest odhaczone**. Dotyczy trzech zmian naraz: 9 grup
`ZF_<Rodzaj>_<TAG>`, manipulacji `ZF_ManipulacjaGrupa_Pilot` (kostka pancerza →
`RivalAIRemoteControlLarge`) i konwojów na zachowaniu `ZF_Konwoj`.

Spójność nazw między SBC a kodem pilnuje `tools/waliduj_sbc.py` — jeśli jest zielony,
a statek się nie pojawia, problem jest po stronie GRY (brak prefabu w vanilli, safety
check MES, planeta), nie literówki. Wszystko testuj w KOSMOSIE.

- [ ] **O1. Każda frakcja ma własną sylwetkę:** `/zf raid HEL`, `/zf raid KRW`,
  `/zf raid WGR` → trzy RÓŻNE kadłuby (HEL wojskowy, KRW piracki, WGR górniczy), nie
  trzy razy ten sam dron. Komunikat mówi, która grupa poszła (`ZF_Raid_<TAG>`).
  Automat: `/zf autotest floty` sprawdza, że statek staje i ma pilota — sylwetkę oceniasz okiem.
- [ ] **O2. Prefaby vanilli istnieją:** żaden spawn nie kończy się „prefab nie powstał".
  Podejrzane nazwy: `C33_Military_Enforcer`, `C22_Trade_Merchant`, `C40_Pirate_Vulture`,
  `C42_Pirate_SalvageCarrier`, `C12_Mining_Armed_Tender`, `C10_Mining_Carriage` — wzięte
  ze skanu prefabów, nie potwierdzone uruchomieniem.
- [ ] **O3. Pilot w dużym kadłubie:** duży rajdowy statek (HEL/KRW) **leci na gracza**,
  a nie dryfuje po prostej. To sprawdza, czy manipulacja podmieniła kostkę pancerza na
  `RivalAIRemoteControlLarge`. `/zf autotest floty` mówi tylko, czy blok JEST — czy
  RivalAI go używa, widać dopiero po zachowaniu statku.
- [ ] **O4. Manipulacja nie trafia w małą siatkę:** patrole (małe drony) mają swoje
  zdalne sterowanie i NIE dostają dodatkowego bloku; żadna grupa patrolowa nie ma
  `[ManipulationGroups]`. Objaw błędu: blok o złym rozmiarze wtopiony w kadłub.
- [ ] **O5. Konwój jedzie trasą:** `/zf raid WGR convoy` → frachtowiec **leci trasą
  i po niej znika**, zamiast dryfować po prostej z nadaną prędkością. To był powód
  podpięcia `BehaviorName:CargoShip` + gotowego autopilota MES.
- [ ] **O6. Konwój nie atakuje:** statek z O5 nie strzela do gracza bez powodu (to
  transport, nie napastnik).
- [ ] **O7. Grupy zapasowe dla obcych tagów:** `/zf raid SPRT` → brain odrzuca spawn
  („frakcja spoza moda"); grupy `ZF_Patrol`/`ZF_Raid`/`ZF_Convoy` bez tagu zostają
  wyłącznie jako zapas i nie są używane dla HEL/KRW/WGR.

## P. Załogi NPC — AiEnabled (2026-08-01) — DO WERYFIKACJI

Zależność MIĘKKA: bez moda AiEnabled (Workshop 2596208372) nikt się nie pojawia i to
NIE jest błąd — reszta ma działać bez zmian. Dwie drogi: deklaratywna dla statków
(`ZF_Boty.sbc` przez MES) i programowa dla stacji (`Crew.cs` przez API).

**Najbardziej podejrzane miejsce:** wartości `[BotType]` (`Police_Bot`, `Space_Skeleton`)
i `[BotBehavior]` (`Soldier`, `Grinder`) wzięto z opisu moda na Workshopie, a nie z jego
plików. Wiki MES mówi wprost, że `BotType` to pole `Name` z SBC, a NIE SubtypeId — jeśli
boty się nie pojawiają, zacznij od tego.

- [ ] **P1. Załoga na stacji:** dolec bliżej niż 3 km do stacji frakcji → po ~1 min po
  pokładzie chodzą postacie NPC. Automat: `/zf autotest boty` (OSTRZEŻENIE bez AiEnabled).
- [ ] **P2. Załoga na statku rajdowym:** `/zf raid KRW` w kosmosie, podleć bliżej niż
  1,5 km (trigger `PlayerNear`) → na pokładzie pojawia się załoga. Boty NIE chodzą po
  małych siatkach, więc to działa tylko dla dużych kadłubów rajdowych.
- [ ] **P3. Bot należy do frakcji:** postać z P1/P2 jest członkiem HEL/KRW/WGR (MES woła
  `SetPlayersFaction`), a nie bezpańskim NPC.
- [ ] **P4. SEDNO HYBRYDY — bot reaguje na relację:** przy dobrej relacji bot NIE atakuje,
  po doprowadzeniu relacji poniżej `prog_wrogi` (`/zf rel`) **ten sam bot** zaczyna
  strzelać, BEZ respawnu. Wrogość bota leci po natywnej reputacji, czyli po naszej hybrydzie.
- [ ] **P5. Łupieżca KRW rozbiera, a nie strzela:** wśród załogi KRW jest bot
  (`ZF_Bot_KRW_Lupiezca`, zachowanie `Grinder`), który bierze się za twój kadłub szlifierką.
- [ ] **P6. Załoga się nie mnoży:** wyjdź i wróć w pobliże stacji kilka razy → liczba
  postaci nie rośnie (idempotencja przez liczenie postaci, nie przez zapis w storage).
- [ ] **P7. Bez AiEnabled nic się nie psuje:** wyłącz AiEnabled → świat wstaje, jest jedno
  ostrzeżenie na sesję, a stacje, ceny i kontrakty działają jak dotąd.

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
