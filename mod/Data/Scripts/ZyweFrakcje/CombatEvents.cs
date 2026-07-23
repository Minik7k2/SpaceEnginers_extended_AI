using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 5c — zasięg radia. Na żądanie (wpisany czat) skanuje pobliskie gridy frakcji
    /// i ustala jakość łączności do adresata: clear (blisko), weak (daleko, szum), none
    /// (brak grida frakcji w zasięgu). Przy weak psuje treść (słaby odbiór). Osobne, większe
    /// progi niż ProximityWatcher — radio sięga dalej niż „blisko".
    /// </summary>
    internal static class FactionRadio
    {
        public const double ClearMeters = 6000;  // czysty odbiór
        public const double MaxMeters = 15000;   // dalej = poza zasięgiem radia

        /// <summary>Najbliższy grid każdej frakcji NPC w promieniu MaxMeters od gracza.</summary>
        public static Dictionary<string, double> NearestByFaction(Vector3D pos, long playerId)
        {
            var result = new Dictionary<string, double>();
            BoundingSphereD sphere = new BoundingSphereD(pos, MaxMeters);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            for (int i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as IMyCubeGrid;
                if (grid == null)
                {
                    continue;
                }
                string faction = GridOwnership.NpcFactionTag(grid, playerId);
                if (faction == null)
                {
                    continue;
                }
                double dist = Vector3D.Distance(pos, grid.GetPosition());
                double current;
                if (!result.TryGetValue(faction, out current) || dist < current)
                {
                    result[faction] = dist;
                }
            }
            return result;
        }

        /// <summary>clear/weak/none dla adresata wg najbliższego grida jego frakcji.</summary>
        public static string SignalFor(string faction, Dictionary<string, double> nearest)
        {
            double dist;
            if (faction == null || !nearest.TryGetValue(faction, out dist))
            {
                return "none";
            }
            return dist <= ClearMeters ? "clear" : "weak";
        }

        /// <summary>
        /// Szum słabego sygnału: część znaków ginie w trzaskach (spacje zostają).
        /// Pseudolosowo z pozycji i kodu znaku — bez System.Random (pewność whitelisty ModAPI);
        /// deterministyczne, ale zróżnicowane w obrębie wiadomości.
        /// </summary>
        public static string Garble(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool drop = c != ' ' && (((i * 37 + c) & 7) < 3); // ~3/8 znaków w trzaski
                sb.Append(drop ? '.' : c);
            }
            return sb.ToString();
        }
    }

    /// <summary>Ustalanie frakcji NPC władającej siatką (wspólne dla walki i proximity).</summary>
    internal static class GridOwnership
    {
        /// <returns>Tag frakcji NPC albo null (siatka niczyja / gracza / bez frakcji).</returns>
        public static string NpcFactionTag(IMyCubeGrid grid, long playerIdentity)
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
            long owner = owners[0];
            if (owner == playerIdentity)
            {
                return null;
            }
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owner);
            return faction == null ? null : faction.Tag;
        }
    }

    /// <summary>
    /// Etap 2 — walka: handler MyDamageInformation z atrybucją ataku do gracza
    /// (broń→siatka→BigOwners→gracz), agregacja combat_hit w oknie 3 s per frakcja,
    /// grid_destroyed = usunięcie siatki ze świeżymi obrażeniami od gracza
    /// (okno 30 s odróżnia zniszczenie od czystego despawnu MES). docs/protocol.md.
    /// </summary>
    internal sealed class CombatTracker : IDisposable
    {
        private const int FlushAfterTicks = 180;    // 3 s @ 60 Hz — okno agregacji
        private const int FreshDamageTicks = 1800;  // 30 s — zniszczenie vs despawn MES
        private const int CleanupEveryTicks = 3600; // sprzątanie słownika siatek co minutę

        private sealed class Aggregate
        {
            public double Damage;
            public int Hits;
            public string Weapon;
            public int FirstTick;
        }

        private sealed class DamagedGrid
        {
            public string Faction;
            public string Name;
            public int LastTick;
        }

        private readonly EventWriter _events;
        private readonly Dictionary<string, Aggregate> _agg = new Dictionary<string, Aggregate>();
        private readonly Dictionary<long, DamagedGrid> _damaged = new Dictionary<long, DamagedGrid>();
        private readonly List<string> _keysToFlush = new List<string>();
        private readonly List<long> _gridsToForget = new List<long>();
        private int _tick;
        private bool _disposed;

        public CombatTracker(EventWriter events)
        {
            _events = events;
            // Handlera obrażeń nie da się wyrejestrować — po Dispose zostaje w grze,
            // dlatego każde wejście sprawdza _disposed i wychodzi natychmiast.
            MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(0, OnAfterDamage);
            MyAPIGateway.Entities.OnEntityRemove += OnEntityRemove;
        }

        public void Update()
        {
            _tick++;
            if (_agg.Count > 0 && _tick % 30 == 0)
            {
                FlushAged();
            }
            if (_tick % CleanupEveryTicks == 0)
            {
                ForgetStaleGrids();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (MyAPIGateway.Entities != null)
            {
                MyAPIGateway.Entities.OnEntityRemove -= OnEntityRemove;
            }
        }

        private void OnAfterDamage(object target, MyDamageInformation info)
        {
            if (_disposed || info.Amount <= 0f)
            {
                return;
            }
            var slim = target as IMySlimBlock;
            if (slim == null || slim.CubeGrid == null)
            {
                return;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null)
            {
                return;
            }

            string weapon;
            long attacker = ResolveAttacker(info.AttackerId, out weapon);
            if (attacker == 0 || attacker != player.IdentityId)
            {
                // Etap 2: silnik relacji rozlicza czyny gracza; bitwy NPC↔NPC pomijamy,
                // żeby nie zalewać mostka (frakcyjne wojny ogarnia brain od Etapu 3).
                return;
            }

            IMyCubeGrid grid = slim.CubeGrid;
            string faction = GridOwnership.NpcFactionTag(grid, player.IdentityId);
            if (faction == null)
            {
                return;
            }

            Aggregate agg;
            if (!_agg.TryGetValue(faction, out agg))
            {
                agg = new Aggregate();
                agg.FirstTick = _tick;
                _agg[faction] = agg;
            }
            agg.Damage += info.Amount;
            agg.Hits++;
            agg.Weapon = weapon != null ? weapon : info.Type.String;

            DamagedGrid damaged;
            if (!_damaged.TryGetValue(grid.EntityId, out damaged))
            {
                damaged = new DamagedGrid();
                _damaged[grid.EntityId] = damaged;
            }
            damaged.Faction = faction;
            damaged.Name = grid.DisplayName;
            damaged.LastTick = _tick;
        }

        /// <summary>Atrybucja: encja atakująca → tożsamość gracza/NPC. 0 = nie ustalono.</summary>
        private static long ResolveAttacker(long attackerEntityId, out string weapon)
        {
            weapon = null;
            if (attackerEntityId == 0)
            {
                return 0;
            }
            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(attackerEntityId, out entity) || entity == null)
            {
                return 0;
            }

            var character = entity as IMyCharacter;
            if (character != null)
            {
                return character.ControllerInfo != null ? character.ControllerInfo.ControllingIdentityId : 0;
            }

            var handheld = entity as IMyGunBaseUser; // broń ręczna i narzędzia inżyniera
            if (handheld != null)
            {
                weapon = entity.DisplayName;
                return handheld.OwnerId;
            }

            var block = entity as IMyCubeBlock; // wieżyczki i działa stałe
            if (block != null)
            {
                weapon = block.BlockDefinition.SubtypeName;
                if (block.OwnerId != 0)
                {
                    return block.OwnerId;
                }
                return FirstBigOwner(block.CubeGrid);
            }

            var grid = entity as IMyCubeGrid; // taranowanie
            if (grid != null)
            {
                return FirstBigOwner(grid);
            }

            return 0;
        }

        private static long FirstBigOwner(IMyCubeGrid grid)
        {
            if (grid == null)
            {
                return 0;
            }
            List<long> owners = grid.BigOwners;
            return owners != null && owners.Count > 0 ? owners[0] : 0;
        }

        private void FlushAged()
        {
            _keysToFlush.Clear();
            foreach (KeyValuePair<string, Aggregate> kv in _agg)
            {
                if (_tick - kv.Value.FirstTick >= FlushAfterTicks)
                {
                    _keysToFlush.Add(kv.Key);
                }
            }
            for (int i = 0; i < _keysToFlush.Count; i++)
            {
                string faction = _keysToFlush[i];
                Aggregate agg = _agg[faction];
                IMyPlayer player = MyAPIGateway.Session.Player;
                _events.WriteCombatHit(player != null ? player.IdentityId : 0,
                    faction, agg.Damage, agg.Hits, agg.Weapon != null ? agg.Weapon : "?");
                _agg.Remove(faction);
            }
        }

        private void OnEntityRemove(IMyEntity entity)
        {
            if (_disposed)
            {
                return;
            }
            var grid = entity as IMyCubeGrid;
            if (grid == null)
            {
                return;
            }
            DamagedGrid damaged;
            if (!_damaged.TryGetValue(grid.EntityId, out damaged))
            {
                return;
            }
            _damaged.Remove(grid.EntityId);
            if (_tick - damaged.LastTick <= FreshDamageTicks)
            {
                _events.WriteGridDestroyed(damaged.Faction, damaged.Name, true);
            }
        }

        private void ForgetStaleGrids()
        {
            _gridsToForget.Clear();
            foreach (KeyValuePair<long, DamagedGrid> kv in _damaged)
            {
                if (_tick - kv.Value.LastTick > FreshDamageTicks)
                {
                    _gridsToForget.Add(kv.Key);
                }
            }
            for (int i = 0; i < _gridsToForget.Count; i++)
            {
                _damaged.Remove(_gridsToForget[i]);
            }
        }
    }

    /// <summary>
    /// Etap 2 — proximity z histerezą: enter poniżej 3 km, exit powyżej 4 km
    /// (albo poza zasięgiem skanu). Skan sfery 4,5 km co 2 s, per frakcja NPC.
    /// </summary>
    internal sealed class ProximityWatcher
    {
        private const double EnterDist = 3000;
        private const double ExitDist = 4000;
        private const double ScanRadius = 4500;
        private const int ScanEveryTicks = 120; // 2 s @ 60 Hz

        private readonly EventWriter _events;
        private readonly Dictionary<string, bool> _inside = new Dictionary<string, bool>();
        private readonly Dictionary<string, double> _minDist = new Dictionary<string, double>();
        private readonly List<string> _exiting = new List<string>();
        private int _tick;

        public ProximityWatcher(EventWriter events)
        {
            _events = events;
        }

        public void Update()
        {
            _tick++;
            if (_tick % ScanEveryTicks != 0)
            {
                return;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return; // bez postaci (menu/respawn) stany zamrożone
            }
            Vector3D pos = player.Character.WorldMatrix.Translation;

            _minDist.Clear();
            BoundingSphereD sphere = new BoundingSphereD(pos, ScanRadius);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);
            for (int i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as IMyCubeGrid;
                if (grid == null)
                {
                    continue;
                }
                string faction = GridOwnership.NpcFactionTag(grid, player.IdentityId);
                if (faction == null)
                {
                    continue;
                }
                double dist = Vector3D.Distance(pos, grid.GetPosition());
                double current;
                if (!_minDist.TryGetValue(faction, out current) || dist < current)
                {
                    _minDist[faction] = dist;
                }
            }

            foreach (KeyValuePair<string, double> kv in _minDist)
            {
                bool inside;
                _inside.TryGetValue(kv.Key, out inside);
                if (!inside && kv.Value < EnterDist)
                {
                    _inside[kv.Key] = true;
                    _events.WriteProximity(kv.Key, "enter", (long)kv.Value);
                }
            }

            _exiting.Clear();
            foreach (KeyValuePair<string, bool> kv in _inside)
            {
                if (!kv.Value)
                {
                    continue;
                }
                double dist;
                if (!_minDist.TryGetValue(kv.Key, out dist) || dist > ExitDist)
                {
                    _exiting.Add(kv.Key);
                }
            }
            for (int i = 0; i < _exiting.Count; i++)
            {
                string faction = _exiting[i];
                _inside[faction] = false;
                double dist;
                long reported = _minDist.TryGetValue(faction, out dist) ? (long)dist : (long)ScanRadius;
                _events.WriteProximity(faction, "exit", reported);
            }
        }
    }
}
