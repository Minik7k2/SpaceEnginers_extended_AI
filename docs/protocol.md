# Protokół mostka plikowego (v1)

Katalog: storage moda (MyAPIGateway.Utilities, per-world). Brain dostaje ścieżkę w configu.
Format: JSON Lines, UTF-8, jedna linia = jedna wiadomość. Append-only.
Wspólne pola: `v` (int, wersja=1), `seq` (rosnący licznik nadawcy), `ts` (unix ms), `type`, `data`.
Odbiorca pomija linie z nieznanym `type` oraz `v` > obsługiwana. Linia bez `\n` = niedokończona, czekaj.
Rotacja: nadawca po 5 MB zaczyna `events-NNNN.jsonl` / `commands-NNNN.jsonl`; odbiorca kasuje w pełni przetworzone stare pliki. Offsety: brain w SQLite (bridge_state), mod w swoim storage.

## events.jsonl (mod → brain)

session_start  {"world":"nazwa","player_id":123,"player_name":"Minik","mod_version":"0.1"}
heartbeat      {"pos":[x,y,z],"speed":m_s}                                  co 10 s
chat_message   {"text":"...","target":"KRW"|null,"in_range":["WGR"],"signal":"clear"|"weak"|"none","balans":4500}   target=@frakcja; signal=łączność do adresata (5c); balans=saldo gracza (pole opcjonalne, brak = nieznane)
proximity      {"faction":"WGR","state":"enter"|"exit","dist":2900}         enter<3000m, exit>4000m
combat_hit     {"attacker":123|null,"faction":"KRW","damage":450.5,"hits":37,"weapon":"gatling"}  agregat 3 s
grid_destroyed {"faction":"KRW","grid":"nazwa","by_player":true}
trade          {"faction":"HEL","kind":"buy"|"sell","value":1500}           heurystyka: zmiana salda + sklep frakcji <300 m
contract_created {"contract_id":"123","faction":"WGR","kind":"dostawa","reward":50000,"reward_str":"50000","opis":"dostawa 600 płyt stalowych","target":""}  kontrakt naprawdę powstał w grze; ID -> SQLite. `kind` = typ, który POWSTAŁ (mod mógł zejść na dostawę — patrz „Typy kontraktów"). `target` niepuste tylko dla nagrody za głowę
contract_taken {"contract_id":"123","faction":"WGR","kind":"naprawa"}          gracz PRZYJĄŁ zlecenie w terminalu (OnContractAcquired) — moment reakcji świata, patrz niżej
contract_done  {"contract_id":"...","faction":"WGR","success":true}        faction opcjonalne — brain zna je z ID
ransom_paid    {"faction":"KRW","item":"Iron","amount":500}                  B+ gracz dostarczył trybut do skrzynki zrzutu w oknie — pokój + relacja
ransom_expired {"faction":"KRW","reason":"deadline"|"brak_skrzynki"}         B+ koniec żądania bez dostawy. deadline = gracz nie zdążył (kara + trwała nieufność); brak_skrzynki = skrzynka przepadła (sprzątacz śmieci SE) i brain kasuje żądanie BEZ kary
debug_command  {"cmd":"rel"|"tick"} | {"cmd":"spawn"|"okup"|"okup-surowce"|"kontrakt","faction":"KRW"}   /zf rel, /zf tick, /zf raid, /zf okup, /zf okup-surowce, /zf kontrakt. Dla `kontrakt` dodatkowo opcjonalne `"kind":"nagroda"` (/zf kontrakt KRW nagroda) — bez niego brain losuje typ wagami

## commands.jsonl (brain → mod)

radio_message   {"faction":"KRW","text":"...","color":"red","priority":1}   [RADIO | NAZWA], TTL 2 min
spawn_request   {"faction":"KRW","kind":"patrol"|"raid"|"convoy","near_player":true,"context":"incydent#123"}
stand_down      {"faction":"KRW","ransom":4000}   frakcja odpuściła — statki rajdu odlatują; ransom>0 = mod pobiera tyle kredytów gracz→frakcja (Etap 6)
ransom_demand   {"faction":"KRW","item":"Iron","amount":500,"deadline_s":900}   B+ frakcja żąda trybutu: mod stawia skrzynkę zrzutu (owner=0, GPS), wstrzymuje ogień, pilnuje deadline; dostawa→ransom_paid, brak→ransom_expired
reputation_sync {"faction":"KRW","other":"","value":-72.0,"vanilla":-1050}   HYBRYDA: nasza relacja przepisana na natywną reputację SE. other="" = relacja frakcja→gracz (SetReputationBetweenPlayerAndFaction), other="WGR" = polityka frakcja↔frakcja (SetReputation, symetryczna). Do zapisu służy `vanilla`; `value` (nasza skala) jest tylko do logów/`/zf rep`
price_update    {"faction":"HEL","modifier":1.5,"embargo":false,"value":-42.0}   cennik sklepu frakcji przepisany z relacji. Mod mnoży przez `modifier` CENY BAZOWE ofert (bazę pamięta u siebie), `embargo`=true zamyka handel; `value` (nasza skala) tylko do logów/`/zf ceny`
contract_create {"faction":"WGR","kind":"dostawa","reward":50000,"duration_min":45,"target_faction":""}  mod stawia kontrakt na bloku frakcji i odsyła contract_created. `target_faction` ma znaczenie TYLKO dla kind="nagroda"

## Typy kontraktów — brain proponuje, gra rozstrzyga

Każdy typ to inna klasa z `Sandbox.ModAPI.Contracts` z własnym konstruktorem i własnym
CELEM, którego mod musi poszukać w świecie:

| `kind` | klasa ModAPI | cel, którego szuka mod |
|---|---|---|
| `dostawa` | `MyContractAcquisition` | towar + blok frakcji (zawsze wykonalne) |
| `nagroda` | `MyContractBounty` | `identity` właściciela siatki frakcji z `target_faction` |
| `transport` | `MyContractHauling` | drugi blok kontraktów/sklepu, na innej siatce |
| `naprawa` | `MyContractRepair` | siatka frakcji z niepełnymi blokami, a gdy brak — postawiony wrak |
| `poszukiwania` | `MyContractSearch` | zgubiony moduł frakcji dalej niż 5 km od gracza (stawiany, patrz „Rekwizyty") |
| `wlasne` | `MyContractCustom` | definicja `ZF_Zlecenie` z `mod/Data/ContractTypes.sbc` |

- Typ wybiera brain wagami z `[kontrakty.typy]` (nadpisania per frakcja), ale **mod ma
  ostatnie słowo**: gdy celu nie ma w świecie albo `AddContract` zwróci `Success=false`,
  wystawia DOSTAWĘ i to ona wraca w `contract_created`. Brain utrwala typ, który
  naprawdę powstał — od niego zależy mnożnik nagrody i relacji (`[kontrakty.mnoznik]`).
  Powód zejścia na dostawę leci na czat, żeby nie trzeba było zgadywać.
- `nagroda` nie wchodzi nawet do losowania, gdy wystawca z nikim nie jest poniżej
  `prog_wrogi` — nagroda za głowę bez wroga nie ma celu.
- Typu `eskorta` **nie ma** (usunięty 2026-08-09): gra nie wozi definicji
  `ContractTypeEscort`, więc `AddContract` zwracało `Error` przy każdej próbie — i to bez
  wpisu do logu. Nie da się go przywrócić samą wagą; wpis `eskorta` w `[kontrakty.typy]`
  wywali config brainu z komunikatem „nieznany typ kontraktu".
- `wlasne` to jedyny typ nieprzewidziany wprost w dokumentacji API: wymaga definicji
  `MyObjectBuilder_ContractTypeDefinition`, a zgłoszone bugi Keena mówią, że kontrakty
  custom nie ruszają reputacji vanilla (u nas nieszkodliwe — reputację prowadzi brain)
  i mogą źle pokazywać nazwę typu w UI. Wyłącznik: `wlasne = 0` w `[kontrakty.typy]`.
- `reputationReward`/`failReputationPrice` dla `wlasne` są ZEROWE celowo — reputacją
  rządzi nasz silnik relacji (patrz „Reputacja — kto tu rządzi" niżej).

### Rekwizyty: frakcja przygotowuje sobie robotę

`poszukiwania` i `naprawa` nie czekają już, aż w świecie przypadkiem stanie coś
nadającego się na cel — mod STAWIA rekwizyt z `mod/Data/Prefabs/ZF_ContractProps.sbc`
i dopiero wtedy tworzy kontrakt (SpawnPrefab jest asynchroniczny, więc kontrakt
powstaje w jego callbacku):

- `ZF_Zgubka` — „Zgubiony modul", NIESTATYCZNY (vanillowe poszukiwania każą przywieźć
  znaleziony grid pod stację, więc musi dać się złapać podwoziem), z beaconem. Stawiany
  8 km od gracza. Celem poszukiwań jest wyłącznie taki moduł: stacji nikt nie przywiezie.
  Kolejne zlecenia REUŻYWAJĄ modułu zgubionego wcześniej, jeśli leży dość daleko.
- `ZF_Wrak` — „Uszkodzony modul frakcji", statyczny, z blokami o obniżonej integralności
  (prosto z prefabu — nie psujemy niczego w locie). Stawiany 2,5 km od stacji frakcji
  TYLKO wtedy, gdy frakcja nie ma już czego naprawiać.

Rekwizyt dostaje właściciela = frakcja wystawiająca. To i fabuła (jej zguba, jej awaria),
i ochrona przed sprzątaczem śmieci SE, który zjada bezpańskie małe gridy z dala od gracza.

### Przyjęcie zlecenia: kiedy świat reaguje

`contract_taken` to jedyny moment, w którym wiadomo, że gracz naprawdę wziął robotę:

- konwój do `eskorty` wyrusza DOPIERO teraz (wcześniej statki krążyły przy każdym
  zleceniu, którego gracz nawet nie zobaczył),
- cel nagrody za głowę dostaje patrol ochronny (`target` z payloadu kontraktu),
- każda frakcja WROGA wystawcy (relacja <= `prog_wrogi`) traci do gracza
  `kontrakt_przyjety_u_wroga` punktów — przyjęcie zlecenia to opowiedzenie się po
  czyjejś stronie. Kara jest jednorazowa: status kontraktu w SQLite przechodzi na
  `taken`, a powtórzony callback jest ignorowany.

Status `taken` liczy się do `max_otwartych` tak samo jak `open` — zlecenie w trakcie
wciąż blokuje frakcji wystawienie następnego.

## Reputacja — kto tu rządzi

- Źródłem prawdy jest silnik relacji brainu (-100..+100, sufity, histereza, pamięć).
  Natywna reputacja SE (-1500..+1500) jest tylko JEGO RZUTEM: brain liczy, mod zapisuje.
- Odwzorowanie jest odcinkowo-liniowe i celuje w progi gry (±500): nasz `prog_wrogi`
  ląduje tuż poniżej -500 (w grze „wróg"), `prog_sojusznik` tuż powyżej +500
  („sojusznik"), 0 → 0, ±100 → ±1500. Wartości w `[reputacja]` w rules.toml.
- Kierunek jest jednostronny. Mod co ~5 s sprawdza, czy gra nie zmieniła reputacji po
  swojemu (nagrody z kontraktów vanilla) i przywraca wartość z brainu — inaczej to samo
  zdarzenie liczyłoby się dwa razy, a obie liczby znowu by się rozjechały.
- Synchronizowane są WYŁĄCZNIE nasze frakcje (HEL/KRW/WGR). Reputacja frakcji vanilla/MES
  (RTSL, SPRT...) należy do gry.
- Po `session_start` brain wysyła komplet wartości (mod nie utrwala ich między sesjami).

## Cennik — kto tu rządzi

Tak jak przy reputacji: liczy brain, zapisuje mod. Relacja frakcja→gracz idzie przez
odcinkowo liniowe odwzorowanie z twardym węzłem w zerze (`[ceny]` w rules.toml):
-100 → `mnoznik_wrog`, **0 → dokładnie 1.0**, +100 → `mnoznik_sojusznik`. Węzeł w zerze
jest twardy celowo — przy neutralnej relacji cennik ma zostać taki, jaki wygenerowała gra,
inaczej gracz nie ma z czym porównać późniejszej zniżki ani kary.

- Mod przepisuje `PricePerUnit` ofertom na blokach sklepu należących do frakcji.
  Używa MODOWEGO `Sandbox.ModAPI.IMyStoreBlock` (`GetStoreItems` — wszystkie oferty
  bloku), nie tego z `Ingame` (tylko Insert/Cancel/GetPlayerStoreItems).
  `VRage.Game.ModAPI.IMyStoreItem.PricePerUnit` i `Amount` są ZAPISYWALNE, więc ofert
  nie trzeba anulować i wstawiać od nowa.
- Mnożnik liczy się zawsze od CENY BAZOWEJ, nigdy od bieżącej. Ceny siedzą w zapisie
  świata, więc bez pamiętanej bazy mnożniki składałyby się przy każdym wczytaniu
  (1.6 → 2.56 → 4.1…). Baza idzie do `prices_mod_state.txt` w storage moda.
- `embargo` (relacja ≤ `prog_embarga`) zeruje `Amount` ofert zamiast je kasować.
  Mod zapamiętuje stan magazynu Z CHWILI EMBARGA i wraca dokładnie do niego — odtwarzanie
  ilości bazowej pozwoliłoby wykupić stację tuż przed embargiem i odzyskać towar za darmo.
- Powtarzalne: stacje NPC same odnawiają asortyment (nowe oferty mają ceny z gry), więc
  mod nakłada cennik ponownie co ~30 s, a nie raz.
- Synchronizowane są WYŁĄCZNIE nasze frakcje (HEL/KRW/WGR), tak jak przy reputacji.
- Po `session_start` brain wysyła komplet cenników (mod nie utrwala mnożników, tylko bazy).

## Ekonomia — czego mostek NIE gwarantuje

- `trade` to HEURYSTYKA, nie zdarzenie z gry: mod porównuje saldo gracza co 2 s
  i przypisuje zmianę do frakcji, jeśli jej sklep jest bliżej niż 300 m. Własne przelewy
  moda (okup, nagroda za kontrakt) są wyciszane na ~10 s, żeby nie liczyły się podwójnie.
  (Trop na przyszłość: `IMyStoreItem` ma zdarzenie `OnTransaction` —
  `Action<int,int,long,long,long>`. Semantyka parametrów nieudokumentowana, ale to
  potencjalne zastąpienie heurystyki prawdziwym zdarzeniem transakcji.)
- Kontrakt powstaje tylko wtedy, gdy frakcja MA w świecie blok kontraktów albo sklep
  (to jego EntityId trafia do `MyContractAcquisition` jako startBlockId). Brak takiego
  bloku = komunikat na czacie i pominięte zlecenie; sprawdzisz to komendą `/zf stations`.
- Callbacki kontraktu (OnContractSucceeded/Failed) nie przeżywają wczytania świata,
  dlatego mod trzyma ID w `contracts_mod_state.txt` i dodatkowo odpytuje stan co ~5 s.
