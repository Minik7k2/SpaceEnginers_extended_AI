using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Narzędzie testowe: <c>/zf stacja &lt;frakcja&gt;</c> — oddaje wskazaną siatkę frakcji NPC.
    /// Po co: kontrakty (Etap 6) powstają tylko na bloku kontraktów/sklepie NALEŻĄCYM do
    /// frakcji (<see cref="FactionEconomy.TryFindContractBlock"/>), a vanilla nie daje
    /// żadnego sposobu, żeby oddać własną budowlę frakcji NPC. Bez tej komendy testy I2-I10
    /// są nietestowalne — czekałyby na własne stacje frakcji z Etapu 7.
    ///
    /// Cel wybierany wzrokiem: siatka pod celownikiem do 200 m, a jak nic tam nie ma —
    /// najbliższa siatka w promieniu 150 m. UWAGA: to działa też na twój statek, więc celuj
    /// świadomie (oddany grid przestaje być twój).
    /// </summary>
    internal static class DebugStation
    {
        private const double RayMeters = 200;
        private const double NearMeters = 150;

        public static void Handle(string args)
        {
            string tag = (args ?? string.Empty).Trim().ToUpperInvariant();
            if (tag.Length == 0)
            {
                Show("Użycie: /zf stacja <frakcja> — oddaje siatkę pod celownikiem tej frakcji " +
                     "(np. postaw blok kontraktów, wyceluj w grid, wpisz /zf stacja WGR)");
                return;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tag);
            if (faction == null)
            {
                Show("nie ma frakcji o tagu \"" + tag + "\"");
                return;
            }
            if (faction.FounderId == 0)
            {
                Show("frakcja " + tag + " nie ma tożsamości założyciela — nie ma komu oddać siatki");
                return;
            }

            IMyCubeGrid grid = TargetGrid();
            if (grid == null)
            {
                Show("nie widzę siatki — wyceluj w nią (do " + (int)RayMeters + " m) albo podleć bliżej");
                return;
            }

            // ShareMode.All, nie Faction: właścicielem ma być frakcja (tego wymaga
            // TryFindContractBlock), ale terminal musi otwierać się GRACZOWI, który do tej
            // frakcji nie należy — inaczej blok kontraktów wita „odmowa dostępu". Tak samo
            // działają vanilla stacje handlowe: cudzy właściciel, otwarty terminal.
            grid.ChangeGridOwnership(faction.FounderId, MyOwnershipShareModeEnum.All);

            // Od razu mów, czy to wystarczy do kontraktów — inaczej gracz zgaduje, czemu
            // /zf kontrakt dalej milczy.
            long blockId;
            string gridName;
            if (FactionEconomy.TryFindContractBlock(tag, out blockId, out gridName))
            {
                Show("siatka \"" + grid.DisplayName + "\" należy teraz do " + tag +
                     " | blok kontraktów/sklepu: " + gridName + " (" + blockId + ") — /zf kontrakt " +
                     tag + " powinno przejść");
            }
            else
            {
                Show("siatka \"" + grid.DisplayName + "\" należy teraz do " + tag +
                     " | UWAGA: żadna siatka " + tag + " nie ma bloku kontraktów ani sklepu — " +
                     "dostaw Blok kontraktów i powtórz komendę");
            }
        }

        // Najpierw celownik (kamera działa też w kokpicie), potem najbliższa siatka.
        private static IMyCubeGrid TargetGrid()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Camera == null)
            {
                return null;
            }
            MatrixD cam = MyAPIGateway.Session.Camera.WorldMatrix;
            Vector3D from = cam.Translation;

            IHitInfo hit;
            if (MyAPIGateway.Physics.CastRay(from, from + cam.Forward * RayMeters, out hit) &&
                hit.HitEntity != null)
            {
                var aimed = hit.HitEntity as IMyCubeGrid;
                if (aimed != null && !aimed.MarkedForClose)
                {
                    return aimed;
                }
            }

            var sphere = new BoundingSphereD(from, NearMeters);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            IMyCubeGrid best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                double dist = Vector3D.Distance(from, grid.WorldMatrix.Translation);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = grid;
                }
            }
            return best;
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
