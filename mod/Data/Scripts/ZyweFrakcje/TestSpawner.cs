using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Testowy spawn wroga: /zf spawn [TAG] [NazwaPrefabu]. Lekki pomocnik do testów
    /// Etapu 2 — vanillowy prefab z własnością ustawioną na założyciela frakcji NPC.
    /// Pełny spawn przez MES API wchodzi w Etapie 5.
    /// </summary>
    internal static class TestSpawner
    {
        private const string DefaultPrefab = "DS_Pirate_ShakedownDrone"; // kosmiczny, jonowy
        private const string DefaultFactionTag = "SPRT";
        private const double SpawnDistance = 200;

        private static readonly List<IMyCubeGrid> Spawned = new List<IMyCubeGrid>();

        public static void HandleCommand(string args)
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                Show("brak postaci gracza — spróbuj po respawnie");
                return;
            }

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

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tag);
            if (faction == null)
            {
                Show("nie ma frakcji o tagu \"" + tag + "\"");
                return;
            }

            MatrixD view = player.Character.WorldMatrix;
            Vector3D pos = view.Translation + view.Forward * SpawnDistance + view.Up * 15;

            string spawnedPrefab = prefab;
            string spawnedTag = tag;
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
                    ? "zespawnowano " + spawnedPrefab + " dla " + spawnedTag + " (~200 m przed tobą)"
                    : "prefab \"" + spawnedPrefab + "\" nie powstał — sprawdź nazwę"));
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
