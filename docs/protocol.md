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
contract_created {"contract_id":"123","faction":"WGR","kind":"dostawa","reward":50000,"reward_str":"50000","opis":"dostawa 600 płyt stalowych"}  kontrakt naprawdę powstał w grze; ID -> SQLite. `kind` = typ, który POWSTAŁ (mod mógł zejść na dostawę — patrz „Typy kontraktów")
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
price_update    {"faction":"HEL","modifier":1.5}                            Etap 6 (jeszcze nieobsługiwane)
contract_create {"faction":"WGR","kind":"dostawa","reward":50000,"duration_min":45,"target_faction":""}  mod stawia kontrakt na bloku frakcji i odsyła contract_created. `target_faction` ma znaczenie TYLKO dla kind="nagroda"

## Typy kontraktów — brain proponuje, gra rozstrzyga

Każdy typ to inna klasa z `Sandbox.ModAPI.Contracts` z własnym konstruktorem i własnym
CELEM, którego mod musi poszukać w świecie:

| `kind` | klasa ModAPI | cel, którego szuka mod |
|---|---|---|
| `dostawa` | `MyContractAcquisition` | towar + blok frakcji (zawsze wykonalne) |
| `nagroda` | `MyContractBounty` | `identity` właściciela siatki frakcji z `target_faction` |
| `transport` | `MyContractHauling` | drugi blok kontraktów/sklepu, na innej siatce |
| `naprawa` | `MyContractRepair` | siatka frakcji z niepełnymi blokami |
| `poszukiwania` | `MyContractSearch` | siatka frakcji dalej niż 5 km od gracza |
| `eskorta` | `MyContractEscort` | trasa (dwa punkty) + tożsamość właściciela konwoju |
| `wlasne` | `MyContractCustom` | definicja `ZF_Zlecenie` z `mod/Data/ContractTypes.sbc` |

- Typ wybiera brain wagami z `[kontrakty.typy]` (nadpisania per frakcja), ale **mod ma
  ostatnie słowo**: gdy celu nie ma w świecie albo `AddContract` zwróci `Success=false`,
  wystawia DOSTAWĘ i to ona wraca w `contract_created`. Brain utrwala typ, który
  naprawdę powstał — od niego zależy mnożnik nagrody i relacji (`[kontrakty.mnoznik]`).
  Powód zejścia na dostawę leci na czat, żeby nie trzeba było zgadywać.
- `nagroda` nie wchodzi nawet do losowania, gdy wystawca z nikim nie jest poniżej
  `prog_wrogi` — nagroda za głowę bez wroga nie ma celu.
- `eskorta` pociąga za sobą `spawn_request` z `kind=convoy` (nie ma czego eskortować
  bez statku); szanuje `[spawn].wlaczone`, omija tylko cooldown spawnu.
- `wlasne` to jedyny typ nieprzewidziany wprost w dokumentacji API: wymaga definicji
  `MyObjectBuilder_ContractTypeDefinition`, a zgłoszone bugi Keena mówią, że kontrakty
  custom nie ruszają reputacji vanilla (u nas nieszkodliwe — reputację prowadzi brain)
  i mogą źle pokazywać nazwę typu w UI. Wyłącznik: `wlasne = 0` w `[kontrakty.typy]`.
- `reputationReward`/`failReputationPrice` dla `wlasne` są ZEROWE celowo — reputacją
  rządzi nasz silnik relacji (patrz „Reputacja — kto tu rządzi" niżej).

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

## Ekonomia — czego mostek NIE gwarantuje

- `trade` to HEURYSTYKA, nie zdarzenie z gry: ModAPI nie ma callbacku transakcji
  (IMyStoreBlock daje tylko Insert/Cancel/GetPlayerStoreItems). Mod porównuje saldo
  gracza co 2 s i przypisuje zmianę do frakcji, jeśli jej sklep jest bliżej niż 300 m.
  Własne przelewy moda (okup, nagroda za kontrakt) są wyciszane na ~10 s, żeby nie
  liczyły się podwójnie.
- Kontrakt powstaje tylko wtedy, gdy frakcja MA w świecie blok kontraktów albo sklep
  (to jego EntityId trafia do `MyContractAcquisition` jako startBlockId). Brak takiego
  bloku = komunikat na czacie i pominięte zlecenie; sprawdzisz to komendą `/zf stations`.
- Callbacki kontraktu (OnContractSucceeded/Failed) nie przeżywają wczytania świata,
  dlatego mod trzyma ID w `contracts_mod_state.txt` i dodatkowo odpytuje stan co ~5 s.
