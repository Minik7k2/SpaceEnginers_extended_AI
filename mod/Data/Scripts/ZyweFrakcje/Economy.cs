using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 6 — ekonomia po stronie gry. Dwie rzeczy:
    ///  - wyszukiwanie bloków ekonomicznych frakcji (sklep / blok kontraktów), bo kontrakt
    ///    trzeba wystawić NA KONKRETNYM bloku (MyContractAcquisition bierze startBlockId);
    ///  - wykrywanie handlu gracza z frakcją (patrz <see cref="TradeWatcher"/>).
    /// Bloki rozpoznajemy po TypeIdString definicji, żeby nie zależeć od interfejsów
    /// spoza whitelisty ModAPI (IMyStoreBlock żyje w Sandbox.ModAPI.Ingame).
    /// </summary>
    internal static class FactionEconomy
    {
        public const string StoreType = "StoreBlock";
        public const string ContractType = "ContractBlock";

        private const double NearStoreMeters = 300; // „stoję przy stacji handlowej frakcji"

        /// <summary>Czy siatka ma blok danego typu (podciąg TypeIdString definicji).</summary>
        public static bool HasBlockOfType(IMyCubeGrid grid, string typeSubstring, out long entityId)
        {
            entityId = 0;
            if (grid == null)
            {
                return false;
            }
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            for (int i = 0; i < blocks.Count; i++)
            {
                IMyCubeBlock fat = blocks[i].FatBlock;
                if (fat == null)
                {
                    continue;
                }
                string typeId = fat.BlockDefinition.TypeIdString;
                if (typeId != null && typeId.IndexOf(typeSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    entityId = fat.EntityId;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Frakcja NPC, z której sklepem gracz właśnie stoi (najbliższa siatka frakcji ze
        /// sklepem w promieniu 300 m). null = nie ma w pobliżu żadnego sklepu frakcji, więc
        /// zmiana salda gracza NIE jest handlem z nami.
        /// </summary>
        public static string NearestStoreFaction(IMyPlayer player)
        {
            if (player == null)
            {
                return null;
            }
            Vector3D pos = player.GetPosition();
            BoundingSphereD sphere = new BoundingSphereD(pos, NearStoreMeters);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetTopMostEntitiesInSphere(ref sphere);

            string best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < entities.Count; i++)
            {
                var grid = entities[i] as IMyCubeGrid;
                if (grid == null)
                {
                    continue;
                }
                string tag = GridOwnership.NpcFactionTag(grid, player.IdentityId);
                if (tag == null)
                {
                    continue;
                }
                double dist = Vector3D.Distance(pos, grid.GetPosition());
                if (dist >= bestDist)
                {
                    continue;
                }
                long ignored;
                if (HasBlockOfType(grid, StoreType, out ignored))
                {
                    best = tag;
                    bestDist = dist;
                }
            }
            return best;
        }

        /// <summary>
        /// Blok, na którym frakcja może wystawić kontrakt: najpierw blok kontraktów, w razie
        /// jego braku sklep (kontrakty pokazują się w terminalu stacji). Szuka po CAŁYM świecie,
        /// bo stacja frakcji nie musi być przy graczu — wołane rzadko (tylko przy tworzeniu
        /// kontraktu), więc pełny skan jest akceptowalny.
        /// </summary>
        public static bool TryFindContractBlock(string factionTag, out long entityId, out string gridName)
        {
            entityId = 0;
            gridName = null;
            if (string.IsNullOrEmpty(factionTag))
            {
                return false;
            }

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(factionTag);
            if (faction == null)
            {
                return false;
            }

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);

            long storeFallbackId = 0;
            string storeFallbackName = null;
            foreach (IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null)
                {
                    continue;
                }
                List<long> owners = grid.BigOwners;
                if (owners == null || owners.Count == 0)
                {
                    continue;
                }
                IMyFaction owner = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[0]);
                if (owner == null || owner.Tag != factionTag)
                {
                    continue;
                }

                long blockId;
                if (HasBlockOfType(grid, ContractType, out blockId))
                {
                    entityId = blockId;
                    gridName = grid.DisplayName;
                    return true; // blok kontraktów bije sklep
                }
                if (storeFallbackId == 0 && HasBlockOfType(grid, StoreType, out blockId))
                {
                    storeFallbackId = blockId;
                    storeFallbackName = grid.DisplayName;
                }
            }

            if (storeFallbackId != 0)
            {
                entityId = storeFallbackId;
                gridName = storeFallbackName;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Wykrywanie handlu gracza z frakcją (Etap 6). ModAPI NIE daje zdarzenia transakcji
    /// (IMyStoreBlock ma tylko Insert/Cancel/GetPlayerStoreItems), więc jedziemy heurystyką:
    /// zmiana salda gracza + sklep frakcji NPC w promieniu 300 m = handel z tą frakcją.
    /// Saldo spada => gracz kupił (buy), rośnie => sprzedał (sell); wartość to |delta|.
    ///
    /// Zmiany salda z NASZYCH mechanik (okup, nagroda za kontrakt) muszą omijać ten kanał —
    /// stąd <see cref="Resync"/>, które przestawia punkt odniesienia bez zgłaszania handlu.
    /// </summary>
    internal sealed class TradeWatcher
    {
        private const int PollEveryTicks = 120; // ~2 s przy 60 Hz
        private const long MinDeltaCredits = 10; // ignoruj grosze i zaokrąglenia

        // Okno wyciszenia po własnym przelewie. Nagroda za kontrakt bywa księgowana
        // kilka sekund po zdarzeniu i to DOKŁADNIE przy stacji frakcji — czyli w miejscu,
        // gdzie heurystyka handlu jest najczulsza. Jeden odczyt to za mało, stąd okno.
        private const int SuppressTicks = 600; // ~10 s

        private readonly EventWriter _events;
        private long _balance;
        private bool _hasBaseline;
        private int _tick;
        private int _suppressUntilTick;

        public TradeWatcher(EventWriter events)
        {
            _events = events;
        }

        public void Update()
        {
            _tick++;
            if (_tick % PollEveryTicks != 0)
            {
                return;
            }

            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null)
            {
                return;
            }
            long balance;
            if (!player.TryGetBalanceInfo(out balance))
            {
                return;
            }

            if (_tick <= _suppressUntilTick)
            {
                _balance = balance; // okup / nagroda za kontrakt — tylko przesuwamy odniesienie
                _hasBaseline = true;
                return;
            }

            if (!_hasBaseline)
            {
                _balance = balance;
                _hasBaseline = true;
                return; // pierwszy odczyt (albo po Resync) tylko ustawia punkt odniesienia
            }

            long delta = balance - _balance;
            _balance = balance;
            if (delta == 0)
            {
                return;
            }
            long value = delta < 0 ? -delta : delta;
            if (value < MinDeltaCredits)
            {
                return;
            }

            // Bez sklepu frakcji w pobliżu to nie jest handel z nami (nagroda za misję,
            // konsola admina, sprzedaż na stacji vanilla) — nie ruszamy relacji.
            string faction = FactionEconomy.NearestStoreFaction(player);
            if (faction == null)
            {
                return;
            }

            _events.WriteTrade(faction, delta < 0 ? "buy" : "sell", value);
            MyAPIGateway.Utilities.ShowMessage("ZF", "Handel z " + faction + ": " + value + " kr");
        }

        /// <summary>
        /// Wycisz wykrywanie handlu na ~10 s i przyjmij bieżące saldo za nowe odniesienie.
        /// Wołane po okupie i po rozliczeniu kontraktu, żeby nasze własne przelewy nie
        /// wyglądały jak handel ze sklepem.
        /// </summary>
        public void Suppress()
        {
            _suppressUntilTick = _tick + SuppressTicks;
            _hasBaseline = false;
        }
    }
}
