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
        private int _tick;

        public override void LoadData()
        {
            _events = new EventWriter(typeof(ZyweFrakcjeSession), RotateBytes);
            _commands = new CommandReader(typeof(ZyweFrakcjeSession));
            _radio = new RadioDisplay();
            _mes = new MESApi(); // rejestruje handler; MESApiReady dopiero gdy MES odeśle API
            TestSpawner.SetMes(_mes);
            _ransom = new RansomManager(_events); // B+ okup w surowcach: skrzynka zrzutu + detekcja
            // Hybryda reputacji: brain liczy, gra pokazuje (okno frakcji, wieżyczki, ceny).
            _reputation = new ReputationSync();
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
            _contracts = new ContractManager(typeof(ZyweFrakcjeSession), _events, _trade);
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
        }

        public override void UpdateAfterSimulation()
        {
            _tick++;

            if (_tick == 1)
            {
                WriteSessionStart();
            }
            if (_tick % HeartbeatEveryTicks == 0)
            {
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

            if (messageText.Trim().Equals("/zf stations", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                ReportStations(); // Etap 6.2 diag: czy nasze frakcje dostały stacje ekonomiczne
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
                string tag = messageText.Substring(raidPrefix.Length).Trim().ToUpperInvariant();
                if (tag.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf raid <frakcja> (np. /zf raid KRW)");
                }
                else
                {
                    // Wymuszony spawn przez brain: mod pisze debug_command spawn, brain
                    // odpisuje spawn_request, który wraca do PollCommands. Testuje cały potok.
                    _events.WriteDebugSpawn(tag);
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

            const string kontraktPrefix = "/zf kontrakt";
            if (messageText.StartsWith(kontraktPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                string tag = messageText.Substring(kontraktPrefix.Length).Trim().ToUpperInvariant();
                if (tag.Length == 0)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Użycie: /zf kontrakt <frakcja> (wymusza zlecenie, omija cooldown)");
                }
                else
                {
                    // Cały potok: mod -> brain -> contract_create -> ContractSystem -> contract_created.
                    _events.WriteDebugKontrakt(tag);
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

            if (messageText.StartsWith("/zf", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                MyAPIGateway.Utilities.ShowMessage("ZF", "Komendy: /zf rel, /zf rep, /zf tick, /zf stations, /zf spawn <frakcja>, /zf raid <frakcja>, /zf okup <frakcja>, /zf okup-surowce <frakcja>, /zf kontrakt <frakcja>, /zf daj <surowiec> [ilość], /zf stacja <frakcja>, /zf event <json>");
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
                // price_update: Etap 6 dalszy ciąg.
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

            _contracts.Create(faction, kind, reward, durationMin);
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
                    tags[i] + ": stacji=" + count + (count > 0 ? " [" + ids + "]" : "") + blok);
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
