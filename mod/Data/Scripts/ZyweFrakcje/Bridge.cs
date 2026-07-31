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

        /// <param name="balance">
        /// Saldo gracza w kredytach; -1 = nieznane. Brain przyjmuje okup w kredytach tylko
        /// wtedy, gdy oferta z czatu ma pokrycie — inaczej „dam ci milion" z pustym kontem
        /// kupowałoby pokój za darmo.
        /// </param>
        public void WriteChatMessage(string text, string target, IEnumerable<string> inRange,
                                     string signal, long balance)
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
            if (balance >= 0)
            {
                builder.Add("balans", balance);
            }
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

        /// <summary>
        /// Zniszczona siatka frakcji. is_station (siatka statyczna) decyduje po stronie
        /// brainu o wadze: statek -30, stacja -50 + TRWAŁY sufit relacji.
        /// </summary>
        public void WriteGridDestroyed(string faction, string grid, bool byPlayer, bool isStation)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("grid", grid)
                .Add("by_player", byPlayer)
                .Add("is_station", isStation)
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

        /// <summary>
        /// Etap 6 — handel wykryty heurystycznie (zmiana salda przy sklepie frakcji).
        /// kind: "buy" (gracz kupił) / "sell" (gracz sprzedał), value w kredytach.
        /// </summary>
        public void WriteTrade(string faction, string kind, long value)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("kind", kind)
                .Add("value", value)
                .Build();
            WriteLine("trade", data);
        }

        /// <summary>
        /// Etap 6 — kontrakt naprawdę powstał w grze. Dopiero to zdarzenie utrwala go
        /// w SQLite brainu (contract_id z gry musi przeżyć wczytanie świata).
        /// </summary>
        public void WriteContractCreated(string contractId, string faction, string kind, long reward,
                                         string opis, string target)
        {
            string data = new Json.Builder()
                .Add("contract_id", contractId)
                .Add("faction", faction)
                .Add("kind", kind)
                .Add("reward", reward)
                .Add("reward_str", reward.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add("opis", opis)
                // Cel nagrody za głowę: brain utrwala go w payloadzie kontraktu, żeby po
                // przyjęciu zlecenia wiedzieć, kogo ostrzec (ochrona celu).
                .Add("target", target ?? string.Empty)
                .Build();
            WriteLine("contract_created", data);
        }

        /// <summary>
        /// Etap 6 — gracz PRZYJĄŁ zlecenie w terminalu (OnContractAcquired). Osobne zdarzenie,
        /// bo to moment reakcji świata: konwój do eskorty, ochrona celu nagrody, utrata
        /// zaufania u wrogów wystawcy.
        /// </summary>
        public void WriteContractTaken(string contractId, string faction, string kind)
        {
            string data = new Json.Builder()
                .Add("contract_id", contractId)
                .Add("faction", faction)
                .Add("kind", kind)
                .Build();
            WriteLine("contract_taken", data);
        }

        /// <summary>Etap 6 — kontrakt rozliczony (wykonany albo zawalony).</summary>
        public void WriteContractDone(string contractId, string faction, bool success)
        {
            string data = new Json.Builder()
                .Add("contract_id", contractId)
                .Add("faction", faction)
                .Add("success", success)
                .Build();
            WriteLine("contract_done", data);
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

        /// <summary>
        /// "/zf kontrakt &lt;frakcja&gt; [typ]" — wymusza w brainie wystawienie zlecenia (Etap 6).
        /// kind == null => brain losuje typ wagami z [kontrakty.typy].
        /// </summary>
        public void WriteDebugKontrakt(string faction, string kind)
        {
            var builder = new Json.Builder()
                .Add("cmd", "kontrakt")
                .Add("faction", faction);
            if (!string.IsNullOrEmpty(kind))
            {
                builder = builder.Add("kind", kind);
            }
            string data = builder.Build();
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

        /// <summary>"/zf okup-surowce &lt;frakcja&gt;" — deterministyczny test żądania trybutu (B+).</summary>
        public void WriteDebugOkupSurowce(string faction)
        {
            string data = new Json.Builder()
                .Add("cmd", "okup-surowce")
                .Add("faction", faction)
                .Build();
            WriteLine("debug_command", data);
        }

        /// <summary>B+: gracz dostarczył żądany trybut do skrzynki zrzutu w oknie — pokój + relacja.</summary>
        public void WriteRansomPaid(string faction, string item, long amount)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("item", item)
                .Add("amount", amount)
                .Build();
            WriteLine("ransom_paid", data);
        }

        /// <summary>B+: minął deadline bez dostawy — ataki trwają, trwała utrata wiarygodności.</summary>
        /// <param name="reason">
        /// "deadline" — gracz nie dostarczył trybutu w oknie (kara, trwała nieufność).
        /// "brak_skrzynki" — to NASZA skrzynka przepadła (sprzątacz śmieci SE, despawn):
        /// gracz nie miał gdzie zapłacić, więc brain kasuje żądanie BEZ kary.
        /// </param>
        public void WriteRansomExpired(string faction, string reason)
        {
            string data = new Json.Builder()
                .Add("faction", faction)
                .Add("reason", reason ?? "deadline")
                .Build();
            WriteLine("ransom_expired", data);
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

        // Diagnostyka mostka (2026-08-01). Zerwany mostek wyglądał dokładnie jak zdrowy:
        // odczyt padał w catch niżej, offset nie schodził na dysk, a gra i log SE milczały.
        // Te liczniki pokazuje `/zf stations`, żeby następnym razem wystarczyła jedna komenda.
        public int ProcessedLines { get; private set; }   // linii skonsumowanych w tej sesji
        public int PollCount { get; private set; }        // ile razy Poll() w ogóle się wykonało
        public int ReadFailures { get; private set; }     // nieudane odczyty commands.jsonl (suma)
        public int SaveFailures { get; private set; }     // nieudane zapisy offsetu (suma)
        public string LastError { get; private set; }     // ostatni wyjątek, typ + treść

        // Bez tego pojedynczy wyścig z brainem zalewałby czat. Meldujemy dopiero serię,
        // czyli sytuację, w której awaria jest trwała, a nie chwilowa.
        private const int FailuresBeforeShout = 5;
        private int _consecutiveReadFailures;
        private bool _saveFailureReported;

        public CommandReader(Type owner)
        {
            _owner = owner;
            LoadState();
        }

        /// <summary>Jedna linia stanu mostka do `/zf stations`.</summary>
        public string Diagnostics()
        {
            int offset;
            _offsets.TryGetValue(BridgeNaming.RotatedFileName("commands", 1), out offset);
            string s = "mostek: offset=" + offset + " przetworzono=" + ProcessedLines +
                       " polli=" + PollCount;
            if (ReadFailures > 0 || SaveFailures > 0)
            {
                s += " | BŁĘDY odczyt=" + ReadFailures + " zapis=" + SaveFailures +
                     " (" + (LastError ?? "?") + ")";
            }
            return s;
        }

        public List<Dictionary<string, object>> Poll()
        {
            var results = new List<Dictionary<string, object>>();
            PollCount++;

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
                catch (Exception e)
                {
                    // Wyścig z zapisem po stronie brainu (plik chwilowo niedostępny) —
                    // nie wywalamy gry, wracamy w następnym pollu za ~60 tików. ALE seria
                    // takich błędów to martwy mostek, a nie wyścig: wtedy krzyczymy na czat.
                    ReadFailures++;
                    _consecutiveReadFailures++;
                    LastError = e.GetType().Name + ": " + e.Message;
                    if (_consecutiveReadFailures == FailuresBeforeShout)
                    {
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            "MOSTEK: nie mogę odczytać " + fileName + " (" + FailuresBeforeShout +
                            " prób z rzędu) — komendy brainu NIE docierają. " + LastError);
                    }
                    continue;
                }
                _consecutiveReadFailures = 0;
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
                    ProcessedLines += lines.Count - offset;
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

        /// <summary>
        /// Utrwala offsety. Nieudany zapis NIE może wywalić gry, ale nie może też przejść
        /// niezauważony: offset zostaje wtedy w pamięci, a po wczytaniu świata mod cofa się
        /// do starej wartości i przetwarza te same komendy DRUGI RAZ (zdublowane zlecenia,
        /// powtórzone radio, podwójny reputation_sync). Zaobserwowane 2026-07-31: plik stanu
        /// stał na 13 liniach, podczas gdy mod skonsumował 35.
        /// </summary>
        private void SaveState()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in _offsets)
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }
            try
            {
                using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, _owner))
                {
                    writer.Write(sb.ToString());
                }
            }
            catch (Exception e)
            {
                SaveFailures++;
                LastError = e.GetType().Name + ": " + e.Message;
                if (!_saveFailureReported)
                {
                    _saveFailureReported = true;
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "MOSTEK: nie mogę zapisać offsetu (" + StateFile + ") — po wczytaniu " +
                        "świata komendy powtórzą się. " + LastError);
                }
            }
        }
    }
}
