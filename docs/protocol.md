# Protokół mostka plikowego (v1)

Katalog: storage moda (MyAPIGateway.Utilities, per-world). Brain dostaje ścieżkę w configu.
Format: JSON Lines, UTF-8, jedna linia = jedna wiadomość. Append-only.
Wspólne pola: `v` (int, wersja=1), `seq` (rosnący licznik nadawcy), `ts` (unix ms), `type`, `data`.
Odbiorca pomija linie z nieznanym `type` oraz `v` > obsługiwana. Linia bez `\n` = niedokończona, czekaj.
Rotacja: nadawca po 5 MB zaczyna `events-NNNN.jsonl` / `commands-NNNN.jsonl`; odbiorca kasuje w pełni przetworzone stare pliki. Offsety: brain w SQLite (bridge_state), mod w swoim storage.

## events.jsonl (mod → brain)

session_start  {"world":"nazwa","player_id":123,"player_name":"Minik","mod_version":"0.1"}
heartbeat      {"pos":[x,y,z],"speed":m_s}                                  co 10 s
chat_message   {"text":"...","target":"KRW"|null,"in_range":["WGR"]}        target = @frakcja
proximity      {"faction":"WGR","state":"enter"|"exit","dist":2900}         enter<3000m, exit>4000m
combat_hit     {"attacker":123|null,"faction":"KRW","damage":450.5,"hits":37,"weapon":"gatling"}  agregat 3 s
grid_destroyed {"faction":"KRW","grid":"nazwa","by_player":true}
trade          {"faction":"HEL","kind":"buy"|"sell","value":1500}           Etap 6
contract_done  {"contract_id":"...","faction":"WGR","success":true}        Etap 6
debug_command  {"cmd":"rel"|"tick"}                                        komendy testowe /zf rel, /zf tick

## commands.jsonl (brain → mod)

radio_message   {"faction":"KRW","text":"...","color":"red","priority":1}   [RADIO | NAZWA], TTL 2 min
spawn_request   {"faction":"KRW","kind":"patrol"|"raid"|"convoy","near_player":true,"context":"incydent#123"}
price_update    {"faction":"HEL","modifier":1.5}                            Etap 6
contract_create {"faction":"WGR","kind":"escort","reward":50000,"payload":{}}  Etap 6
