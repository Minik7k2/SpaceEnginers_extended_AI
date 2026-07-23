using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>Wspólna konwencja nazw rotowanych plików mostka, patrz docs/protocol.md.</summary>
    internal static class BridgeNaming
    {
        public static string RotatedFileName(string prefix, int index)
        {
            return index <= 1 ? prefix + ".jsonl" : string.Format("{0}-{1:D4}.jsonl", prefix, index);
        }

        public static List<string> SplitCompleteLines(string content)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(content))
            {
                return lines;
            }
            int start = 0;
            while (true)
            {
                int nl = content.IndexOf('\n', start);
                if (nl < 0)
                {
                    break; // niedokończona końcówka (torn write) — ignorujemy do następnego pollu
                }
                lines.Add(content.Substring(start, nl - start));
                start = nl + 1;
            }
            return lines;
        }
    }

    /// <summary>
    /// Pisze events.jsonl (+ rotacja po rotateBytes). Trzyma TextWriter otwarty przez całą sesję
    /// (append), a przy (re)otwarciu pliku odtwarza jego dotychczasową zawartość, bo
    /// WriteFileInWorldStorage zawsze nadpisuje od zera — patrz docs/protocol.md.
    /// </summary>
    internal sealed class EventWriter : IDisposable
    {
        private readonly Type _owner;
        private readonly ulong _rotateBytes;
        private int _currentIndex = 1;
        private long _currentBytes;
        private long _seq;
        private TextWriter _writer;

        public EventWriter(Type owner, ulong rotateBytes)
        {
            _owner = owner;
            _rotateBytes = rotateBytes;

            while (MyAPIGateway.Utilities.FileExistsInWorldStorage(BridgeNaming.RotatedFileName("events", _currentIndex + 1), _owner))
            {
                _currentIndex++;
            }
            OpenActiveFile();
        }

        public void WriteSessionStart(string world, long playerId, string playerName, string modVersion)
        {
            string data = new Json.Builder()
                .Add("world", world)
                .Add("player_id", playerId)
                .Add("player_name", playerName)
                .Add("mod_version", modVersion)
                .Build();
            WriteLine("session_start", data);
        }

        public void WriteHeartbeat(double x, double y, double z, double speed)
        {
            string pos = "[" +
                x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                z.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            string data = new Json.Builder()
                .AddRaw("pos", pos)
                .Add("speed", speed)
                .Build();
            WriteLine("heartbeat", data);
        }

        public void WriteChatMessage(string text, string target, IEnumerable<string> inRange, string signal)
        {
            var builder = new Json.Builder().Add("text", text);
            if (target == null)
            {
                builder.AddRaw("target", "null");
            }
            else
            {
                builder.Add("target", target);
            }
            builder.AddStringArray("in_range", inRange);
            // 5c: jakość łączności do adresata (clear/weak/none) — brain bramkuje zasięgiem.
            builder.Add("signal", signal ?? "clear");
            WriteLine("chat_message", builder.Build());
        }

        /// <summary>Agregat obrażeń zadanych frakcji przez gracza w oknie 3 s (Etap 2).</summary>
        public void WriteCombatHit(long attacker, string faction, double damage, int hits, string weapon)
        {
            var builder = new Json.Builder();
            if (attacker == 0)
            {
                builder.AddRaw("attacker", "null");
            }
            else
            {
                builder.Add("attacker", attacker);
            }
            string data = builder
                .Add("faction", faction)
                .Add("damage", damage)
                .Add("hits", (long)hits)
                .Add("weapon", weapon)
                .Build();
            WriteLine("combat_hit", data);
        }

        public void WriteGridDestroyed(string faction, string grid, bool byPlayer)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("grid", grid)
                .Add("by_player", byPlayer)
                .Build();
            WriteLine("grid_destroyed", data);
        }

        public void WriteProximity(string faction, string state, long dist)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("state", state)
                .Add("dist", dist)
                .Build();
            WriteLine("proximity", data);
        }

        /// <summary>Komendy testowe "/zf rel" i "/zf tick" — brain odpowiada przez radio_message.</summary>
        public void WriteDebugCommand(string cmd)
        {
            string data = new Json.Builder()
                .Add("cmd", cmd)
                .Build();
            WriteLine("debug_command", data);
        }

        /// <summary>"/zf raid &lt;frakcja&gt;" — wymusza w brainie spawn_request danej frakcji (Etap 5).</summary>
        public void WriteDebugSpawn(string faction)
        {
            string data = new Json.Builder()
                .Add("cmd", "spawn")
                .Add("faction", faction)
                .Build();
            WriteLine("debug_command", data);
        }

        /// <summary>"/zf okup &lt;frakcja&gt;" — deterministyczny test de-eskalacji: brain odwołuje rajd (stand_down).</summary>
        public void WriteDebugOkup(string faction)
        {
            string data = new Json.Builder()
                .Add("cmd", "okup")
                .Add("faction", faction)
                .Build();
            WriteLine("debug_command", data);
        }

        /// <summary>Komenda czatu "/zf event {"type":"...","data":{...}}" — testy mostka bez SE.</summary>
        public void WriteRawEvent(string json)
        {
            Dictionary<string, object> obj;
            try
            {
                obj = Json.ParseObject(json);
            }
            catch (FormatException)
            {
                return;
            }

            object typeObj;
            if (!obj.TryGetValue("type", out typeObj) || !(typeObj is string))
            {
                return;
            }
            object dataObj;
            obj.TryGetValue("data", out dataObj);

            WriteLine((string)typeObj, Json.Stringify(dataObj ?? new Dictionary<string, object>()));
        }

        // DateTimeOffset jest poza whitelistą ModAPI — epokę liczymy z DateTime.
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private void WriteLine(string type, string dataJson)
        {
            _seq++;
            long ts = (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
            string line = new Json.Builder()
                .Add("v", 1L)
                .Add("seq", _seq)
                .Add("ts", ts)
                .Add("type", type)
                .AddRaw("data", dataJson)
                .Build();

            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();
            _currentBytes += Encoding.UTF8.GetByteCount(line) + 1;
            RotateIfNeeded();
        }

        private void OpenActiveFile()
        {
            string fileName = BridgeNaming.RotatedFileName("events", _currentIndex);
            string existing = null;
            if (MyAPIGateway.Utilities.FileExistsInWorldStorage(fileName, _owner))
            {
                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(fileName, _owner))
                {
                    existing = reader.ReadToEnd();
                }
            }

            _writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(fileName, _owner);
            _currentBytes = 0;
            if (!string.IsNullOrEmpty(existing))
            {
                _writer.Write(existing);
                _writer.Flush();
                _currentBytes = Encoding.UTF8.GetByteCount(existing);
            }
        }

        private void RotateIfNeeded()
        {
            if (_currentBytes < (long)_rotateBytes)
            {
                return;
            }
            _writer.Flush();
            _writer.Dispose();
            _currentIndex++;
            OpenActiveFile();
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Czyta commands.jsonl (+ rotowane commands-NNNN.jsonl) pisane przez brain. Offsety
    /// (liczba przetworzonych linii na plik) trzymane w małym pliku stanu moda — brain trzyma
    /// swoje offsety w SQLite, mod w swoim storage (sandbox), patrz docs/protocol.md.
    /// </summary>
    internal sealed class CommandReader
    {
        private const string StateFile = "bridge_mod_state.txt";

        private readonly Type _owner;
        private readonly Dictionary<string, int> _offsets = new Dictionary<string, int>();

        public CommandReader(Type owner)
        {
            _owner = owner;
            LoadState();
        }

        public List<Dictionary<string, object>> Poll()
        {
            var results = new List<Dictionary<string, object>>();

            int activeIndex = 0;
            for (int idx = 1; MyAPIGateway.Utilities.FileExistsInWorldStorage(BridgeNaming.RotatedFileName("commands", idx), _owner); idx++)
            {
                activeIndex = idx;
            }
            if (activeIndex == 0)
            {
                return results; // brain jeszcze nic nie napisał
            }

            bool stateChanged = false;
            for (int idx = 1; idx <= activeIndex; idx++)
            {
                string fileName = BridgeNaming.RotatedFileName("commands", idx);

                string content;
                try
                {
                    using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(fileName, _owner))
                    {
                        content = reader.ReadToEnd();
                    }
                }
                catch (Exception)
                {
                    // Wyścig z zapisem po stronie brainu (plik chwilowo niedostępny) —
                    // nie wywalamy gry, wracamy w następnym pollu za ~60 tików.
                    continue;
                }
                List<string> lines = BridgeNaming.SplitCompleteLines(content);

                int offset;
                _offsets.TryGetValue(fileName, out offset);

                for (int i = offset; i < lines.Count; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    try
                    {
                        results.Add(Json.ParseObject(line));
                    }
                    catch (FormatException)
                    {
                        // uszkodzona linia — pomijamy trwale, nie blokujemy mostka na jednym błędzie
                    }
                }

                if (lines.Count > offset)
                {
                    _offsets[fileName] = lines.Count;
                    stateChanged = true;
                }

                if (idx < activeIndex)
                {
                    // plik zamknięty przez brain (rotacja) i właśnie w pełni przetworzony — kasujemy
                    MyAPIGateway.Utilities.DeleteFileInWorldStorage(fileName, _owner);
                    _offsets.Remove(fileName);
                    stateChanged = true;
                }
            }

            if (stateChanged)
            {
                SaveState();
            }
            return results;
        }

        private void LoadState()
        {
            if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(StateFile, _owner))
            {
                return;
            }
            string content;
            using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(StateFile, _owner))
            {
                content = reader.ReadToEnd();
            }
            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('\t');
                if (parts.Length != 2)
                {
                    continue;
                }
                int offset;
                if (int.TryParse(parts[1], out offset))
                {
                    _offsets[parts[0]] = offset;
                }
            }
        }

        private void SaveState()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in _offsets)
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }
            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, _owner))
            {
                writer.Write(sb.ToString());
            }
        }
    }
}
