using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
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
            List<IMyCubeGrid> grids;
            if (!FactionGrids.TryGetValue(tag, out grids))
            {
                grids = new List<IMyCubeGrid>();
                FactionGrids[tag] = grids;
            }
            grids.Add(grid);
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

        // kind (patrol/raid/convoy) → nazwa SpawnGroupa MES (SpawnGroups.sbc).
        private static string GroupForKind(string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "raid": return "ZF_Raid";
                case "convoy": return "ZF_Convoy";
                default: return "ZF_Patrol";
            }
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
            Vector3D pos = view.Translation + view.Forward * SpawnDistance + view.Up * 15;

            if (!IsSpace(pos))
            {
                Show("frakcja " + tag + ": rajd na razie tylko w kosmosie — floty atmosferyczne/naziemne w budowie (spawn pominięty)");
                return;
            }

            MatrixD spawnMatrix = MatrixD.CreateWorld(pos, view.Forward, view.Up);

            string group = GroupForKind(kind);
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
            Vector3D pos = view.Translation + view.Forward * SpawnDistance + view.Up * 15;

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
