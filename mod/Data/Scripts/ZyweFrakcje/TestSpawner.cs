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
    /// Spawner statków frakcji. Dwa wejścia:
    ///  - "/zf spawn [TAG] [Prefab]" — ręczny, niskopoziomowy test vanilla PrefabManagera;
    ///  - spawn_request z brainu (Etap 5) — <see cref="SpawnForFaction"/>.
    /// Backend produkcyjny to MES: <see cref="SetMes"/> wstrzykuje uchwyt API z sesji,
    /// a spawn_request idzie przez <c>CustomSpawnRequest</c> (grupy ZF_Patrol/ZF_Raid/
    /// ZF_Convoy z SpawnGroups.sbc, frakcja przez factionOverride). Gdy MES nie jest
    /// zasubskrybowany/gotowy, spawn_request spada na vanilla prefab (stub) — żeby mod
    /// działał też bez MES.
    /// </summary>
    internal static class TestSpawner
    {
        private const string DefaultPrefab = "DS_Pirate_ShakedownDrone"; // kosmiczny, jonowy, uzbrojony
        private const string DefaultFactionTag = "SPRT";
        private const double SpawnDistance = 200;
        private const string SpawnProfileId = "ZyweFrakcje"; // etykieta źródła spawnu w logach MES

        private static readonly List<IMyCubeGrid> Spawned = new List<IMyCubeGrid>();

        // Uchwyt MES API; ustawiany przez sesję w LoadData. null => MES niedostępny (fallback vanilla).
        private static MESApi _mes;

        /// <summary>Wstrzyknięcie uchwytu MES API z sesji (LoadData). null wyłącza ścieżkę MES.</summary>
        public static void SetMes(MESApi mes)
        {
            _mes = mes;
        }

        // --- Śledzenie zespawnowanych gridów frakcji + wycofanie (stand_down) ---
        private const int DespawnDelayTicks = 600; // ~10 s przy 60 Hz: zawieszenie ognia, potem despawn

        // faction -> żywe gridy rajdu tej frakcji (do wycofania na stand_down).
        private static readonly Dictionary<string, List<IMyCubeGrid>> FactionGrids =
            new Dictionary<string, List<IMyCubeGrid>>();
        // faction -> nazwa grupy z OSTATNIEGO CustomSpawnRequest (do EnsurePilot: rozróżnia
        // rajd/patrol od konwoju, żeby dobrać właściwe zachowanie CustomData).
        // Wpis KASUJEMY po zużyciu (2026-08-04): CustomSpawnRequest jest asynchroniczny,
        // więc bez tego rajd zamówiony po konwoju tej samej frakcji dostawał w callbacku
        // zachowanie konwoju. Dziś ryzyko jest małe (wszystkie grupy mają wyłączone spawny
        // naturalne, więc callback przychodzi tylko po NASZYM zamówieniu), ale nic tego nie
        // pilnowało, a objawem byłby transportowiec zamiast napastnika — łatwy do przeoczenia.
        private static readonly Dictionary<string, string> OstatniaGrupa =
            new Dictionary<string, string>();
        // gridy w trakcie wycofania -> tick, w którym mają zniknąć.
        private static readonly Dictionary<IMyCubeGrid, int> DespawnAt =
            new Dictionary<IMyCubeGrid, int>();
        private static bool _spawnActionRegistered;
        private static int _tick;
        // Stała referencja delegata — ta sama do rejestracji i wyrejestrowania w MES.
        private static readonly Action<IMyCubeGrid> SpawnAction = OnMesSpawn;

        private static bool IsOwnFaction(string tag)
        {
            return tag == "HEL" || tag == "KRW" || tag == "WGR";
        }

        /// <summary>Rejestruje w MES akcję po udanym spawnie (raz, gdy API gotowe). Woła sesja co tik.</summary>
        public static void EnsureSpawnActionRegistered()
        {
            if (_spawnActionRegistered || _mes == null || !_mes.MESApiReady)
            {
                return;
            }
            _mes.RegisterSuccessfulSpawnAction(SpawnAction, true);
            _spawnActionRegistered = true;
        }

        /// <summary>Wyrejestrowanie akcji spawnu (UnloadData).</summary>
        public static void UnregisterSpawnAction()
        {
            if (_spawnActionRegistered && _mes != null && _mes.MESApiReady)
            {
                _mes.RegisterSuccessfulSpawnAction(SpawnAction, false);
            }
            _spawnActionRegistered = false;
        }

        // MES woła to po każdym udanym spawnie (globalnie). Filtrujemy do naszych frakcji:
        // wyciszamy anteny/PB (koniec angielskiego gadania prefaba) i zapamiętujemy grid.
        private static void OnMesSpawn(IMyCubeGrid grid)
        {
            string tag = FactionTagOf(grid);
            if (tag == null || !IsOwnFaction(tag))
            {
                return;
            }
            SilenceGrid(grid);
            EnsurePilot(grid, tag);
            List<IMyCubeGrid> grids;
            if (!FactionGrids.TryGetValue(tag, out grids))
            {
                grids = new List<IMyCubeGrid>();
                FactionGrids[tag] = grids;
            }
            grids.Add(grid);
        }

        // Pancerz, który MES-owy [ReplaceArmorBlocksWithModules] potrafi zamienić — te same
        // dwa podtypy, których ArmorModuleReplacement.cs (MES) szuka na siatce (LargeArmor).
        private static readonly string[] ArmorSubtypes = { "LargeBlockArmorBlock", "LargeHeavyBlockArmorBlock" };

        // Treść CustomData musi odtwarzać profil, który grupa spawnu wskazuje w <Behaviour> —
        // MES normalnie zapisuje go sam (BehaviorBuilder.RivalAiInitialize), ale robi to na
        // bloku zdalnego sterowania ZNALEZIONYM w prefabie. Nasze duże kadłuby go nie mają,
        // więc w chwili manipulacji nie ma na czym zapisać i cała treść spada na nas.
        //
        // KLUCZOWE: dla rajdów to NIE jest goły ZF_Fighter, tylko ZF_Fighter_<TAG>
        // z mod/Data/ZF_Boty.sbc — różni się `[Triggers:ZF_Trigger_Zaloga_<TAG>]`, czyli
        // ZAŁOGĄ. Pominięcie triggera daje statek, który lata i strzela, ale jest pusty
        // w środku (sekcja P autotestu: „na pokładzie nie ma nikogo").
        private const string BehaviorFighterBaza = "[RivalAI Behavior]\n[BehaviorName:Fighter]";
        private const string BehaviorKonwoj =
            "[RivalAI Behavior]\n[BehaviorName:CargoShip]\n[AutopilotData:MES-DefaultAutoPilot-CargoShip]\n" +
            "[UsePauseAutopilotFromSpawnGroup:true]\n[GetSpeedFromSpawnGroup:true]";

        /// <summary>Profil bojowy z załogą tej frakcji (odpowiednik ZF_Fighter_TAG z ZF_Boty.sbc).</summary>
        private static string BehaviorFighterDla(string tag)
        {
            return OwnTags.Contains(tag)
                ? BehaviorFighterBaza + "\n[Triggers:ZF_Trigger_Zaloga_" + tag + "]"
                : BehaviorFighterBaza;
        }

        /// <summary>
        /// Dokłada blok zdalnego sterowania (RivalAIRemoteControlLarge) dużym kadłubom bez
        /// niego. ZDECYDOWANIE UDOWODNIONE dekompilacją MES (2026-08-02): manipulacja
        /// [ReplaceArmorBlocksWithModules]/[ModulesForArmorReplacement] w ZF_Manipulations.sbc
        /// NIGDY nie wstawi bloku RemoteControl — ArmorModuleReplacement.cs (MES) filtruje
        /// przez zamkniętą listę LargeModules/SmallModules (anteny suppressor + DefensiveCombat/
        /// FlightMovement), która NIE zawiera RemoteControl. To NIE jest kwestia konfiguracji czy
        /// warunków wyścigu — mechanizm strukturalnie nie potrafi tego bloku dodać, więc robimy to
        /// sami, na żywej siatce, tym samym wzorcem co Stations.cs (AddBlock po spawnie).
        /// </summary>
        private static void EnsurePilot(IMyCubeGrid grid, string tag)
        {
            if (grid.GridSizeEnum != MyCubeSize.Large)
            {
                return; // małe drony mają własne zdalne sterowanie w prefabie
            }

            var bloki = new List<IMySlimBlock>();
            grid.GetBlocks(bloki);
            for (int i = 0; i < bloki.Count; i++)
            {
                if (bloki[i].FatBlock is Sandbox.ModAPI.Ingame.IMyRemoteControl)
                {
                    return; // ma już pilota — nic do roboty
                }
            }

            IMySlimBlock armor = null;
            for (int i = 0; i < bloki.Count; i++)
            {
                string subtype = bloki[i].BlockDefinition.Id.SubtypeName;
                if (Array.IndexOf(ArmorSubtypes, subtype) >= 0)
                {
                    armor = bloki[i];
                    break;
                }
            }
            if (armor == null)
            {
                Show(tag + ": kadłub bez pancerza do podmiany na pilota — brak zdalnego sterowania");
                return;
            }

            Quaternion orientation;
            armor.Orientation.GetQuaternion(out orientation);
            Vector3I pos = armor.Position;

            string group;
            bool konwoj = OstatniaGrupa.TryGetValue(tag, out group) &&
                          group.IndexOf("Convoy", StringComparison.OrdinalIgnoreCase) >= 0;
            OstatniaGrupa.Remove(tag); // zużyte — kolejny spawn ma podać swoją grupę sam

            List<long> owners = grid.BigOwners;
            long owner = owners != null && owners.Count > 0 ? owners[0] : 0;

            var ob = new MyObjectBuilder_RemoteControl
            {
                SubtypeName = "RivalAIRemoteControlLarge",
                Min = pos,
                BlockOrientation = new SerializableBlockOrientation(ref orientation),
                Owner = owner,
                ShareMode = MyOwnershipShareModeEnum.Faction,
                CustomName = "AI Control Module",
            };
            // Enabled/CustomData nie istnieją na MyObjectBuilder_RemoteControl (ShipController
            // ma inną gałąź OB niż FunctionalBlock, w odróżnieniu od ContractBlock/StoreBlock
            // w Stations.cs) — ustawiamy je na już żywym bloku, jak SilenceGrid/NeutralizeGrid
            // w tym samym pliku.
            // ODWRACALNA PODMIANA (2026-08-04). Miejsce trzeba zwolnić przed AddBlock, ale
            // nieudane AddBlock (brak definicji RivalAIRemoteControlLarge bez MES, kolizja)
            // zostawiało dotąd kadłub z dziurą, BEZ pilota i BEZ jednego słowa na czacie —
            // a autotest wskazywał potem na manipulację MES, której w danych już nie ma.
            // Dlatego zapamiętujemy pancerz i przy porażce wstawiamy go z powrotem.
            var kopiaPancerza = armor.GetObjectBuilder() as MyObjectBuilder_CubeBlock;
            grid.RemoveBlock(armor);

            IMySlimBlock nowy = grid.AddBlock(ob, false);
            if (nowy == null)
            {
                if (kopiaPancerza != null)
                {
                    grid.AddBlock(kopiaPancerza, false); // kadłub wraca do stanu sprzed próby
                }
                Show(tag + ": nie udało się dołożyć bloku zdalnego sterowania " +
                     "(RivalAIRemoteControlLarge — jest MES?) — kadłub poleci bez pilota");
                return;
            }

            var terminal = nowy.FatBlock as Sandbox.ModAPI.Ingame.IMyTerminalBlock;
            if (terminal != null)
            {
                terminal.CustomData = konwoj ? BehaviorKonwoj : BehaviorFighterDla(tag);
            }
            else
            {
                Show(tag + ": blok zdalnego sterowania powstał, ale nie da się na nim ustawić " +
                     "profilu RivalAI — kadłub może dryfować");
            }
        }

        private static string FactionTagOf(IMyCubeGrid grid)
        {
            if (grid == null)
            {
                return null;
            }
            List<long> owners = grid.BigOwners;
            if (owners == null || owners.Count == 0)
            {
                return null;
            }
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[0]);
            return faction != null ? faction.Tag : null;
        }

        // Wyłącza anteny i programmable blocki — źródło angielskich komunikatów prefaba.
        private static void SilenceGrid(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            for (int i = 0; i < blocks.Count; i++)
            {
                IMyCubeBlock fat = blocks[i].FatBlock;
                if (fat == null)
                {
                    continue;
                }
                var antenna = fat as Sandbox.ModAPI.Ingame.IMyRadioAntenna;
                if (antenna != null)
                {
                    antenna.Enabled = false;
                    continue;
                }
                var pb = fat as Sandbox.ModAPI.Ingame.IMyProgrammableBlock;
                if (pb != null)
                {
                    pb.Enabled = false;
                }
            }
        }

        // Neutralizuje statek przy wycofaniu: broń OFF (zawieszenie ognia) + zdalne
        // sterowanie OFF (zatrzymuje RivalAI, żeby nie wznowił ognia przed despawnem).
        private static void NeutralizeGrid(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            for (int i = 0; i < blocks.Count; i++)
            {
                IMyCubeBlock fat = blocks[i].FatBlock;
                var gun = fat as Sandbox.ModAPI.Ingame.IMyUserControllableGun;
                if (gun != null)
                {
                    gun.Enabled = false;
                    continue;
                }
                var turret = fat as Sandbox.ModAPI.Ingame.IMyLargeTurretBase;
                if (turret != null)
                {
                    turret.Enabled = false;
                    continue;
                }
                // IMyRemoteControl nie dziedziczy IMyFunctionalBlock (brak .Enabled),
                // więc wyłączamy je przez rzut na IMyFunctionalBlock (blok i tak go implementuje).
                if (fat is Sandbox.ModAPI.Ingame.IMyRemoteControl)
                {
                    var func = fat as Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
                    if (func != null)
                    {
                        func.Enabled = false;
                    }
                }
            }
        }

        // Odwrotność NeutralizeGrid: włącza z powrotem broń i zdalne sterowanie (RivalAI
        // wznawia atak). Używane przy wygaśnięciu okupu surowcowego (B+) — ataki mają trwać dalej.
        private static void ReactivateGrid(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            for (int i = 0; i < blocks.Count; i++)
            {
                IMyCubeBlock fat = blocks[i].FatBlock;
                var gun = fat as Sandbox.ModAPI.Ingame.IMyUserControllableGun;
                if (gun != null)
                {
                    gun.Enabled = true;
                    continue;
                }
                var turret = fat as Sandbox.ModAPI.Ingame.IMyLargeTurretBase;
                if (turret != null)
                {
                    turret.Enabled = true;
                    continue;
                }
                if (fat is Sandbox.ModAPI.Ingame.IMyRemoteControl)
                {
                    var func = fat as Sandbox.ModAPI.Ingame.IMyFunctionalBlock;
                    if (func != null)
                    {
                        func.Enabled = true;
                    }
                }
            }
        }

        /// <summary>
        /// B+ okup w surowcach: wstrzymanie ognia NA CZAS okna zrzutu (odwracalne). W odróżnieniu
        /// od HandleStandDown NIE despawnuje ani nie czyści listy gridów — statki mają czekać, a po
        /// deadline wznowić ogień (ResumeFire) albo despawnować przy dostawie (HandleStandDown).
        /// </summary>
        public static void HoldFire(string faction)
        {
            List<IMyCubeGrid> grids;
            if (!FactionGrids.TryGetValue(faction, out grids))
            {
                return;
            }
            for (int i = 0; i < grids.Count; i++)
            {
                if (grids[i] != null && !grids[i].MarkedForClose)
                {
                    NeutralizeGrid(grids[i]);
                }
            }
        }

        /// <summary>
        /// Statki tej frakcji postawione w tej sesji przez MES (te same, które odwołuje
        /// stand_down). Do diagnostyki i do <see cref="Autotest"/> — pusta lista znaczy albo
        /// „nic nie stanęło", albo „poszła ścieżka awaryjna bez MES", bo tamtej nie śledzimy.
        /// </summary>
        public static List<IMyCubeGrid> SledzoneSiatki(string faction)
        {
            List<IMyCubeGrid> grids;
            return FactionGrids.TryGetValue(faction, out grids) ? grids : new List<IMyCubeGrid>();
        }

        /// <summary>Czy spawny idą przez MES (false = ścieżka awaryjna vanilla, bez śledzenia).</summary>
        public static bool MesAktywny { get { return _mes != null && _mes.MESApiReady; } }

        /// <summary>B+ okup w surowcach: wznowienie ognia po wygaśnięciu okupu (brak dostawy).</summary>
        public static void ResumeFire(string faction)
        {
            List<IMyCubeGrid> grids;
            if (!FactionGrids.TryGetValue(faction, out grids))
            {
                return;
            }
            for (int i = 0; i < grids.Count; i++)
            {
                if (grids[i] != null && !grids[i].MarkedForClose)
                {
                    ReactivateGrid(grids[i]);
                }
            }
        }

        /// <summary>
        /// stand_down z brainu (okup/kapitulacja/rozejm): statki rajdu frakcji natychmiast
        /// przestają strzelać (broń + AI off), a po <see cref="DespawnDelayTicks"/> znikają.
        /// </summary>
        public static void HandleStandDown(string faction)
        {
            List<IMyCubeGrid> grids;
            if (!FactionGrids.TryGetValue(faction, out grids) || grids.Count == 0)
            {
                Show("stand_down " + faction + ": brak statków rajdu do wycofania");
                return;
            }

            int count = 0;
            for (int i = 0; i < grids.Count; i++)
            {
                IMyCubeGrid grid = grids[i];
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                NeutralizeGrid(grid);
                DespawnAt[grid] = _tick + DespawnDelayTicks;
                count++;
            }
            grids.Clear();
            Show("stand_down " + faction + ": " + count + " statk(i) wstrzymuje ogień i despawnuje");
        }

        /// <summary>Co tik z sesji: zamyka gridy, którym minął czas wycofania.</summary>
        public static void Update(int tick)
        {
            _tick = tick;
            if (DespawnAt.Count == 0)
            {
                return;
            }
            List<IMyCubeGrid> done = null;
            foreach (var kv in DespawnAt)
            {
                if (tick >= kv.Value)
                {
                    if (kv.Key != null && !kv.Key.MarkedForClose)
                    {
                        kv.Key.Close();
                    }
                    if (done == null)
                    {
                        done = new List<IMyCubeGrid>();
                    }
                    done.Add(kv.Key);
                }
            }
            if (done != null)
            {
                for (int i = 0; i < done.Count; i++)
                {
                    DespawnAt.Remove(done[i]);
                }
            }
        }

        /// <summary>"/zf spawn [TAG] [Prefab]" — ręczny test vanilla (omija MES).</summary>
        public static void HandleCommand(string args)
        {
            string tag = DefaultFactionTag;
            string prefab = DefaultPrefab;
            if (!string.IsNullOrEmpty(args))
            {
                string trimmed = args.Trim();
                if (trimmed.Length > 0)
                {
                    int space = trimmed.IndexOf(' ');
                    if (space < 0)
                    {
                        tag = trimmed.ToUpperInvariant();
                    }
                    else
                    {
                        tag = trimmed.Substring(0, space).ToUpperInvariant();
                        prefab = trimmed.Substring(space + 1).Trim();
                    }
                }
            }
            Spawn(tag, prefab, "reczny");
        }

        /// <summary>
        /// spawn_request z brainu: statek frakcji przy graczu. Idzie przez MES gdy dostępny
        /// (kind → SpawnGroup), inaczej fallback na vanilla prefab.
        /// </summary>
        public static void SpawnForFaction(string tag, string kind)
        {
            string resolvedKind = string.IsNullOrEmpty(kind) ? "patrol" : kind;
            if (_mes != null && _mes.MESApiReady)
            {
                SpawnMes(tag, resolvedKind);
            }
            else
            {
                Spawn(tag, DefaultPrefab, resolvedKind);
            }
        }

        // Nasze frakcje mają własne floty (ZF_Raid_KRW itd.); reszta idzie na grupy ogólne.
        private static readonly HashSet<string> OwnTags =
            new HashSet<string> { "HEL", "KRW", "WGR" };

        /// <summary>
        /// (frakcja, kind) → nazwa SpawnGroupa MES (SpawnGroups.sbc). Do 2026-08-01 mapowanie
        /// szło po samym kind, więc wszystkie trzy frakcje latały tym samym pirackim dronem —
        /// mechanika się zgadzała, ale na ekranie nie było widać żadnej różnicy między nimi.
        /// Tag doklejamy tylko dla HEL/KRW/WGR: dla obcego tagu taka grupa nie istnieje,
        /// a MES odrzuciłby spawn nieznanej nazwy.
        /// </summary>
        private static string GroupForKind(string tag, string kind)
        {
            string baseName;
            switch (kind.ToLowerInvariant())
            {
                case "raid": baseName = "ZF_Raid"; break;
                case "convoy": baseName = "ZF_Convoy"; break;
                default: baseName = "ZF_Patrol"; break;
            }
            string upper = string.IsNullOrEmpty(tag) ? "" : tag.ToUpperInvariant();
            return OwnTags.Contains(upper) ? baseName + "_" + upper : baseName;
        }

        // Ile kolejnych spawnów zamawiano w tej sesji — z tego liczymy kierunek, żeby
        // kadłuby szły w RÓŻNE strony (patrz SpawnPoint).
        private static int _licznikSpawnow;

        // Promień wolnej przestrzeni żądany dla kadłuba. Duże rajdowe siatki
        // (C33_Military_Enforcer, C40_Pirate_Vulture) mają kilkadziesiąt metrów, więc
        // z zapasem — lepiej odsunąć statek dalej niż wtopić go w inny.
        private const float PromienWolnegoMiejsca = 150;

        // Wachlarz: kolejne spawny co 72°, a po pełnym obrocie o 40 m dalej w bok.
        private const int SpawnowNaObrot = 5;
        private const double RozrzutBazowy = 70;
        private const double RozrzutNaObrot = 40;
        // Sufit rozrzutu: licznik spawnów rośnie przez całą sesję, więc bez tego setny rajd
        // stawałby ~830 m w bok, a tysięczny ~8 km — czyli poza walką, o którą chodzi.
        // Po osiągnięciu sufitu wachlarz dalej kręci kątem, tylko na stałym promieniu.
        private const double RozrzutMax = 250;

        /// <summary>
        /// Punkt spawnu ~200 m przed graczem, ale ODSUNIĘTY od poprzednich (2026-08-02).
        ///
        /// Wcześniej oba wejścia spawnu liczyły dokładnie ten sam punkt
        /// (<c>Translation + Forward*200 + Up*15</c>), więc każdy kolejny rajd lądował
        /// w tym samym miejscu co poprzedni — przy `/zf autotest floty` trzy rajdy
        /// (HEL, KRW, WGR) wchodziły jeden w drugi.
        ///
        /// Dwa niezależne zabezpieczenia, bo każde łata inną dziurę:
        ///  * WACHLARZ z licznika — działa nawet wtedy, gdy poprzedni statek jeszcze nie
        ///    istnieje w świecie (CustomSpawnRequest jest asynchroniczny, więc przy szybkiej
        ///    serii zamówień samo szukanie wolnego miejsca patrzy na świat sprzed spawnu);
        ///  * FindFreePlace — odsuwa od tego, co JUŻ stoi (asteroidy, cudze statki, stacje),
        ///    czego sam wachlarz nie wie.
        /// </summary>
        private static Vector3D SpawnPoint(MatrixD view)
        {
            int n = _licznikSpawnow++;
            Vector3D wanted = view.Translation + view.Forward * SpawnDistance + view.Up * 15;

            if (n > 0)
            {
                // Pierwszy spawn leci prosto przed gracza (tak było i tak jest najczytelniej),
                // dopiero kolejne rozchodzą się na boki.
                double kat = n * (2.0 * Math.PI / SpawnowNaObrot);
                double bok = RozrzutBazowy + ((n - 1) / SpawnowNaObrot) * RozrzutNaObrot;
                if (bok > RozrzutMax)
                {
                    bok = RozrzutMax;
                }
                wanted += view.Right * (Math.Cos(kat) * bok) + view.Up * (Math.Sin(kat) * bok);
            }

            Vector3D? wolne = MyAPIGateway.Entities.FindFreePlace(wanted, PromienWolnegoMiejsca);
            return wolne.HasValue ? wolne.Value : wanted;
        }

        // Klasyfikacja środowiska w punkcie spawnu. Na razie KOSMOS vs PLANETA po
        // grawitacji naturalnej. Nasze grupy (jonowy dron + RivalAI) latają tylko
        // w kosmosie; warianty atmosferyczne/naziemne (osobne grupy + statki) dojdą
        // później — najpierw kosmos.
        private static bool IsSpace(Vector3D pos)
        {
            float gravityMultiplier;
            Vector3 gravity = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out gravityMultiplier);
            return gravity.Length() < 0.05f;
        }

        // Ścieżka MES: statek ~200 m przed graczem, frakcja przez factionOverride.
        // Brak fallbacku na vanilla — gdy MES jest gotowy, jego decyzja (np. odmowa
        // przez safety check) jest wiążąca i widoczna, a nie maskowana prefabem.
        private static void SpawnMes(string tag, string kind)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                Show("brak postaci gracza — spawn pominięty");
                return;
            }

            MatrixD view = player.Character.WorldMatrix;
            Vector3D pos = SpawnPoint(view);

            if (!IsSpace(pos))
            {
                Show("frakcja " + tag + ": rajd na razie tylko w kosmosie — floty atmosferyczne/naziemne w budowie (spawn pominięty)");
                return;
            }

            MatrixD spawnMatrix = MatrixD.CreateWorld(pos, view.Forward, view.Up);

            string group = GroupForKind(tag, kind);
            OstatniaGrupa[tag] = group;
            bool ok = _mes.CustomSpawnRequest(
                new List<string> { group },
                spawnMatrix,
                Vector3.Zero,
                false,     // ignoreSafetyCheck — niech MES znajdzie bezpieczne miejsce
                tag,       // factionOverride: HEL/KRW/WGR
                SpawnProfileId);

            Show(ok
                ? "spawn (MES) " + kind + ": " + group + " dla " + tag
                : "MES odrzucił spawn " + group + " dla " + tag + " (safety check?)");
        }

        // Fallback bez MES: gołe vanilla PrefabManager na własność frakcji (stub).
        private static void Spawn(string tag, string prefab, string kind)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                Show("brak postaci gracza — spawn pominięty");
                return;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tag);
            if (faction == null)
            {
                Show("nie ma frakcji o tagu \"" + tag + "\" — czy Factions.sbc się załadował? (spróbuj na nowym świecie)");
                return;
            }

            MatrixD view = player.Character.WorldMatrix;
            Vector3D pos = SpawnPoint(view);

            string spawnedPrefab = prefab;
            string spawnedTag = tag;
            string spawnedKind = kind;
            Spawned.Clear();
            MyAPIGateway.PrefabManager.SpawnPrefab(
                Spawned,
                prefab,
                pos,
                (Vector3)view.Forward,
                (Vector3)view.Up,
                Vector3.Zero,
                Vector3.Zero,
                null,
                SpawningOptions.RotateFirstCockpitTowardsDirection,
                faction.FounderId,
                true,
                () => Show(Spawned.Count > 0
                    ? "spawn (vanilla) " + spawnedKind + ": " + spawnedPrefab + " dla " + spawnedTag + " (~200 m przed tobą)"
                    : "prefab \"" + spawnedPrefab + "\" nie powstał — sprawdź nazwę"));
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
