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

            if (messageText.StartsWith("/zf", StringComparison.OrdinalIgnoreCase))
            {
                // rel/tick wymagają silnika relacji (Etap 3) — jeszcze nie tutaj.
                sendToOthers = false;
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
                if ((typeObj as string) == "radio_message")
                {
                    HandleRadioMessage(msg);
                }
                // spawn_request / price_update / contract_create: obsługa w Etapach 5/6.
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

            MyAPIGateway.Utilities.ShowMessage("RADIO | " + faction, text);
        }
    }
}
