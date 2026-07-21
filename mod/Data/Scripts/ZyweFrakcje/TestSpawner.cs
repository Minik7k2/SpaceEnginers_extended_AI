using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Spawner statków frakcji. Dwa wejścia:
    ///  - "/zf spawn [TAG] [Prefab]" — ręczny test (Etap 2), z opcjonalnym prefabem;
    ///  - spawn_request z brainu (Etap 5) — <see cref="SpawnForFaction"/>.
    /// Backend to na razie vanilla PrefabManager z własnością ustawioną na założyciela
    /// frakcji NPC (stub). Docelowo, gdy będą spawn groups z paczkami statków, podmienimy
    /// go na MES CustomSpawnRequest (patrol/raid/convoy → grupy spawnu MES).
    /// </summary>
    internal static class TestSpawner
    {
        private const string DefaultPrefab = "DS_Pirate_ShakedownDrone"; // kosmiczny, jonowy, uzbrojony
        private const string DefaultFactionTag = "SPRT";
        private const double SpawnDistance = 200;

        private static readonly List<IMyCubeGrid> Spawned = new List<IMyCubeGrid>();

        /// <summary>"/zf spawn [TAG] [Prefab]" — ręczny test z opcjonalnym prefabem.</summary>
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
        /// spawn_request z brainu: statek frakcji przy graczu. kind (patrol/raid/convoy)
        /// na razie tylko w komunikacie — docelowo mapowany na spawn groups MES.
        /// </summary>
        public static void SpawnForFaction(string tag, string kind)
        {
            Spawn(tag, DefaultPrefab, string.IsNullOrEmpty(kind) ? "patrol" : kind);
        }

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
                    ? "spawn " + spawnedKind + ": " + spawnedPrefab + " dla " + spawnedTag + " (~200 m przed tobą)"
                    : "prefab \"" + spawnedPrefab + "\" nie powstał — sprawdź nazwę"));
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
