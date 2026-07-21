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
        private int _tick;

        public override void LoadData()
        {
            _events = new EventWriter(typeof(ZyweFrakcjeSession), RotateBytes);
            _commands = new CommandReader(typeof(ZyweFrakcjeSession));
            MyAPIGateway.Utilities.MessageEntered += OnMessageEntered;
        }

        public override void BeforeStart()
        {
            // DamageSystem jest dostępny dopiero tu, nie w LoadData.
            _combat = new CombatTracker(_events);
            _proximity = new ProximityWatcher(_events);
        }

        protected override void UnloadData()
        {
            if (MyAPIGateway.Utilities != null)
            {
                MyAPIGateway.Utilities.MessageEntered -= OnMessageEntered;
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

            if (messageText.StartsWith("/zf", StringComparison.OrdinalIgnoreCase))
            {
                sendToOthers = false;
                MyAPIGateway.Utilities.ShowMessage("ZF", "Komendy: /zf rel, /zf tick, /zf spawn <frakcja>, /zf raid <frakcja>, /zf event <json>");
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
            _events.WriteChatMessage(messageText, target, new string[0]);
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
                // price_update / contract_create: obsługa w Etapie 6.
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

            MyAPIGateway.Utilities.ShowMessage("RADIO | " + faction, text);
        }
    }
}
