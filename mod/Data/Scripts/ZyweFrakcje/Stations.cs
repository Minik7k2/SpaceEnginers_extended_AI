using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Frakcja sama stawia sobie stację (Etap 6 — ostatni brakujący element).
    ///
    /// Do 2026-08-01 blok kontraktów i sklep mogła mieć wyłącznie siatka oddana frakcji
    /// ręcznie przez `/zf stacja`, czyli rusztowanie testowe. W normalnej rozgrywce
    /// frakcje nie miały gdzie wystawiać zleceń ani handlować.
    ///
    /// STAWIAMY GOTOWCE Z VANILLI, nie własny prefab. Pierwsza wersja stawiała ręcznie
    /// napisaną płytę 3x2 z czterema blokami na wierzchu — działała, ale wyglądała jak
    /// prototyp z warsztatu i nie miała nic wspólnego ze stacją. Vanilla ma gotowe,
    /// zaprojektowane stacje (3362 bloki u Helionu), więc bierzemy je i DOKŁADAMY im
    /// dwa brakujące bloki ekonomiczne — żaden vanillowy prefab nie ma ani terminala
    /// zleceń, ani sklepu (sprawdzone: wszystkie kandydatki mają 0 i 0).
    ///
    /// Bloki dokładamy przez <c>IMyCubeGrid.AddBlock</c> po spawnie, a nie edycją XML
    /// prefabu: dopiero na żywej siatce da się zapytać <c>CanAddCubes</c> o wolne miejsce,
    /// więc blok ląduje na kadłubie zamiast w środku innego bloku.
    ///
    /// Warunek postawienia jest STANEM ŚWIATA, nie zapisem w storage: stawiamy tylko
    /// frakcji, która nie ma żadnego bloku ekonomicznego. Dzięki temu rzecz jest
    /// idempotentna — po wczytaniu świata stacja już stoi, a gdy gracz ją zburzy,
    /// frakcja po karencji odbuduje się sama. Żadnego pliku stanu do zgubienia.
    /// </summary>
    internal sealed class StationSpawner
    {
        private const int CheckEveryTicks = 1800;   // ~30 s przy 60 Hz — to nie jest pilne
        private const int RetryTicks = 18000;       // ~5 min karencji na frakcję po próbie

        // Na tyle daleko, żeby stacja nie wyrosła graczowi na głowie, i na tyle blisko,
        // żeby dało się do niej dolecieć. Radiolatarnie stacji sięgają dużo dalej.
        private const double MinMeters = 8000;
        private const double MaxMeters = 15000;
        private const float FreeRadius = 400; // stacje vanilli bywają wielkie

        // Gotowe stacje z vanilli, dobrane do charakteru frakcji. Wszystkie statyczne,
        // duża siatka, z własnym zasilaniem (reaktory/baterie/panele).
        private static readonly string[] Tags = { "HEL", "KRW", "WGR" };
        private static readonly string[] Prefabs =
        {
            "GE_LogisticsFacility", // HEL: korporacyjne centrum logistyczne, 3362 bloki
            "RE19_PirateDepot",     // KRW: piracki skład, 591 bloków
            "RE05_StagingStation",  // WGR: przemysłowa stacja przeładunkowa, 766 bloków
        };

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
                Spawn(tag, Prefabs[i], i, player.GetPosition(), tick);
                return; // jedna stacja na przebieg
            }
        }

        private void Spawn(string tag, string prefab, int index, Vector3D playerPos, int tick)
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

            Vector3D pos = PlaceNear(playerPos, index, tick);
            MatrixD m = MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up);
            var result = new List<IMyCubeGrid>();
            _spawning = true;

            MyAPIGateway.PrefabManager.SpawnPrefab(
                result,
                prefab,
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
                            "Stacja " + tag + " nie powstała (brak prefabu " + prefab + "?)");
                        return;
                    }
                    IMyCubeGrid grid = result[0];
                    // Bezpiecznik jak przy rekwizytach zleceń: prefab potrafi przyjść bez
                    // właściciela mimo ownerId, a stacja bez właściciela jest bezużyteczna.
                    grid.ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
                    grid.CustomName = "Stacja " + tag;

                    string czego = DodajBlokiEkonomiczne(grid, owner);
                    double km = Vector3D.Distance(playerPos, pos) / 1000.0;
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        tag + " postawiła stację " + km.ToString("0.0") + " km stąd" + czego);
                });
        }

        /// <summary>
        /// Dokłada terminal zleceń i sklep, bo żaden vanillowy prefab stacji ich nie ma.
        /// Zwraca dopisek do komunikatu — gdy się nie uda, gracz ma o tym WIEDZIEĆ, inaczej
        /// stacja stoi i wygląda dobrze, a zleceń nie ma i nie wiadomo dlaczego.
        /// </summary>
        private static string DodajBlokiEkonomiczne(IMyCubeGrid grid, long owner)
        {
            long ignored;
            bool maKontrakty = FactionEconomy.HasBlockOfType(grid, FactionEconomy.ContractType, out ignored);
            bool maSklep = FactionEconomy.HasBlockOfType(grid, FactionEconomy.StoreType, out ignored);

            if (!maKontrakty)
            {
                maKontrakty = Dolóż(grid, new MyObjectBuilder_ContractBlock(),
                                    "ContractBlock", "Terminal zlecen", owner);
            }
            if (!maSklep)
            {
                maSklep = Dolóż(grid, new MyObjectBuilder_StoreBlock(),
                                "StoreBlock", "Sklep frakcji", owner);
            }

            if (maKontrakty && maSklep)
            {
                return " — terminal zleceń i sklep na miejscu.";
            }
            return " — UWAGA: nie udało się dołożyć " +
                   (maKontrakty ? "sklepu" : (maSklep ? "terminala zleceń" : "terminala ani sklepu")) +
                   " (brak wolnego miejsca na kadłubie?).";
        }

        // MyObjectBuilder_FunctionalBlock, bo stąd biorą się CustomName (z TerminalBlock)
        // i Enabled — terminal zleceń i sklep to bloki funkcyjne i muszą wstać włączone.
        private static bool Dolóż(IMyCubeGrid grid, MyObjectBuilder_FunctionalBlock ob,
                                  string subtype, string nazwa, long owner)
        {
            Vector3I pos;
            if (!ZnajdzWolneMiejsce(grid, out pos))
            {
                return false;
            }
            ob.Enabled = true;
            ob.SubtypeName = subtype;
            ob.Min = pos;
            ob.BlockOrientation = new SerializableBlockOrientation(
                Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
            ob.Owner = owner;
            ob.ShareMode = MyOwnershipShareModeEnum.Faction;
            ob.CustomName = nazwa;
            return grid.AddBlock(ob, false) != null;
        }

        // Kolejność ma znaczenie: najpierw w górę, żeby blok wylądował na wierzchu kadłuba,
        // a nie wciśnięty gdzieś z boku między inne bloki.
        private static readonly Vector3I[] Kierunki =
        {
            Vector3I.Up, Vector3I.Forward, Vector3I.Backward,
            Vector3I.Left, Vector3I.Right, Vector3I.Down,
        };

        /// <summary>
        /// Wolna kratka STYKAJĄCA SIĘ z istniejącym blokiem — inaczej blok albo poleciałby
        /// w próżnię, albo wszedł w kolizję. Pytamy żywą siatkę (CanAddCubes), bo tylko ona
        /// zna swoją geometrię; przy edycji prefabu w XML trzeba by to zgadywać.
        /// Wybieramy kratkę najwyżej położoną (największe Y) — dach stacji jest widoczny
        /// i osiągalny, a nie schowany w kadłubie.
        /// </summary>
        private static bool ZnajdzWolneMiejsce(IMyCubeGrid grid, out Vector3I pos)
        {
            var bloki = new List<IMySlimBlock>();
            grid.GetBlocks(bloki);

            bool znaleziono = false;
            Vector3I najlepsza = Vector3I.Zero;
            for (int i = 0; i < bloki.Count; i++)
            {
                Vector3I p = bloki[i].Position;
                for (int d = 0; d < Kierunki.Length; d++)
                {
                    Vector3I c = p + Kierunki[d];
                    if (znaleziono && c.Y <= najlepsza.Y)
                    {
                        continue; // już mamy wyżej — nie ma po co pytać siatki
                    }
                    if (grid.GetCubeBlock(c) == null && grid.CanAddCubes(c, c))
                    {
                        najlepsza = c;
                        znaleziono = true;
                    }
                }
            }
            pos = najlepsza;
            return znaleziono;
        }

        /// <summary>
        /// Punkt w widełkach MinMeters..MaxMeters od gracza. Kierunek jest ROZŁOŻONY PER
        /// FRAKCJA (co ~120°) z drobnym rozrzutem z licznika tików — pierwsza wersja losowała
        /// kierunek wyłącznie z tików i trzy stacje potrafiły wylądować kilkaset metrów od
        /// siebie, co wyglądało jak osiedle, a nie jak trzy niezależne frakcje.
        /// FindFreePlace odsuwa stację, gdy trafi w asteroidę albo w cudzą siatkę.
        /// </summary>
        private static Vector3D PlaceNear(Vector3D origin, int index, int tick)
        {
            double kat = index * (2.0 * Math.PI / 3.0) + ((tick >> 5) & 31) / 31.0 * 0.6;
            double wys = (((tick >> 10) & 31) / 31.0 - 0.5) * 0.8; // lekko nad/pod płaszczyzną
            var dir = new Vector3D(Math.Cos(kat), wys, Math.Sin(kat));
            dir = Vector3D.Normalize(dir);
            double meters = MinMeters + ((tick >> 3) & 63) / 63.0 * (MaxMeters - MinMeters);
            Vector3D wanted = origin + dir * meters;
            Vector3D? free = MyAPIGateway.Entities.FindFreePlace(wanted, FreeRadius);
            return free.HasValue ? free.Value : wanted;
        }
    }
}
