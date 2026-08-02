using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// B+ — okup w SUROWCACH. Na komendę brainu <c>ransom_demand</c> stawia skrzynkę zrzutu
    /// (prefab ZF_DropCrate, właściciel=0 żeby gracz mógł włożyć towar), znaczy ją GPS-em,
    /// wstrzymuje ogień statkom frakcji i pilnuje okna deadline. Co tik skanuje inwentarz
    /// skrzynki: dostawa (towar &gt;= ilość) => <c>ransom_paid</c> + despawn statków; deadline
    /// bez dostawy => <c>ransom_expired</c> + wznowienie ognia. Skrzynkę zawsze sprząta.
    ///
    /// Zakres v1: okup żyje w obrębie SESJI. Reload w trakcie okna anuluje pending (skrzynka
    /// zostaje jako zwykły, bezpański grid). Pełna persystencja przez reload — polish (Etap 7).
    /// Cały plik do weryfikacji W GRZE (SpawnPrefab/GPS/ChangeGridOwnership/GetInventory).
    /// </summary>
    internal sealed class RansomManager
    {
        private const string CratePrefab = "ZF_DropCrate";
        private const string CrateGridName = "Skrzynka zrzutu"; // DisplayName z prefabu — po nim poznajemy sieroty
        private const string GpsPrefix = "ZRZUT ";
        private const int TicksPerSecond = 60;
        private const int SweepTick = 180;        // ~3 s po wczytaniu świata: encje są już w scenie
        private const int CrateGraceTicks = 300;  // ~5 s na powstanie skrzynki (SpawnPrefab jest async)
        private const double CrateDistance = 120; // m przed graczem — w zasięgu, ale nie na kolanach
        private const float CrateFreeRadius = 15; // promień szukania wolnego miejsca (FindFreePlace)

        private readonly EventWriter _events;
        private bool _swept;

        private sealed class Pending
        {
            public string Faction;
            public string ItemKey;         // logiczny klucz (Iron/Nickel/...) — odsyłany w ransom_paid
            public string ItemPl;          // nazwa po polsku do komunikatu/GPS
            public MyDefinitionId ItemDef; // rzeczywisty przedmiot w inwentarzu (ingot)
            public long Amount;
            public int DeadlineTick;
            public int SpawnTick;          // do wykrycia „skrzynka nie powstała / przepadła"
            public IMyCubeGrid Crate;
            public IMyGps Gps;
        }

        // faction -> aktywne żądanie (jedno na frakcję; brain też nie dubluje).
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();

        public RansomManager(EventWriter events)
        {
            _events = events;
        }

        /// <summary>Komenda brainu ransom_demand (już wyłuskane pole "data").</summary>
        public void HandleDemand(Dictionary<string, object> data, int tick)
        {
            if (data == null)
            {
                return;
            }
            string faction = AsString(data, "faction");
            string itemKey = AsString(data, "item");
            long amount = AsLong(data, "amount");
            int deadlineS = (int)AsLong(data, "deadline_s");
            if (string.IsNullOrEmpty(faction) || string.IsNullOrEmpty(itemKey) || amount <= 0)
            {
                return;
            }
            if (_pending.ContainsKey(faction))
            {
                return; // już wisi żądanie tej frakcji — nie dubluj skrzynki
            }
            if (deadlineS <= 0)
            {
                deadlineS = 900;
            }

            MyDefinitionId def;
            if (!TryItemDef(itemKey, out def))
            {
                Show("okup " + faction + ": nieznany surowiec \"" + itemKey + "\" — żądanie pominięte");
                return;
            }
            string itemPl = PolishName(itemKey);

            Vector3D pos;
            if (!ChooseDropPos(out pos))
            {
                Show("okup " + faction + ": brak miejsca na skrzynkę (brak gracza) — pominięto");
                return;
            }

            // Rekord tworzymy od razu, żeby druga komenda nie zdublowała żądania w oknie
            // między wywołaniem SpawnPrefab a jego (asynchronicznym) callbackiem.
            var pending = new Pending
            {
                Faction = faction,
                ItemKey = itemKey,
                ItemPl = itemPl,
                ItemDef = def,
                Amount = amount,
                DeadlineTick = tick + deadlineS * TicksPerSecond,
                SpawnTick = tick,
            };
            _pending[faction] = pending;

            SpawnCrate(pending, pos);

            // Wstrzymanie ognia + komunikat od razu (nie czekamy na spawn skrzynki).
            TestSpawner.HoldFire(faction);
            Show("[" + faction + "] Trybut za pokój: dostarcz " + amount + " " + itemPl +
                 " do skrzynki zrzutu (GPS: ZRZUT " + faction + ") w " + (deadlineS / 60) +
                 " min. Inaczej ataki trwają.");
        }

        /// <summary>
        /// Czy tej frakcji wisi żądanie trybutu — dla `/zf autotest okup`. Drugi parametr
        /// mówi, czy skrzynka NAPRAWDĘ stoi w świecie: SpawnPrefab jest asynchroniczny, więc
        /// „żądanie jest" i „skrzynka jest" to dwa różne pytania i K2 sprawdza oba.
        /// </summary>
        public bool MaPending(string faction, out bool skrzynkaStoi)
        {
            skrzynkaStoi = false;
            Pending p;
            if (faction == null || !_pending.TryGetValue(faction, out p))
            {
                return false;
            }
            skrzynkaStoi = p.Crate != null && !p.Crate.MarkedForClose;
            return true;
        }

        /// <summary>
        /// Odwołanie wiszącego żądania bez kary i bez wznawiania ognia (stand_down: okup
        /// gotówkowy, kapitulacja, rozejm). Brain przy stand_down kasuje swój pending
        /// z założeniem, że skrzynkę sprząta mod — to jest to sprzątanie.
        /// </summary>
        public void Cancel(string faction)
        {
            if (string.IsNullOrEmpty(faction))
            {
                return;
            }
            Pending p;
            if (!_pending.TryGetValue(faction, out p))
            {
                return;
            }
            Cleanup(p);
            _pending.Remove(faction);
            Show("[" + faction + "] Żądanie trybutu odwołane — skrzynka zrzutu znika.");
        }

        /// <summary>Co tik z sesji: skan skrzynek i egzekwowanie deadline'ów.</summary>
        public void Update(int tick)
        {
            if (!_swept && tick >= SweepTick)
            {
                _swept = true;
                SweepOrphans();
            }
            if (_pending.Count == 0)
            {
                return;
            }
            List<string> done = null;
            foreach (KeyValuePair<string, Pending> kv in _pending)
            {
                Pending p = kv.Value;

                // Dostawa: kontener skrzynki ma >= żądaną ilość. Porównanie przez float
                // (MyFixedPoint ma explicit operator float; nie zakładamy operatora z long).
                if (p.Crate != null && !p.Crate.MarkedForClose && (float)CrateAmount(p.Crate, p.ItemDef) >= p.Amount)
                {
                    _events.WriteRansomPaid(p.Faction, p.ItemKey, p.Amount);
                    Cleanup(p);
                    TestSpawner.HandleStandDown(p.Faction); // dostarczono — statki odlatują
                    Show("[" + p.Faction + "] Trybut dostarczony (" + p.Amount + " " + p.ItemPl + "). Zawieszenie broni.");
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    continue;
                }

                // Skrzynka przepadła (nie powstała albo zjadł ją sprzątacz śmieci SE): gracz
                // nie ma gdzie zapłacić, więc kasujemy żądanie BEZ kary. Karać za nasz brak
                // skrzynki to najgorszy możliwy wariant świata mściwego.
                if (tick - p.SpawnTick > CrateGraceTicks && (p.Crate == null || p.Crate.MarkedForClose))
                {
                    _events.WriteRansomExpired(p.Faction, "brak_skrzynki");
                    Cleanup(p);
                    TestSpawner.ResumeFire(p.Faction);
                    Show("[" + p.Faction + "] Skrzynka zrzutu przepadła — żądanie trybutu anulowane (bez kary).");
                    (done ?? (done = new List<string>())).Add(kv.Key);
                    continue;
                }

                // Deadline: minął czas bez dostawy.
                if (tick >= p.DeadlineTick)
                {
                    _events.WriteRansomExpired(p.Faction, "deadline");
                    Cleanup(p);
                    TestSpawner.ResumeFire(p.Faction); // brak dostawy — ataki wznowione
                    Show("[" + p.Faction + "] Czas na trybut minął. Ataki trwają.");
                    (done ?? (done = new List<string>())).Add(kv.Key);
                }
            }
            if (done != null)
            {
                for (int i = 0; i < done.Count; i++)
                {
                    _pending.Remove(done[i]);
                }
            }
        }

        /// <summary>
        /// Skrzynka ZAWSZE przed graczem, nie przy statku frakcji. Dwa powody z gry:
        /// (1) sprzątacz śmieci SE kasuje małe gridy dalej niż PlayerDistanceThreshold (500 m)
        /// od gracza, a statek rajdu bywa kilometry stąd; (2) 25 m nad kadłubem lecącego
        /// drona to kolizja albo skrzynka zabrana w podróż. FindFreePlace pilnuje, żeby
        /// nie wsadzić jej w asteroidę ani w cudzy grid.
        /// </summary>
        private static bool ChooseDropPos(out Vector3D pos)
        {
            pos = Vector3D.Zero;
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return false;
            }
            MatrixD m = player.Character.WorldMatrix;
            Vector3D wanted = m.Translation + m.Forward * CrateDistance + m.Up * 5;
            Vector3D? free = MyAPIGateway.Entities.FindFreePlace(wanted, CrateFreeRadius);
            pos = free.HasValue ? free.Value : wanted;
            return true;
        }

        private void SpawnCrate(Pending pending, Vector3D pos)
        {
            MatrixD m = MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up);
            var result = new List<IMyCubeGrid>();
            MyAPIGateway.PrefabManager.SpawnPrefab(
                result,
                CratePrefab,
                pos,
                (Vector3)m.Forward,
                (Vector3)m.Up,
                Vector3.Zero,
                Vector3.Zero,
                null,
                SpawningOptions.None,
                0,      // ownerId = nikt (gracz musi mieć dostęp, by włożyć towar)
                true,
                () =>
                {
                    if (result.Count == 0)
                    {
                        Show("okup " + pending.Faction + ": skrzynka zrzutu nie powstała (prefab " + CratePrefab + "?)");
                        return;
                    }
                    pending.Crate = result[0];
                    // Bezpiecznik: właściciel=0, gdyby prefab miał domyślnego właściciela.
                    pending.Crate.ChangeGridOwnership(0, MyOwnershipShareModeEnum.All);
                    pending.Gps = AddGps(pending.Faction, pos, pending.Amount, pending.ItemPl);
                });
        }

        private static IMyGps AddGps(string faction, Vector3D pos, long amount, string itemPl)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null)
            {
                return null;
            }
            IMyGps gps = MyAPIGateway.Session.GPS.Create(
                GpsPrefix + faction,
                "Skrzynka zrzutu okupu — dostarcz " + amount + " " + itemPl,
                pos, true, false);
            MyAPIGateway.Session.GPS.AddGps(player.IdentityId, gps);
            return gps;
        }

        private void Cleanup(Pending p)
        {
            RemoveGps(GpsPrefix + p.Faction, p.Gps);
            p.Gps = null;
            if (p.Crate != null && !p.Crate.MarkedForClose)
            {
                p.Crate.Close();
            }
            p.Crate = null;
        }

        // Kasowanie po instancji potrafi nie trafić (kolekcja gracza trzyma własny wpis,
        // wyszukiwany po hashu z nazwy/opisu/pozycji), więc na wszelki wypadek dobijamy
        // po nazwie. Hashe zbieramy przed usuwaniem — nie modyfikujemy listy w trakcie.
        private static void RemoveGps(string name, IMyGps instance)
        {
            IMyPlayer player = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (player == null)
            {
                return;
            }
            if (instance != null)
            {
                MyAPIGateway.Session.GPS.RemoveGps(player.IdentityId, instance);
            }
            List<IMyGps> list = MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId);
            if (list == null)
            {
                return;
            }
            var hashes = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Name != null && list[i].Name.StartsWith(name))
                {
                    hashes.Add(list[i].Hash);
                }
            }
            for (int i = 0; i < hashes.Count; i++)
            {
                MyAPIGateway.Session.GPS.RemoveGps(player.IdentityId, hashes[i]);
            }
        }

        // Sprzątanie po poprzedniej sesji: pending nie przeżywa wczytania świata (zakres v1),
        // więc każda skrzynka ZF i każdy GPS „ZRZUT ..." w świeżo wczytanym świecie to sierota
        // po przerwanym oknie okupu. Bramka na _pending chroni przed zabiciem świeżej skrzynki,
        // gdyby żądanie przyszło w pierwszych sekundach po wczytaniu.
        private void SweepOrphans()
        {
            if (_pending.Count > 0)
            {
                return;
            }
            RemoveGps(GpsPrefix, null);

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            int closed = 0;
            foreach (IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                if (grid.DisplayName == null || !grid.DisplayName.StartsWith(CrateGridName))
                {
                    continue;
                }
                // Nasza skrzynka jest bezpańska (ownerId=0) — cudzej budowli nie ruszamy.
                List<long> owners = grid.BigOwners;
                if (owners != null && owners.Count > 0)
                {
                    continue;
                }
                grid.Close();
                closed++;
            }
            if (closed > 0)
            {
                Show("sprzątnięto porzucone skrzynki zrzutu: " + closed);
            }
        }

        // Suma danego surowca we wszystkich kontenerach skrzynki.
        private static MyFixedPoint CrateAmount(IMyCubeGrid crate, MyDefinitionId def)
        {
            var blocks = new List<IMySlimBlock>();
            crate.GetBlocks(blocks);
            MyFixedPoint total = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                IMyCubeBlock fat = blocks[i].FatBlock;
                if (!(fat is IMyCargoContainer))
                {
                    continue;
                }
                IMyEntity ent = fat as IMyEntity;
                if (ent == null || !ent.HasInventory)
                {
                    continue;
                }
                IMyInventory inv = ent.GetInventory(0);
                if (inv != null)
                {
                    total += inv.GetItemAmount(def);
                }
            }
            return total;
        }

        // Logiczny klucz surowca -> rzeczywisty przedmiot (ingot). Trybut w sztabkach
        // (bardziej wartościowy i klimatyczny niż ruda).
        private static bool TryItemDef(string key, out MyDefinitionId def)
        {
            try
            {
                def = MyDefinitionId.Parse("MyObjectBuilder_Ingot/" + key);
                return true;
            }
            catch (Exception)
            {
                def = new MyDefinitionId();
                return false;
            }
        }

        private static string PolishName(string key)
        {
            switch (key)
            {
                case "Iron": return "sztabek żelaza";
                case "Nickel": return "sztabek niklu";
                case "Silicon": return "sztabek krzemu";
                case "Cobalt": return "sztabek kobaltu";
                case "Silver": return "sztabek srebra";
                case "Gold": return "sztabek złota";
                case "Platinum": return "sztabek platyny";
                case "Magnesium": return "sztabek magnezu";
                case "Uranium": return "sztabek uranu";
                default: return key;
            }
        }

        private static string AsString(Dictionary<string, object> data, string key)
        {
            object o;
            return data.TryGetValue(key, out o) ? o as string : null;
        }

        // Liczby z Json.ParseObject przychodzą jako double (patrz Json.ParseNumber).
        private static long AsLong(Dictionary<string, object> data, string key)
        {
            object o;
            if (data.TryGetValue(key, out o) && o is double)
            {
                return (long)(double)o;
            }
            return 0;
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
