# Zlecenia własne: `custom` jako SZABLON, nie jako rodzaj roboty

Projekt (2026-08-09). **Plan, nie opis stanu** — nic z Etapów 1–3 tego dokumentu jeszcze
nie istnieje w kodzie. Stan bieżący: `wlasne` stoi w `[kontrakty.typy]` z wagą 0.

## Skąd ten dokument

`wlasne` trafiło do tabeli wag obok `dostawy` i `naprawy`, jakby było rodzajem zadania.
Nie jest. `MyContractCustom` to **sposób budowania** zlecenia — jedyny, w którym warunek
zwycięstwa piszemy my, zamiast prosić grę o jeden z jej ośmiu gotowych.

Skutek pomyłki widać w kodzie: dzisiejsze `wlasne` dostaje `startBlockId` + `endBlockId`
z `FindHaulTarget`, ma `StrategyType = Hauling` w SBC i różni się od `transportu`
wyłącznie tekstem w terminalu. Czyli jest transportem, którego **nie da się ukończyć**,
bo `MyContractConditionCustom` zamyka wyłącznie mod przez `TryFinishCustomContract(id)`,
czego nie robimy. Ściśle gorszy od typu, który już mamy.

To ten sam kształt błędu co eskorta: wysłaliśmy połowę prezentacyjną (SBC, teksty PL)
bez połowy mechanicznej, a różnicę zasłonił komentarz w configu.

---

## Wszystkie typy zleceń — pełna lista

### 1. Co wozi GRA (osiem definicji w `Content/Data`)

Spisane dekompilacją 2026-08-05 przy okazji eskorty. To jest sufit tego, co da się
dostać „za darmo" — warunek liczy gra.

| Definicja gry | Czy używamy | Uwaga |
|---|---|---|
| `ObtainAndDeliver` | ✅ jako `dostawa` | zdobądź towar, przywieź na blok |
| `GridHauling` | ✅ jako `transport` | ładunek między dwoma blokami |
| `Repair` | ✅ jako `naprawa` | napraw uszkodzoną siatkę |
| `Find` | ✅ jako `poszukiwania` | znajdź siatkę w promieniu |
| `PvEBounty` | ⚠️ jako `nagroda` (waga 0) | patrz niżej — liczy zabicia GRACZY |
| `Hunt` | ❌ | polowanie na zwierzynę; nie pasuje do frakcji |
| `Deliver` | ❌ | podzbiór `ObtainAndDeliver` |
| `Salvage` | ❌ | kandydat do rozważenia (złomowanie wraku) |
| ~~`Escort`~~ | ❌ NIE ISTNIEJE | usunięta z gry; nasz typ wycięty 2026-08-09 |

### 2. Klasy z `Sandbox.ModAPI.Contracts`, które mod instancjuje

`MyContractAcquisition`, `MyContractHauling`, `MyContractRepair`, `MyContractSearch`,
`MyContractBounty`, `MyContractCustom`.
(`MyContractFind`, `MyContractWithSpawnableGrid`, `MyContractGenerator` pojawiają się
tylko w komentarzach — to wewnętrzne klasy gry z dekompilacji, nie ModAPI.)

### 3. Nasze rodzaje DZIŚ (`Config::contract_kinds()`)

| `kind` | klasa | waga | stan |
|---|---|---|---|
| `dostawa` | `MyContractAcquisition` | 3 | działa; jest też fallbackiem dla wszystkich |
| `transport` | `MyContractHauling` | 2 | działa |
| `naprawa` | `MyContractRepair` | 1 | działa (stawia wrak, gdy brak celu) |
| `poszukiwania` | `MyContractSearch` | 1 | działa (stawia zgubkę) |
| `nagroda` | `MyContractBounty` | **0** | dług: vanilla liczy zabicia GRACZY |
| `wlasne` | `MyContractCustom` | **0** | dług: brak warunku wykonania |

### 4. Nasze rodzaje PO przebudowie

`wlasne` znika jako rodzaj i staje się szablonem, na którym stoi cała druga rodzina.

**Rodzina A — oparte o vanillę** (warunek liczy gra):

| `kind` | klasa ModAPI |
|---|---|
| `dostawa` | `MyContractAcquisition` |
| `transport` | `MyContractHauling` |
| `naprawa` | `MyContractRepair` |
| `poszukiwania` | `MyContractSearch` |

**Rodzina B — oparte o `MyContractCustom`** (warunek liczy mod):

| `kind` | co robi gracz | detekcja, którą JUŻ mamy |
|---|---|---|
| `polowanie` | zniszcz siatkę wskazanej wrogiej frakcji | `grid_destroyed` z atrybucją do gracza (Etap 2) |
| `trybut` | dostarcz N surowca do skrzyni frakcji | skrzynka + GPS + kontrola inwentarza (`RansomManager`) |
| `konwoj` | doprowadź konwój frakcji do celu | spawn MES + dystans do punktu |
| `pakt` | nie atakuj frakcji X przez N minut | brak `combat_hit` na X (`CombatTracker`) |

