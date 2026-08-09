using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 1 — most: sesja pisze session_start/heartbeat/chat_message do events.jsonl,
    /// odczytuje commands.jsonl co ~60 tików i wyświetla radio_message jako [RADIO | NAZWA].
    /// Etap 5a: radio idzie przez RadioDisplay (kolor frakcji, kolejka priorytetowa, TTL 2 min).
    /// Zero System.Net, zero plików poza MyAPIGateway.Utilities.*FileInStorage (whitelist ModAPI).
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class ZyweFrakcjeSession : MySessionComponentBase
    {
        private const ulong RotateBytes = 5 * 1024 * 1024;
        private const int HeartbeatEveryTicks = 600;    // ~10 s przy 60 Hz
        private const int CommandsPollEveryTicks = 60;  // spec: "mod czyta co ~60 tików"
        private const string ModVersion = "0.1";

        private EventWriter _events;
        private CommandReader _commands;
        private CombatTracker _combat;
        private ProximityWatcher _proximity;
        private RadioDisplay _radio;
        private MESApi _mes;
        private RansomManager _ransom;
        private TradeWatcher _trade;
        private ContractManager _contracts;
        private ReputationSync _reputation;
        private PriceManager _prices;
        private StationSpawner _stations;
        private CrewSpawner _crew;
        private Autotest _autotest;
        private int _tick;

        // Ostrzeżenia raz na rodzaj problemu — inaczej komunikat leciałby co poll (~co 60 tików).
        private readonly HashSet<string> _warned = new HashSet<string>();

        // Awaria z LoadData. Nie melduje się jej od razu: w LoadData nie ma jeszcze czatu ani
        // HUD-u, więc ShowMessage poszedłby w próżnię. Czeka na pierwszy tik.
        private string _loadDataError;

        /// <summary>
        /// Mówi o problemie RAZ. Powstało po sesji 2026-07-31, w której komendy brainu ginęły
        /// bez śladu: handlery wychodziły na `if (_x == null) return;`, więc gra, czat i log SE
        /// wyglądały tak samo jak przy zdrowym mostku.
        /// </summary>
        private void WarnOnce(string key, string message)
        {
            if (_warned.Add(key))
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "UWAGA: " + message);
            }
        }

        /// <summary>
        /// UWAGA: wyjątek z LoadData zabija ładowanie świata („Przy ładowaniu świata wystąpił
        /// błąd" i powrót do menu) — gracz nie ma wtedy ani gry, ani pojęcia, że winny jest mod.
        /// Zdarzyło się 2026-08-01 przy nazwie świata z końcową spacją: storage moda gra składa
        /// z NAZWY świata, a nie ze ścieżki zapisu, więc EventWriter nie miał gdzie pisać.
        /// Dlatego każdy komponent osobno i nic nie leci wyżej — tak jak w BeforeStart().
        /// </summary>
        public override void LoadData()
        {
            // Załoga na stacjach przez AiEnabled. Zależność MIĘKKA: bez tamtego moda API
            // zgłasza się jako niegotowe i nikogo nie stawiamy — reszta działa bez zmian.
            //
            // MUSI BYĆ W LoadData, NIE W BeforeStart (ustalone 2026-08-05 z kodu AiEnabled).
            // Kontrakt tego API jest niesymetryczny i obie strony piszą go wprost w nagłówku:
            // odbiorca (`RemoteBotAPI`) rejestruje handler w LoadData, a nadawca (`LocalBotAPI`
            // w AiEnabled `AISession.BeforeStart`) rozsyła słownik metod DOKŁADNIE RAZ przez
            // `SendModMessage`. To wywołanie jest synchroniczne, więc handler zarejestrowany
            // dopiero w naszym BeforeStart mógł się spóźnić o całą fazę — i tak było: AiEnabled
            // nadawał, zanim ktokolwiek słuchał, `Valid` zostawało `false` na zawsze i ŻADNE
            // wywołanie API nie miało prawa zadziałać. Objawem był pusty pokład przy
            // zasubskrybowanym modzie, bez jednego błędu w logu SE. To NIE była kwestia
            // [BotType] ani nazw botów, na które wskazywały dotychczasowe tropy.
            try
            {
                _crew = new CrewSpawner();
            }
            catch (Exception e)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "BŁĄD startu załóg: " + e.GetType().Name + ": " + e.Message);
            }
            // EventWriter z założenia NIE rzuca: przy błędzie storage wyłącza się sam (Failed),
            // więc reszta moda zawsze ma z czym rozmawiać i nigdzie nie trzeba sprawdzać null.
            _events = new EventWriter(typeof(ZyweFrakcjeSession), RotateBytes);
            // CommandReader czyta plik offsetów, czyli robi I/O — jedyny tutaj realny kandydat
            // do wyjątku. Bez mostka w tę stronę gra żyje dalej, tylko brain nie ma jak odpowiadać.
            try
            {
                _commands = new CommandReader(typeof(ZyweFrakcjeSession));
            }
            catch (Exception e)
            {
                _loadDataError = "CommandReader: " + e.GetType().Name + ": " + e.Message;
            }
            try
            {
                _radio = new RadioDisplay();
                _mes = new MESApi(); // rejestruje handler; MESApiReady dopiero gdy MES odeśle API
                TestSpawner.SetMes(_mes);
                _ransom = new RansomManager(_events); // B+ okup w surowcach: skrzynka zrzutu + detekcja
                // Hybryda reputacji: brain liczy, gra pokazuje (okno frakcji, wieżyczki, ceny).
                _reputation = new ReputationSync();
            }
            catch (Exception e)
            {
                _loadDataError = (_loadDataError == null ? "" : _loadDataError + " | ") +
                                 e.GetType().Name + ": " + e.Message;
            }
            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;
        }

        public override void BeforeStart()
        {
            // DamageSystem jest dostępny dopiero tu, nie w LoadData.
            _combat = new CombatTracker(_events);
            _proximity = new ProximityWatcher(_events);
            // Ekonomia (Etap 6): handel z heurystyki salda, kontrakty przez ContractSystem.
            // Też dopiero tu — konto gracza i ContractSystem nie są gotowe w LoadData.
            _trade = new TradeWatcher(_events);
            // Każdy komponent w osobnym try/catch: gdy jeden nie wstanie, reszta ma działać
            // dalej, a gracz ma się o tym DOWIEDZIEĆ. Wcześniej wyjątek tutaj zostawiał
            // `_contracts`/`_prices` jako null i kontrakty milczały przez całą sesję.
            try
            {
                _contracts = new ContractManager(typeof(ZyweFrakcjeSession), _events, _trade);
            }
            catch (Exception e)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "BŁĄD startu kontraktów: " + e.GetType().Name + ": " + e.Message);
            }
            // Cennik sklepów frakcji (ceny bazowe wracają ze storage — patrz Prices.cs).
            // Też dopiero tu: LoadData jest za wcześnie na sięganie do świata.
            try
            {
                _prices = new PriceManager(typeof(ZyweFrakcjeSession));
            }
            catch (Exception e)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "BŁĄD startu cennika: " + e.GetType().Name + ": " + e.Message);
            }
            // Bez własnych stacji frakcje nie mają gdzie wystawiać zleceń ani handlować —
            // do 2026-08-01 trzeba było oddawać im siatkę ręcznie przez `/zf stacja`.
            _stations = new StationSpawner();
            // Samosprawdzanie w grze (/zf autotest) — dopiero tu, bo bierze wszystkie mechaniki,
            // które sprawdza: cennik, kontrakty, hybrydę reputacji i żądania okupu. Autotest
            // podaje im dokładnie takie ładunki, jakie przysłałby brain, więc sekcje działają
            // także wtedy, gdy zf_brain.exe nie jest uruchomiony.
            _autotest = new Autotest(_events, _prices, _contracts, _reputation, _ransom, _crew,
                                     _combat);
        }

        protected override void UnloadData()
        {
            if (MyAPIGateway.Utilities != null)
            {
                MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;
            }
            if (_mes != null)
            {
                TestSpawner.UnregisterSpawnAction();
                _mes.UnregisterListener();
                TestSpawner.SetMes(null);
            }
            if (_combat != null)
            {
                _combat.Dispose();
            }
            if (_events != null)
            {
                _events.Dispose();
            }
            if (_crew != null)
            {
                // RemoteBotAPI rejestruje handler wiadomości — trzeba go oddać przy wyjściu.
                _crew.Dispose();
            }
        }

        public override void UpdateAfterSimulation()
        {
            _tick++;

            if (_tick == 1)
            {
                ReportStartupProblems();
                WriteSessionStart();
            }
            if (_tick % HeartbeatEveryTicks == 0)
            {
                ReportStartupProblems(); // mostek potrafi paść też w trakcie gry
                WriteHeartbeat();
            }
            if (_tick % CommandsPollEveryTicks == 0)
            {
                PollCommands();
            }
            if (_combat != null)
            {
                _combat.Update();
            }
            if (_proximity != null)
            {
                _proximity.Update();
            }
            if (_radio != null)
            {
                _radio.Update(_tick); // kolejka priorytetowa radia: kolor + TTL + odstęp
            }
            if (_trade != null)
            {
                _trade.Update(); // handel: zmiana salda przy sklepie frakcji
            }
            if (_contracts != null)
            {
                _contracts.Update(); // stan kontraktów (po wczytaniu świata nie ma callbacków)
            }
            // MES bywa gotowy dopiero po kilku tikach — rejestrujemy akcję spawnu, gdy wstanie.
            TestSpawner.EnsureSpawnActionRegistered();
            TestSpawner.Update(_tick); // wycofanie: despawn statków po stand_down
            if (_ransom != null)
            {
                _ransom.Update(_tick); // B+ okup: skan skrzynek zrzutu + egzekwowanie deadline'ów
            }
            if (_reputation != null)
            {
                // Pilnuje, żeby natywna reputacja trzymała się wartości z brainu (gra
                // potrafi ruszyć ją sama, np. nagrodą za kontrakt).
                _reputation.Update(_tick);
            }
            if (_prices != null)
            {
                // Stacje NPC same odnawiają asortyment — nowe oferty przychodzą z cenami
                // z gry, więc cennik frakcji trzeba nakładać powtórnie, nie raz.
                _prices.Update(_tick);
            }
            if (_stations != null)
            {
                // Frakcja bez własnego punktu ekonomicznego stawia sobie stację.
                _stations.Update(_tick);
            }
            if (_crew != null)
            {
                // Ludzie na stacjach — tylko gdy gracz jest w okolicy i gdy AiEnabled żyje.
                _crew.Update(_tick);
            }
            if (_autotest != null)
            {
                // Maszyna kroków /zf autotest; poza przebiegiem testu kosztuje jedno porównanie.
                _autotest.Update(_tick);
            }
        }

        /// <summary>
        /// Melduje na czacie, że mod wstał kaleki. Do 2026-08-01 taka awaria albo wywalała
        /// ładowanie świata, albo (po naprawie) mogłaby zniknąć bez śladu — a mod bez mostka
        /// wygląda dokładnie jak mod zdrowy: frakcje po prostu milczą.
        /// Wołane co heartbeat, bo mostek potrafi paść też w środku gry; WarnOnce dedupuje.
        /// </summary>
        private void ReportStartupProblems()
        {
            if (_loadDataError != null)
            {
                WarnOnce("load-data", "część moda nie wstała: " + _loadDataError);
            }
            if (_events != null && _events.Failed)
            {
                WarnOnce("mostek-zapis",
                    "nie mogę pisać zdarzeń do storage świata — frakcje będą milczeć. " +
                    _events.FailureReason + " | Najczęstsza przyczyna: nazwa świata różni się " +
                    "od nazwy katalogu zapisu (np. spacja na końcu). Zmień nazwę świata na taką " +
                    "jak katalog w Saves.");
            }
            CheckWorldNameMatchesFolder();
        }

        private static readonly char[] PathSeparators = { '\\', '/' };

        /// <summary>
        /// Wyłapuje rozjazd nazwy świata z katalogiem zapisu — cichszego brata awarii z
        /// 2026-08-01. Gra składa storage moda z NAZWY świata
        /// (<c>MySession.WorldSavePath = SavesPath + SessionName</c>), a nie ze ścieżki zapisu,
        /// i nie przemianowuje katalogu przy zmianie nazwy (w MySession nie ma Directory.Move).
        /// Skutki rozjazdu: albo wyjątek (nazwa ze spacją na końcu — katalog takiej mieć nie może),
        /// albo, częściej, cicha strata: mod pisze do świeżego katalogu OBOK zapisu, więc
        /// kontrakty, ceny bazowe i offsety mostka wyglądają po wczytaniu jak skasowane.
        /// </summary>
        private void CheckWorldNameMatchesFolder()
        {
            string path = MyAPIGateway.Session.CurrentPath;
            string name = MyAPIGateway.Session.Name;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name))
            {
                return;
            }
            // Tylko dla świata wczytanego z Saves. Świeżo zaczęty świat ze scenariusza pokazuje
            // tu ścieżkę szablonu z Content i swojego katalogu jeszcze nie ma — ostrzeżenie
            // byłoby fałszywym alarmem, a po pierwszym zapisie nazwy i tak się zgadzają.
            if (path.IndexOf("\\Saves\\", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }
            int cut = path.LastIndexOfAny(PathSeparators);
            string folder = cut >= 0 ? path.Substring(cut + 1) : path;
            // Dwukropka katalog mieć nie może, więc gra podmienia go tak samo po obu stronach.
            if (string.Equals(folder, name.Replace(':', '-'), StringComparison.Ordinal))
            {
                return;
            }
            WarnOnce("nazwa-swiata",
                "nazwa świata [" + name + "] różni się od katalogu zapisu [" + folder +
                "] — gra kieruje dane moda obok zapisu. Ustaw nazwę świata dokładnie taką jak " +
                "katalog w Saves, inaczej kontrakty, ceny i stan mostka nie przetrwają wczytania.");
        }

        private void WriteSessionStart()
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            string worldName = MyAPIGateway.Session.Name ?? "";
            long playerId = player != null ? player.IdentityId : 0;
            string playerName = player != null ? player.DisplayName : "";
            _events.WriteSessionStart(worldName, playerId, playerName ?? "", ModVersion);
        }

        private void WriteHeartbeat()
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return;
            }
            Vector3D pos = player.Character.WorldMatrix.Translation;
            double speed = 0;
            if (player.Character.Physics != null)
            {
                speed = player.Character.Physics.LinearVelocity.Length();
            }
            _events.WriteHeartbeat(pos.X, pos.Y, pos.Z, speed);
        }

        private void OnMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (string.IsNullOrEmpty(messageText))
            {
                return;
            }

            const string eventPrefix = "/zf event ";
            if (messageText.StartsWith(eventPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                _events.WriteRawEvent(messageText.Substring(eventPrefix.Length));
                return;
            }

            const string spawnPrefix = "/zf spawn";
            if (messageText.StartsWith(spawnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                TestSpawner.HandleCommand(messageText.Substring(spawnPrefix.Length));
                return;
            }

            if (messageText.Trim().Equals("/zf rel", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                _events.WriteDebugCommand("rel");
                return;
            }

            if (messageText.Trim().Equals("/zf tick", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                _events.WriteDebugCommand("tick");
                return;
            }

            if (messageText.Trim().Equals("/zf rep", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                // Kontrola hybrydy: co brain chce mieć w oknie frakcji vs co gra ma naprawdę.
                if (_reputation != null)
                {
                    _reputation.Report();
                }
                return;
            }

            if (messageText.Trim().Equals("/zf ceny", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                // Kontrola cennika: jaki mnożnik kazał nałożyć brain i czy było na czym.
                if (_prices != null)
                {
                    _prices.Report();
                }
                return;
            }

            if (messageText.Trim().Equals("/zf stations", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                ReportStations(); // Etap 6.2 diag: czy nasze frakcje dostały stacje ekonomiczne
                return;
            }

            if (messageText.Trim().Equals("/zf kontrakty", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                // Diag „zlecenie powstało, ale go nie widać": pytamy grę, jakie kontrakty
                // wisi na naszym bloku i na stacjach frakcji — to rozstrzyga, czy problem
                // jest po stronie tworzenia, czy tylko wyświetlania w terminalu.
                _contracts.Report();
                return;
            }

            // Diagnostyka załogi: przechodzi krok po kroku tę samą ścieżkę co CrewSpawner
            // i MELDUJE, na którym warunku staje. Powstało 2026-08-05, gdy okazało się, że
            // boty nie pojawiają się także na stacji, przy graczu w zasięgu — a wszystkie
            // gałęzie odmowy w Crew.cs były do tej pory ciche albo prawie ciche.
            const string zalogaPrefix = "/zf zaloga";
            if (messageText.StartsWith(zalogaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                if (_crew == null)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "CrewSpawner nie wstał (patrz log SE)");
                }
                else
                {
                    _crew.Diagnostyka();
                }
                return;
            }

            // Oddanie siatki frakcji NPC — bez tego nie ma gdzie wystawić kontraktu (Etap 6).
            const string stacjaPrefix = "/zf stacja";
            if (messageText.StartsWith(stacjaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                DebugStation.Handle(messageText.Substring(stacjaPrefix.Length));
                return;
            }

            const string raidPrefix = "/zf raid";
            if (messageText.StartsWith(raidPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                // "/zf raid <frakcja> [rodzaj]" — rodzaj opcjonalny, jak przy `/zf kontrakt`.
                string[] czesci = messageText.Substring(raidPrefix.Length)
                    .Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (czesci.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Użycie: /zf raid <frakcja> [patrol|raid|convoy] (np. /zf raid WGR convoy)");
                }
                else
                {
                    string rodzaj = czesci.Length > 1 ? czesci[1].ToLowerInvariant() : null;
                    if (rodzaj != null && rodzaj != "patrol" && rodzaj != "raid" && rodzaj != "convoy")
                    {
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            "Nieznany rodzaj \"" + rodzaj + "\" — dozwolone: patrol, raid, convoy.");
                        return;
                    }
                    // Wymuszony spawn przez brain: mod pisze debug_command spawn, brain
                    // odpisuje spawn_request, który wraca do PollCommands. Testuje cały potok.
                    // Bez rodzaju brain dobiera flotę do nastroju frakcji (kind_for_state).
                    _events.WriteDebugSpawn(czesci[0].ToUpperInvariant(), rodzaj);
                }
                return;
            }

            // UWAGA: sprawdzaj PRZED "/zf okup" — inaczej prefiks "/zf okup" przechwyci tę komendę.
            const string okupSurowcePrefix = "/zf okup-surowce";
            if (messageText.StartsWith(okupSurowcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                string tag = messageText.Substring(okupSurowcePrefix.Length).Trim().ToUpperInvariant();
                if (tag.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf okup-surowce <frakcja> (deterministyczny test żądania trybutu)");
                }
                else
                {
                    // Wymuszone żądanie trybutu bez LLM: brain dobiera towar/ilość i wysyła ransom_demand.
                    _events.WriteDebugOkupSurowce(tag);
                }
                return;
            }

            // Dosypanie kasy graczowi — potrzebne, by sprawdzić, czy sufit nagrody kontraktu
            // idzie za saldem gracza (test: zmierz próg, dosyp, zmierz próg jeszcze raz).
            const string kasaPrefix = "/zf kasa";
            if (messageText.StartsWith(kasaPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                long kwota;
                string arg = messageText.Substring(kasaPrefix.Length).Trim();
                IMyPlayer player = MyAPIGateway.Session.Player;
                if (!long.TryParse(arg, out kwota) || player == null)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf kasa <ile> (np. /zf kasa 10000000)");
                }
                else
                {
                    long przed;
                    player.TryGetBalanceInfo(out przed);
                    player.RequestChangeBalance(kwota);
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "zlecono zmianę salda o " + kwota + " kr (przed: " + przed + ") — księgowanie może zająć chwilę");
                }
                return;
            }

            // UWAGA: musi być sprawdzone PRZED "/zf kontrakt", bo tamten prefiks połknąłby
            // "-test" jako nazwę frakcji.
            const string kontraktTestPrefix = "/zf kontrakt-test";
            if (messageText.StartsWith(kontraktTestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                string tag = messageText.Substring(kontraktTestPrefix.Length).Trim().ToUpperInvariant();
                if (tag.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf kontrakt-test <frakcja>");
                }
                else
                {
                    _contracts.SelfTest(tag);
                }
                return;
            }

            const string kontraktPrefix = "/zf kontrakt";
            if (messageText.StartsWith(kontraktPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                // "/zf kontrakt KRW nagroda" — drugi argument (opcjonalny) wymusza TYP
                // zlecenia; bez niego brain losuje wagami z [kontrakty.typy].
                string[] czesci = messageText.Substring(kontraktPrefix.Length)
                    .Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (czesci.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Użycie: /zf kontrakt <frakcja> [typ] (typy: dostawa, nagroda, transport, naprawa, poszukiwania, wlasne)");
                }
                else
                {
                    // Cały potok: mod -> brain -> contract_create -> ContractSystem -> contract_created.
                    _events.WriteDebugKontrakt(czesci[0].ToUpperInvariant(),
                                               czesci.Length > 1 ? czesci[1].ToLowerInvariant() : null);
                }
                return;
            }

            const string okupPrefix = "/zf okup";
            if (messageText.StartsWith(okupPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                string tag = messageText.Substring(okupPrefix.Length).Trim().ToUpperInvariant();
                if (tag.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf okup <frakcja> (deterministyczny test de-eskalacji)");
                }
                else
                {
                    // Wymuszona de-eskalacja bez LLM: brain odwołuje rajd -> stand_down -> statki odlatują.
                    _events.WriteDebugOkup(tag);
                }
                return;
            }

            // Narzędzie testowe: surowiec do ręki bez trybu eksperymentalnego (np. trybut B+).
            const string dajPrefix = "/zf daj";
            if (messageText.StartsWith(dajPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                DebugGive.Handle(messageText.Substring(dajPrefix.Length));
                return;
            }

            // Samosprawdzanie w grze: jedna komenda zamiast przeklikiwania listy z
            // docs/testy-reczne.md. Sprawdza to, czego nie da się sprawdzić poza grą
            // (czy ModAPI naprawdę robi to, co zakładamy) — patrz Autotest.cs.
            const string autotestPrefix = "/zf autotest";
            if (messageText.StartsWith(autotestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                if (_autotest == null)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Autotest nie wstał (BeforeStart) — patrz błędy startu wyżej.");
                }
                else
                {
                    _autotest.Handle(messageText.Substring(autotestPrefix.Length));
                }
                return;
            }

            if (messageText.StartsWith("/zf", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                MyAPIGateway.Utilities.ShowMessage("ZF", "Komendy: /zf rel, /zf rep, /zf ceny, /zf tick, /zf stations, /zf autotest [sekcja], /zf spawn <frakcja>, /zf raid <frakcja>, /zf okup <frakcja>, /zf okup-surowce <frakcja>, /zf kontrakt <frakcja> [typ], /zf daj <surowiec> [ilość], /zf stacja <frakcja>, /zf event <json>");
                return;
            }

            if (messageText.StartsWith("/"))
            {
                // Komendy innych modów (np. /MES.*) — nie są rozmową, nie idą do brainu.
                return;
            }

            string target = null;
            if (messageText.StartsWith("@") && messageText.Length > 1)
            {
                int spaceIdx = messageText.IndexOf(' ');
                if (spaceIdx > 1)
                {
                    target = messageText.Substring(1, spaceIdx - 1).ToUpperInvariant();
                }
            }

            // 5c — zasięg/szum: policz pobliskie frakcje, adresatowi nadaj jakość łączności.
            // Słaby sygnał (weak) psuje treść, którą „słyszy" frakcja (gracz widzi swój oryginał).
            string[] inRange = new string[0];
            string signal = "clear";
            string outgoing = messageText;
            IMyPlayer chatPlayer = MyAPIGateway.Session.Player;
            if (chatPlayer != null && chatPlayer.Character != null)
            {
                Vector3D pos = chatPlayer.Character.WorldMatrix.Translation;
                Dictionary<string, double> nearest =
                    FactionRadio.NearestByFaction(pos, chatPlayer.IdentityId);
                inRange = new List<string>(nearest.Keys).ToArray();
                if (target != null)
                {
                    signal = FactionRadio.SignalFor(target, nearest);
                    if (signal == "weak")
                    {
                        outgoing = FactionRadio.Garble(messageText);
                    }
                }
            }
            // Saldo gracza jedzie razem z wiadomością: brain bramkuje nim okup w kredytach
            // (oferta bez pokrycia nie kupuje pokoju). -1 = nieznane, brain wtedy nie ryzykuje.
            long balance = -1;
            if (chatPlayer != null && !chatPlayer.TryGetBalanceInfo(out balance))
            {
                balance = -1;
            }
            _events.WriteChatMessage(outgoing, target, inRange, signal, balance);
        }

        private void PollCommands()
        {
            if (_commands == null)
            {
                return; // nie wstał w LoadData; gracz dostał już ostrzeżenie z ReportStartupProblems
            }
            List<Dictionary<string, object>> messages = _commands.Poll();
            for (int i = 0; i < messages.Count; i++)
            {
                Dictionary<string, object> msg = messages[i];
                object typeObj;
                msg.TryGetValue("type", out typeObj);
                string type = typeObj as string;
                if (type == "radio_message")
                {
                    HandleRadioMessage(msg);
                }
                else if (type == "spawn_request")
                {
                    HandleSpawnRequest(msg);
                }
                else if (type == "stand_down")
                {
                    HandleStandDown(msg);
                }
                else if (type == "ransom_demand")
                {
                    HandleRansomDemand(msg);
                }
                else if (type == "contract_create")
                {
                    HandleContractCreate(msg);
                }
                else if (type == "reputation_sync")
                {
                    HandleReputationSync(msg);
                }
                else if (type == "price_update")
                {
                    HandlePriceUpdate(msg);
                }
            }
        }

        private void HandleSpawnRequest(Dictionary<string, object> msg)
        {
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;
            if (data == null)
            {
                return;
            }

            object factionObj;
            data.TryGetValue("faction", out factionObj);
            string faction = factionObj as string;
            if (string.IsNullOrEmpty(faction))
            {
                return;
            }

            object kindObj;
            data.TryGetValue("kind", out kindObj);
            string kind = kindObj as string ?? "patrol";

            TestSpawner.SpawnForFaction(faction, kind);
        }

        /// <summary>
        /// Etap 6: brain zleca wystawienie kontraktu (frakcja, nagroda, czas). Liczby z JSON
        /// są double (Json.ParseNumber), stąd rzuty.
        /// </summary>
        private void HandleContractCreate(Dictionary<string, object> msg)
        {
            if (_contracts == null)
            {
                WarnOnce("contracts", "komenda contract_create przyszła, ale ContractManager nie " +
                                      "wstał (BeforeStart) — zlecenia NIE powstaną");
                return;
            }
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;
            if (data == null)
            {
                return;
            }

            object factionObj;
            data.TryGetValue("faction", out factionObj);
            string faction = factionObj as string;
            if (string.IsNullOrEmpty(faction))
            {
                return;
            }

            object kindObj;
            data.TryGetValue("kind", out kindObj);
            string kind = kindObj as string ?? "dostawa";

            // Cel nagrody za głowę (typ "nagroda") — dla pozostałych typów pole jest puste.
            object targetObj;
            data.TryGetValue("target_faction", out targetObj);
            string targetFaction = targetObj as string;

            long reward = 0;
            object rewardObj;
            if (data.TryGetValue("reward", out rewardObj) && rewardObj is double)
            {
                reward = (long)(double)rewardObj;
            }

            int durationMin = 45;
            object durationObj;
            if (data.TryGetValue("duration_min", out durationObj) && durationObj is double)
            {
                durationMin = (int)(double)durationObj;
            }

            _contracts.Create(faction, kind, reward, durationMin, targetFaction);
        }

        /// <summary>
        /// Hybryda reputacji: brain przysyła swoją relację przepisaną na skalę gry, mod
        /// zapisuje ją przez MyAPIGateway.Session.Factions (patrz Reputation.cs).
        /// </summary>
        private void HandleReputationSync(Dictionary<string, object> msg)
        {
            if (_reputation == null)
            {
                return;
            }
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            _reputation.Handle(dataObj as Dictionary<string, object>);
        }

        /// <summary>
        /// Cennik (Etap 6): brain przysyła mnożnik cen wyliczony z relacji, mod przepisuje go
        /// ofertom na blokach sklepu frakcji (patrz Prices.cs).
        /// </summary>
        private void HandlePriceUpdate(Dictionary<string, object> msg)
        {
            if (_prices == null)
            {
                WarnOnce("prices", "komenda price_update przyszła, ale PriceManager nie wstał " +
                                   "(BeforeStart) — ceny NIE będą się zmieniać");
                return;
            }
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            _prices.Handle(dataObj as Dictionary<string, object>);
        }

        private void HandleStandDown(Dictionary<string, object> msg)
        {
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;
            if (data == null)
            {
                return;
            }

            object factionObj;
            data.TryGetValue("faction", out factionObj);
            string faction = factionObj as string;
            if (string.IsNullOrEmpty(faction))
            {
                return;
            }

            // Etap 6: realny okup — >0 znaczy pobierz tyle kredytów z konta gracza na konto
            // frakcji (JSON liczby to double, patrz Json.ParseNumber).
            object ransomObj;
            if (data.TryGetValue("ransom", out ransomObj) && ransomObj is double)
            {
                long ransom = (long)(double)ransomObj;
                if (ransom > 0)
                {
                    CollectRansom(faction, ransom);
                }
            }

            // Pokój unieważnia wiszący trybut w surowcach: brain przy stand_down kasuje swój
            // pending ("mod sprząta skrzynkę"), więc bez tego skrzynka + GPS wisiały do końca
            // deadline'u, a potem leciał ransom_expired — kara za niedostarczenie okupu, który
            // przed chwilą został rozliczony inaczej.
            if (_ransom != null)
            {
                _ransom.Cancel(faction);
            }

            TestSpawner.HandleStandDown(faction);
        }

        // B+ okup w surowcach: brain żąda trybutu — mod stawia skrzynkę zrzutu i pilnuje okna.
        private void HandleRansomDemand(Dictionary<string, object> msg)
        {
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;
            if (data != null && _ransom != null)
            {
                _ransom.HandleDemand(data, _tick);
            }
        }

        /// <summary>
        /// Realny okup (Etap 6): przelewa kredyty z konta gracza na konto frakcji. Pobiera
        /// min(żądane, saldo) — pirat bierze, ile masz. Konta obsługuje Economy (RequestChangeBalance).
        /// </summary>
        private void CollectRansom(string faction, long amount)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || amount <= 0)
            {
                return;
            }
            long balance;
            if (!player.TryGetBalanceInfo(out balance))
            {
                return;
            }
            long taken = balance < amount ? balance : amount;
            if (taken <= 0)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Okup dla " + faction + ": brak kredytów na koncie");
                return;
            }
            player.RequestChangeBalance(-taken);
            IMyFaction fac = MyAPIGateway.Session.Factions.TryGetFactionByTag(faction);
            if (fac != null)
            {
                fac.RequestChangeBalance(taken);
            }
            // Nasz własny przelew nie może wyglądać jak handel ze sklepem frakcji —
            // TradeWatcher bierze nowe saldo za punkt odniesienia i nic nie zgłasza.
            if (_trade != null)
            {
                _trade.Suppress();
            }
            string note = taken < amount ? " (tyle miałeś z żądanych " + amount + ")" : "";
            MyAPIGateway.Utilities.ShowMessage("ZF", "Okup zapłacony: " + taken + " kr dla " + faction + note);
        }

        /// <summary>
        /// Etap 6.2 diagnostyka: wypisuje liczbę i ID stacji ekonomicznych każdej naszej frakcji.
        /// Sprawdza kluczowe założenie — czy stałe frakcje (IsDefault) z typem ekonomicznym w SBC
        /// dostają stacje generowane przez Economy (potrzebne jako factionStationId do AddContract).
        /// Wynik >0 => ścieżka ContractSystem otwarta; 0 wszędzie => trzeba innego podejścia.
        /// </summary>
        private void ReportStations()
        {
            // Stan mostka i komponentów PRZED stacjami: gdy komendy nie docierają albo
            // ContractManager nie wstał, informacja o blokach jest bez znaczenia.
            if (_commands != null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", _commands.Diagnostics());
            }
            else
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "mostek: BRAK czytnika komend");
            }
            if (_events == null || _events.Failed)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "zapis zdarzeń: WYŁĄCZONY (" +
                    (_events == null ? "brak" : _events.FailureReason) + ")");
            }
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "komponenty: kontrakty=" + (_contracts == null ? "BRAK" : "ok") +
                " ceny=" + (_prices == null ? "BRAK" : "ok") +
                " handel=" + (_trade == null ? "BRAK" : "ok") +
                " reputacja=" + (_reputation == null ? "BRAK" : "ok"));

            string[] tags = { "HEL", "KRW", "WGR" };
            for (int i = 0; i < tags.Length; i++)
            {
                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tags[i]);
                if (faction == null)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", tags[i] + ": brak frakcji (nowy świat?)");
                    continue;
                }
                int count = 0;
                string ids = "";
                foreach (IMyFactionStation station in faction.Stations)
                {
                    count++;
                    ids += (ids.Length > 0 ? ", " : "") + station.Id;
                }
                // Etap 6: dla kontraktów liczy się nie tyle stacja Economy, co BLOK
                // (kontraktów albo sklepu) należący do frakcji — to on jest startBlockId.
                long blockId;
                string gridName;
                string blok = FactionEconomy.TryFindContractBlock(tags[i], out blockId, out gridName)
                    ? " | blok kontraktów: " + (gridName ?? "?") + " (" + blockId + ")"
                    : " | BRAK bloku kontraktów/sklepu — zlecenia nie powstaną";

                MyAPIGateway.Utilities.ShowMessage("ZF",
                    tags[i] + ": kasa=" + FactionFunds.Balance(faction) + " kr, stacji=" + count +
                    (count > 0 ? " [" + ids + "]" : "") + blok);
            }
        }

        private void HandleRadioMessage(Dictionary<string, object> msg)
        {
            object dataObj;
            msg.TryGetValue("data", out dataObj);
            var data = dataObj as Dictionary<string, object>;
            if (data == null)
            {
                return;
            }

            object factionObj;
            data.TryGetValue("faction", out factionObj);
            string faction = factionObj as string ?? "???";

            object textObj;
            data.TryGetValue("text", out textObj);
            string text = textObj as string ?? "";

            object colorObj;
            data.TryGetValue("color", out colorObj);
            string color = colorObj as string ?? "white";

            // JSON liczby parsujemy jako double (Json.ParseNumber) — stąd rzut przez double.
            int priority = 0;
            object priorityObj;
            if (data.TryGetValue("priority", out priorityObj) && priorityObj is double)
            {
                priority = (int)(double)priorityObj;
            }

            long ts = 0;
            object tsObj;
            if (msg.TryGetValue("ts", out tsObj) && tsObj is double)
            {
                ts = (long)(double)tsObj;
            }

            _radio.Enqueue(faction, text, color, priority, ts);
        }
    }
}
