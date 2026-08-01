using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Frakcja sama stawia sobie stację (Etap 6 — ostatni brakujący element).
    ///
    /// Do 2026-08-01 blok kontraktów i sklep mogła mieć wyłącznie siatka oddana frakcji
    /// ręcznie przez `/zf stacja`, czyli rusztowanie testowe. W normalnej rozgrywce
    /// frakcje nie miały gdzie wystawiać zleceń ani handlować — cała ekonomia Etapu 6
    /// była niewidoczna.
    ///
    /// Warunek postawienia jest STANEM ŚWIATA, nie zapisem w storage: stawiamy tylko
    /// frakcji, która nie ma żadnego bloku ekonomicznego (<see cref="FactionEconomy.FindContractBlock"/>).
    /// Dzięki temu rzecz jest z natury idempotentna — po wczytaniu świata stacja już
    /// stoi, więc druga nie powstanie, a gdy gracz ją zburzy, frakcja po karencji
    /// odbuduje się sama. Żadnego pliku stanu do zgubienia.
    /// </summary>
    internal sealed class StationSpawner
    {
        private const string Prefab = "ZF_Stacja";

        private const int CheckEveryTicks = 1800;   // ~30 s przy 60 Hz — to nie jest pilne
        private const int RetryTicks = 18000;       // ~5 min karencji na frakcję po próbie

        // Na tyle daleko, żeby stacja nie wyrosła graczowi na głowie, i na tyle blisko,
        // żeby dało się do niej dolecieć. Radiolatarnia (50 km) sięga dużo dalej.
        private const double MinMeters = 6000;
        private const double MaxMeters = 12000;
        private const float FreeRadius = 150; // promień wolnego miejsca dla dużej siatki

        private static readonly string[] Tags = { "HEL", "KRW", "WGR" };

        // Karencja per frakcja: bez niej nieudany spawn wracałby co 30 s i zalewał czat.
        private readonly Dictionary<string, int> _nextTry = new Dictionary<string, int>();

        // SpawnPrefab jest asynchroniczny — jedna stacja naraz, żeby dwie nie wylądowały
        // w tym samym miejscu (FindFreePlace nie wie o siatce, która jeszcze nie powstała).
        private bool _spawning;

        public void Update(int tick)
        {
            if (_spawning || tick % CheckEveryTicks != 0)
            {
                return;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return; // bez gracza nie ma od czego odmierzyć dystansu
            }

            for (int i = 0; i < Tags.Length; i++)
            {
                string tag = Tags[i];
                if (MyAPIGateway.Session.Factions.TryGetFactionByTag(tag) == null)
                {
                    continue; // frakcji nie ma w świecie (Factions.sbc się nie wczytał)
                }
                if (FactionEconomy.FindContractBlock(tag) != null)
                {
                    continue; // ma już gdzie handlować — także wtedy, gdy to siatka z `/zf stacja`
                }
                int next;
                if (_nextTry.TryGetValue(tag, out next) && tick < next)
                {
                    continue;
                }
                _nextTry[tag] = tick + RetryTicks;
                Spawn(tag, player.GetPosition(), tick);
                return; // jedna stacja na przebieg
            }
        }

        private void Spawn(string tag, Vector3D playerPos, int tick)
        {
            string ignored;
            long owner = FactionEconomy.FindTargetIdentity(tag, out ignored);
            if (owner == 0)
            {
                // Bez właściciela stacja byłaby niczyja: nie miałaby kto opłacać kontraktów
                // (gra ściąga nagrodę z konta WŁAŚCICIELA BLOKU), a sprzątacz śmieci SE
                // kasuje bezpańskie siatki. Lepiej spróbować później.
                return;
            }

            Vector3D pos = PlaceNear(playerPos, tick);
            MatrixD m = MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up);
            var result = new List<IMyCubeGrid>();
            _spawning = true;

            MyAPIGateway.PrefabManager.SpawnPrefab(
                result,
                Prefab,
                pos,
                (Vector3)m.Forward,
                (Vector3)m.Up,
                Vector3.Zero,
                Vector3.Zero,
                null,
                SpawningOptions.SetNpcSpawnedGrid,
                owner,
                true,
                () =>
                {
                    _spawning = false;
                    if (result.Count == 0)
                    {
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            "Stacja " + tag + " nie powstała (brak prefabu " + Prefab + "?)");
                        return;
                    }
                    // Bezpiecznik jak przy rekwizytach zleceń: prefab potrafi przyjść bez
                    // właściciela mimo ownerId, a stacja bez właściciela jest bezużyteczna.
                    result[0].ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
                    result[0].CustomName = "Stacja " + tag;

                    double km = Vector3D.Distance(playerPos, pos) / 1000.0;
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        tag + " postawiła stację " + km.ToString("0.0") +
                        " km stąd — szukaj sygnału radiolatarni STACJA.");
                });
        }

        /// <summary>
        /// Punkt w losowym kierunku, w widełkach MinMeters..MaxMeters od gracza. Kierunek
        /// liczymy z licznika tików (tym samym trikiem co rekwizyty zleceń) — mod nie ma
        /// własnego generatora, a Random w ModAPI to proszenie się o kłopoty przy zapisie.
        /// FindFreePlace odsuwa stację, gdy trafi w asteroidę albo w cudzą siatkę.
        /// </summary>
        private static Vector3D PlaceNear(Vector3D origin, int tick)
        {
            var dir = new Vector3D((tick & 255) - 127.5,
                                   ((tick >> 8) & 255) - 127.5,
                                   ((tick >> 16) & 255) - 127.5);
            dir = dir.LengthSquared() > 1 ? Vector3D.Normalize(dir) : Vector3D.Forward;
            double meters = MinMeters + ((tick >> 5) & 63) / 63.0 * (MaxMeters - MinMeters);
            Vector3D wanted = origin + dir * meters;
            Vector3D? free = MyAPIGateway.Entities.FindFreePlace(wanted, FreeRadius);
            return free.HasValue ? free.Value : wanted;
        }
    }
}