Żadnego z tych czterech nie da się wyrazić typem vanilli. **To jest odpowiedź na pytanie
„po co nam custom".**

### 5. Konsekwencja: `nagroda` prawdopodobnie odpada

`polowanie` to ta sama fantazja („zabij dla nas"), tylko licząca to, co naprawdę dzieje
się w single playerze. Jeśli I14 potwierdzi, że `MyContractBounty` liczy wyłącznie
zabicia GRACZY, to `nagroda` jest w naszym świecie **nieukończalna** — czyli dokładnie
sytuacja eskorty — i idzie do wycięcia, a `polowanie` zajmuje jej miejsce.
**Nie usuwamy jej przed potwierdzeniem w grze.**

---

## Architektura

### Rejestr rodzajów

Jedno miejsce opisujące każdy rodzaj z rodziny B. Nowy plik
`mod/Data/Scripts/ZyweFrakcje/CustomContracts.cs`.

```
RodzajCustom
  Kind         : string                      // "polowanie"
  Nazwa        : (faction) -> string         // tytuł PL w terminalu
  Opis         : (faction, params) -> string // treść PL
  Przygotuj    : (faction, out params, out powod) -> bool
  Sprawdz      : (params) -> Trwa | Sukces | Porazka
```

- **`Przygotuj`** robi to, co dziś robi `SpawnProp` dla poszukiwań i naprawy: wybiera cel
  albo stawia rekwizyt i zwraca parametry warunku. Musi zachować kształt ASYNCHRONICZNY
  (`SpawnPrefab` woła callback), bo inaczej kontrakt powstałby przed rekwizytem.
- **`Sprawdz`** jest odpytywane w PĘTLI, KTÓRA JUŻ CHODZI — `ContractManager` co ~5 s
  woła `GetContractState` dla każdego śledzonego kontraktu i zna jego `Kind`.

### Ścieżka zamknięcia — jedna, nie dwie

```
Sprawdz -> Sukces
  -> IMyContractSystem.TryFinishCustomContract(id)
  -> gra ustawia stan Finished
  -> istniejący poll GetContractState widzi Finished
  -> Finish(id, true)  [BEZ ZMIAN]
  -> contract_done do brainu
```

To jest istotne: **nie dokładamy drugiej drogi zamykania kontraktu**, tylko wyzwalacz do
tej, która już działa. Porażka po czasie w ogóle nie wymaga kodu — wygaśnięcie
`Duration` obsługuje gra i wraca jako `Failed` tą samą ścieżką.

Jedyny rodzaj, który może polec CZYNEM gracza, a nie upływem czasu, to `pakt` (strzeliłeś
do frakcji, z którą był rozejm). Tam `Sprawdz` zwraca `Porazka`, a mod musi zawalić
kontrakt jawnie — to jedyne miejsce wymagające sprawdzenia, czy ModAPI w ogóle na to
pozwala. **Dlatego `pakt` jest OSTATNI w kolejności prac.**

### Gdzie mieszkają parametry warunku

Cel, ilość, deadline, koordynaty → JSON.

- **brain**: kolumna `payload` w tabeli `contracts` — już istnieje, już jest zapisywana.
- **mod**: `Tracked` dostaje czwarte pole `Params`, a plik stanu w storage moda czwartą
  kolumnę (dziś zapisuje `id / faction / kind`).

Wymóg twardy: **musi przeżyć wczytanie świata**. Ten sam wymóg co dla ID kontraktów.

### Co się zmienia w brainie

Prawie nic — i to jest zaleta tego projektu. Dla brainu `polowanie` czy `trybut` to nadal
po prostu rodzaje z wagami i mnożnikami; to, czy stoją na klasie vanilli, czy na naszym
warunku, jest szczegółem implementacji MODA. Granica mostka zostaje tam, gdzie była.

- `Config::contract_kinds()`: `-wlasne`, `+polowanie +trybut +konwoj +pakt`
- `[kontrakty.typy]` i `[kontrakty.mnoznik]`: wpisy dla nowych rodzajów
- `polowanie` potrzebuje `target_faction` jak `nagroda` → **ta sama bramka
  `worst_enemy_of`** (bez wroga w polityce typ nie wchodzi do losowania)
- `konwoj` potrzebuje spawnu po `contract_taken` → tu wraca pomysł, który zniknął
  z eskortą (konwój wyrusza, gdy gracz weźmie robotę, nie przy wystawieniu zlecenia)

### Co się zmienia w modzie

- `Contracts.cs`: `case "wlasne"` → po jednym `case` na rodzaj rodziny B, wszystkie
  delegujące do wspólnego `AddCustomContract(rodzaj, ...)`. Switch nie rośnie liniowo.
- `CustomContracts.cs` (nowy): rejestr + warunki.
- `UpdateTracked`: ocena warunku + `TryFinishCustomContract`.
- `ContractTypes.sbc`: **zostaje JEDNA definicja `ZF_Zlecenie`** jako wspólny szablon.
  Rozbicie na definicję per rodzaj dopiero wtedy, gdy okaże się, że UI źle pokazuje nazwę
  typu — mniej niewiadomych na start.

### Co się zmienia w strażnikach długu

- `dlug_test`: wpis `wlasne` w `kBlokady` znika, wchodzą wpisy per rodzaj rodziny B,
  skreślane pojedynczo w miarę jak rodzaje lądują.
- `waliduj_sbc.py`: reguła „każdy typ z wagą > 0 ma swój `case` w `Contracts.cs`" działa
  **bez zmian** — obsłuży nowe rodzaje tak samo.
- Reguła do dopisania: każdy rodzaj w rejestrze ma niepuste `Przygotuj` i `Sprawdz`.
  Rodzaj bez warunku to dokładnie dzisiejsze `wlasne`.

---

## Kolejność prac

### Etap 0 — POTWIERDŹ FUNDAMENT (blokuje wszystko)

Cała rodzina B stoi na założeniu, że `ZF_Zlecenie` się wczytuje i `MyContractCustom`
powstaje na NASZYM bloku kontraktów. **Nikt tego nie potwierdził.** I18 w
`docs/testy-reczne.md` jest odhaczony, ale jego kryterium przechodzi w obie strony
(„albo zlecenie jest w terminalu, albo na czacie leci komunikat o odrzuceniu") — to test
bez asercji.

**Kod bramki: GOTOWY (2026-08-09), wynik: NIEZNANY do przebiegu w grze.**
`/zf autotest kontrakty` ma krok „FUNDAMENT custom", jedyny w tej sekcji TWARDY dla
zejścia na dostawę. Do 2026-08-09 to samo sprawdzenie było OSTRZEŻENIEM, więc brak
działającego custom kontraktu przechodził jako łagodna żółta linijka.

Co dowodzi: `OstatniTyp == "wlasne"` znaczy, że `AddContract` przyjął custom kontrakt,
czyli podtyp `ZF_Zlecenie` ISTNIEJE w danych gry. Gdyby definicji nie było, mod zszedłby
na dostawę.

Czego NIE dowodzi — i dlatego Etap 0 kończy dopiero przebieg z człowiekiem przy sterach:
- czy terminal pokazuje NASZ tytuł („Kontrabanda Krwawej Ręki"), czy generyczną nazwę
  typu. Krok wypisuje na czacie, czego szukać. Jeśli UI ignoruje nasz tytuł, zmienia to
  projekt: jedna definicja wspólna kontra jedna definicja na rodzaj.
- czy `TryFinishCustomContract` domyka kontrakt i wypłaca — to ryzyko Etapu 1.

Jeśli padnie: rodzina B jest niemożliwa, `wlasne` idzie do wycięcia jak eskorta, a ten
dokument zostaje w repo jako zapis „dlaczego nie".

### Etap 1 — szkielet + `polowanie`

Rejestr, wspólny builder, ocena warunku w `UpdateTracked`, utrwalanie parametrów.
Ship z JEDNYM rodzajem: `polowanie` — bo detekcja (`grid_destroyed`) już działa, nie
wymaga nowych rekwizytów i od razu daje odpowiedź, czy `TryFinishCustomContract` w ogóle
domyka kontrakt i wypłaca nagrodę.

### Etap 2 — `trybut`

Najmniej nowego kodu po Etapie 1: skrzynka zrzutu, GPS i kontrola inwentarza stoją
gotowe w `RansomManager`, dziś używane wyłącznie przez okup.

### Etap 3 — `konwoj`

Wymaga MES i przywraca spawn po `contract_taken`.

### Etap 4 — `pakt`

Ostatni, bo jako jedyny wymaga JAWNEGO zawalenia kontraktu czynem gracza — a tego, czy
ModAPI na to pozwala, jeszcze nie wiemy.

### Potem

Rozstrzygnij I14 i wytnij `nagrodę`, jeśli potwierdzi się, że liczy tylko zabicia graczy.

---

## Znane pułapki (spisane, żeby nie odkrywać ich drugi raz)

- **`EndBlockId` custom kontraktu musi być BLOKIEM KONTRAKTÓW** (`as MyContractBlock` →
  `Fail_BlockNotFound`), a `FindHaulTarget` schodzi na sklep. To osobna, udokumentowana
  przyczyna odrzuceń — dotknie każdego rodzaju rodziny B.
- **`Duration` jest w MINUTACH**, nie sekundach.
- **Kontrakt opłaca WŁAŚCICIEL BLOKU**, nie frakcja: `GenerateCustomContract` sprawdza
  `MyBankingSystem.GetBalance(startBlock.OwnerId)` i ściąga z niego nagrodę przy tworzeniu.
- **`reputationReward` / `failReputationPrice` zostają ZEROWE** — reputacją rządzi nasz
  silnik relacji (hybryda). Gdyby gra dokładała swoją, to samo zdarzenie liczyłoby się
  dwa razy.
- **`TryFinishCustomContract` jest niezweryfikowane** — nie wiemy, czy naprawdę przestawia
  stan na `Finished` i wypłaca. To główne ryzyko Etapu 1 i powód, dla którego Etap 1
  wiezie tylko jeden rodzaj.
