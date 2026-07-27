# Protokół mostka plikowego (v1)

Katalog: storage moda (MyAPIGateway.Utilities, per-world). Brain dostaje ścieżkę w configu.
Format: JSON Lines, UTF-8, jedna linia = jedna wiadomość. Append-only.
Wspólne pola: `v` (int, wersja=1), `seq` (rosnący licznik nadawcy), `ts` (unix ms), `type`, `data`.
Odbiorca pomija linie z nieznanym `type` oraz `v` > obsługiwana. Linia bez `\n` = niedokończona, czekaj.
Rotacja: nadawca po 5 MB zaczyna `events-NNNN.jsonl` / `commands-NNNN.jsonl`; odbiorca kasuje w pełni przetworzone stare pliki. Offsety: brain w SQLite (bridge_state), mod w swoim storage.

## events.jsonl (mod → brain)

session_start  {"world":"nazwa","player_id":123,"player_name":"Minik","mod_version":"0.1"}
heartbeat      {"pos":[x,y,z],"speed":m_s}                                  co 10 s
chat_message   {"text":"...","target":"KRW"|null,"in_range":["WGR"],"signal":"clear"|"weak"|"none"}   target=@frakcja; signal=łączność do adresata (5c)
proximity      {"faction":"WGR","state":"enter"|"exit","dist":2900}         enter<3000m, exit>4000m
combat_hit     {"attacker":123|null,"faction":"KRW","damage":450.5,"hits":37,"weapon":"gatling"}  agregat 3 s
grid_destroyed {"faction":"KRW","grid":"nazwa","by_player":true}
trade          {"faction":"HEL","kind":"buy"|"sell","value":1500}           heurystyka: zmiana salda + sklep frakcji <300 m
contract_created {"contract_id":"123","faction":"WGR","kind":"dostawa","reward":50000,"reward_str":"50000","opis":"dostawa 600 płyt stalowych"}  kontrakt naprawdę powstał w grze; ID -> SQLite
contract_done  {"contract_id":"...","faction":"WGR","success":true}        faction opcjonalne — brain zna je z ID
ransom_paid    {"faction":"KRW","item":"Iron","amount":500}                  B+ gracz dostarczył trybut do skrzynki zrzutu w oknie — pokój + relacja
ransom_expired {"faction":"KRW"}                                            B+ minął deadline bez dostawy — ataki trwają, trwała utrata wiarygodności
debug_command  {"cmd":"rel"|"tick"} | {"cmd":"spawn"|"okup"|"okup-surowce"|"kontrakt","faction":"KRW"}   /zf rel, /zf tick, /zf raid, /zf okup, /zf okup-surowce, /zf kontrakt

## commands.jsonl (brain → mod)

radio_message   {"faction":"KRW","text":"...","color":"red","priority":1}   [RADIO | NAZWA], TTL 2 min
spawn_request   {"faction":"KRW","kind":"patrol"|"raid"|"convoy","near_player":true,"context":"incydent#123"}
stand_down      {"faction":"KRW","ransom":4000}   frakcja odpuściła — statki rajdu odlatują; ransom>0 = mod pobiera tyle kredytów gracz→frakcja (Etap 6)
ransom_demand   {"faction":"KRW","item":"Iron","amount":500,"deadline_s":900}   B+ frakcja żąda trybutu: mod stawia skrzynkę zrzutu (owner=0, GPS), wstrzymuje ogień, pilnuje deadline; dostawa→ransom_paid, brak→ransom_expired
price_update    {"faction":"HEL","modifier":1.5}                            Etap 6 (jeszcze nieobsługiwane)
contract_create {"faction":"WGR","kind":"dostawa","reward":50000,"duration_min":45}  mod stawia kontrakt na bloku frakcji i odsyła contract_created

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
