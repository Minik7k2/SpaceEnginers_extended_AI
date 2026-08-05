using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>Blok ekonomiczny frakcji wraz z kontekstem, którego potrzebują kontrakty.</summary>
    internal sealed class EconomyBlock
    {
        public long BlockId;
        public long GridId;
        public string GridName;
        public bool IsContractBlock; // false = sklep (kontrakty też się na nim pokazują)
        public Vector3D Position;
    }

    /// <summary>
    /// Etap 6 — ekonomia po stronie gry. Trzy rzeczy:
    ///  - wyszukiwanie bloków ekonomicznych frakcji (sklep / blok kontraktów), bo kontrakt
    ///    trzeba wystawić NA KONKRETNYM bloku (MyContractAcquisition bierze startBlockId);
    ///  - szukanie CELÓW dla pozostałych typów kontraktów: drugiego bloku (transport),
    ///    uszkodzonej siatki (naprawa), odległej siatki (poszukiwania), tożsamości wrogiej
    ///    frakcji (nagroda za głowę) — bez celu dany typ nie ma jak powstać;
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
        /// Wszystkie siatki należące do frakcji (właściciel = BigOwners[0] w tej frakcji).
        /// Szuka po CAŁYM świecie, bo stacja frakcji nie musi być przy graczu — wołane rzadko
        /// (tylko przy tworzeniu kontraktu), więc pełny skan jest akceptowalny.
        /// factionTag == null => siatki DOWOLNEJ frakcji NPC (cel transportu, eskorty).
        /// Publiczne, bo cennik (<see cref="PriceManager"/>) też chodzi po stacjach frakcji —
        /// tyle że szuka na nich bloków sklepu, a nie celów kontraktów.
        /// </summary>
        public static List<IMyCubeGrid> FactionGrids(string factionTag)
        {
            var result = new List<IMyCubeGrid>();
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);

            foreach (IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                List<long> owners = grid.BigOwners;
                if (owners == null || owners.Count == 0)
                {
                    continue;
                }
                IMyFaction owner = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[0]);
                if (owner == null)
                {
                    continue;
                }
                if (factionTag != null && owner.Tag != factionTag)
                {
                    continue;
                }
                result.Add(grid);
            }
            return result;
        }

        /// <summary>
        /// Blok, na którym frakcja może wystawić kontrakt: najpierw blok kontraktów, w razie
        /// jego braku sklep (kontrakty pokazują się w terminalu stacji). null = frakcja nie ma
        /// gdzie wystawiać zleceń.
        /// </summary>
        public static EconomyBlock FindContractBlock(string factionTag)
        {
            if (string.IsNullOrEmpty(factionTag) ||
                MyAPIGateway.Session.Factions.TryGetFactionByTag(factionTag) == null)
            {
                return null;
            }

            EconomyBlock storeFallback = null;
            foreach (IMyCubeGrid grid in FactionGrids(factionTag))
            {
                long blockId;
                if (HasBlockOfType(grid, ContractType, out blockId))
                {
                    return Describe(grid, blockId, true); // blok kontraktów bije sklep
                }
                if (storeFallback == null && HasBlockOfType(grid, StoreType, out blockId))
                {
                    storeFallback = Describe(grid, blockId, false);
                }
            }
            return storeFallback;
        }

        /// <summary>Stary kształt wywołania (diagnostyka /zf stations) na nowym wyszukiwaniu.</summary>
        public static bool TryFindContractBlock(string factionTag, out long entityId, out string gridName)
        {
            EconomyBlock block = FindContractBlock(factionTag);
            entityId = block == null ? 0 : block.BlockId;
            gridName = block == null ? null : block.GridName;
            return block != null;
        }

        /// <summary>
        /// Cel transportu (MyContractHauling) i punkt docelowy eskorty: blok ekonomiczny na
        /// INNEJ siatce niż start. Najpierw szukamy u frakcji, która wystawia zlecenie (własna
        /// trasa między jej stacjami), potem u kogokolwiek — trasa międzyfrakcyjna też jest
        /// trasą. null = w świecie jest tylko jedna stacja z blokiem ekonomicznym.
        /// </summary>
        /// <summary>
        /// Cel transportu/eskorty. <paramref name="ownerId"/> to właściciel bloku STARTOWEGO:
        /// gra wymaga, żeby oba bloki miały TEGO SAMEGO właściciela, i inaczej odrzuca zlecenie.
        ///
        /// USTALONE Z LOGU GRY (2026-08-05). `MyContractGenerator.CreateCustomHaulingContract`:
        ///     if (startBlock != null &amp;&amp; endBlock != null &amp;&amp; endBlock.OwnerId != startBlock.OwnerId)
        ///         return MyContractCreationResults.Fail_NotAnOwnerOfBlock;
        /// a w logu SE stało wprost: „CreateCustomHaulingContract : Start and target blocks
        /// don't have the same owner." Poprzednia wersja miała fallback na siatkę DOWOLNEJ
        /// frakcji („trasa międzyfrakcyjna też jest trasą") — i to on zabijał każdy transport,
        /// bo blok innej frakcji z definicji ma innego właściciela. Zlecenie schodziło wtedy
        /// po cichu na dostawę i wyglądało to jak problem z kontem w banku.
        /// ownerId == 0 wyłącza filtr (zachowanie jak dawniej).
        /// </summary>
        public static EconomyBlock FindHaulTarget(string factionTag, long excludeGridId, long ownerId)
        {
            EconomyBlock own = FindEconomyBlockOtherThan(factionTag, excludeGridId, ownerId);
            // Fallback tylko wtedy, gdy właściciel nie jest wymagany — inaczej szukanie
            // u obcych frakcji jest z góry stratą czasu i kończy się odmową gry.
            return own ?? (ownerId != 0 ? null : FindEconomyBlockOtherThan(null, excludeGridId, 0));
        }

        private static EconomyBlock FindEconomyBlockOtherThan(string factionTag, long excludeGridId,
                                                             long ownerId)
        {
            foreach (IMyCubeGrid grid in FactionGrids(factionTag))
            {
                if (grid.EntityId == excludeGridId)
                {
                    continue;
                }
                long blockId;
                if (HasBlockOfType(grid, ContractType, out blockId) &&
                    (ownerId == 0 || BlockOwner(blockId) == ownerId))
                {
                    return Describe(grid, blockId, true);
                }
                if (HasBlockOfType(grid, StoreType, out blockId) &&
                    (ownerId == 0 || BlockOwner(blockId) == ownerId))
                {
                    return Describe(grid, blockId, false);
                }
            }
            return null;
        }

        /// <summary>
        /// Pierwsza stacja vanillowej ekonomii należąca do tej frakcji (0 = frakcja nie ma
        /// żadnej). To NIE jest nasza stacja ze StationSpawnera — to wpis w
        /// <c>IMyFaction.Stations</c>, który gra rozpoznaje jako punkt docelowy zlecenia.
        /// Używa tego transport, gdy nie ma drugiego bloku tego samego właściciela.
        /// </summary>
        public static long FirstFactionStationId(string factionTag)
        {
            IMyFaction faction = string.IsNullOrEmpty(factionTag)
                ? null
                : MyAPIGateway.Session.Factions.TryGetFactionByTag(factionTag);
            // faction.Stations to DictionaryValuesReader (struktura), więc porównanie z null
            // się nie kompiluje — wystarczy sprawdzić samą frakcję.
            if (faction == null)
            {
                return 0;
            }
            foreach (IMyFactionStation station in faction.Stations)
            {
                if (station.Id != 0)
                {
                    return station.Id;
                }
            }
            return 0;
        }

        /// <summary>Właściciel bloku po jego EntityId (0 = nie znaleziono albo niczyj).</summary>
        public static long BlockOwner(long blockId)
        {
            var block = MyAPIGateway.Entities.GetEntityById(blockId) as IMyCubeBlock;
            return block == null ? 0 : block.OwnerId;
        }

        /// <summary>
        /// Cel naprawy (MyContractRepair): siatka frakcji z największą liczbą niepełnych
        /// bloków. Liczymy Integrity &lt; MaxIntegrity, więc łapiemy i zniszczone, i
        /// niedospawowane — spawarka jest potrzebna w obu przypadkach. false = wszystko całe.
        /// </summary>
        public static bool TryFindDamagedGrid(string factionTag, out long gridId, out string gridName)
        {
            gridId = 0;
            gridName = null;
            int bestDamaged = 0;

            var blocks = new List<IMySlimBlock>();
            foreach (IMyCubeGrid grid in FactionGrids(factionTag))
            {
                blocks.Clear();
                grid.GetBlocks(blocks);
                int damaged = 0;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i].Integrity < blocks[i].MaxIntegrity)
                    {
                        damaged++;
                    }
                }
                if (damaged > bestDamaged)
                {
                    bestDamaged = damaged;
                    gridId = grid.EntityId;
                    gridName = grid.DisplayName;
                }
            }
            return bestDamaged > 0;
        }

        /// <summary>
        /// Cel poszukiwań (MyContractSearch): REKWIZYT postawiony przez frakcję, rozpoznawany
        /// po nazwie z prefabu, dalej niż minMeters od gracza. Szukamy NAJBLIŻSZEGO takiego
        /// (poza progiem), żeby recyklingować moduły zgubione przy wcześniejszych zleceniach,
        /// zamiast zaśmiecać świat nowymi. Zwykłe siatki frakcji celowo NIE wchodzą w grę:
        /// zlecenie „znajdź" każe przywieźć grid pod stację, a stacji nikt nie przywiezie.
        /// </summary>
        public static bool TryFindProp(string namePrefix, Vector3D from, double minMeters,
                                       out long gridId, out string gridName)
        {
            gridId = 0;
            gridName = null;
            double bestDist = double.MaxValue;

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            foreach (IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.DisplayName == null ||
                    !grid.DisplayName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                double dist = Vector3D.Distance(from, grid.GetPosition());
                if (dist >= minMeters && dist < bestDist)
                {
                    bestDist = dist;
                    gridId = grid.EntityId;
                    gridName = grid.DisplayName;
                }
            }
            return gridId != 0;
        }

        /// <summary>
        /// Tożsamość, na którą można wystawić nagrodę za głowę (MyContractBounty bierze
        /// targetIdentityId). Najpierw właściciel prawdziwej siatki tej frakcji (żywy cel
        /// w świecie), w razie jego braku założyciel frakcji. 0 = frakcji nie ma w grze.
        /// </summary>
        public static long FindTargetIdentity(string factionTag, out string gridName)
        {
            gridName = null;
            if (string.IsNullOrEmpty(factionTag))
            {
                return 0;
            }
            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(factionTag);
            if (faction == null)
            {
                return 0;
            }

            foreach (IMyCubeGrid grid in FactionGrids(factionTag))
            {
                List<long> owners = grid.BigOwners;
                if (owners != null && owners.Count > 0 && owners[0] != 0)
                {
                    gridName = grid.DisplayName;
                    return owners[0];
                }
            }
            return faction.FounderId;
        }

        private static EconomyBlock Describe(IMyCubeGrid grid, long blockId, bool isContractBlock)
        {
            return new EconomyBlock
            {
                BlockId = blockId,
                GridId = grid.EntityId,
                GridName = grid.DisplayName,
                IsContractBlock = isContractBlock,
                Position = grid.GetPosition(),
            };
        }
    }

    /// <summary>
    /// Kasa frakcji NPC — już tylko do podglądu (`/zf stations`).
    /// UWAGA na pułapkę, która kosztowała nas pół sesji testów: konto FRAKCJI
    /// (<c>MyBankingSystem</c> pod <c>FactionId</c>, czyli to, co czyta
    /// <c>IMyFaction.TryGetBalanceInfo</c>) to NIE jest konto, z którego gra opłaca kontrakty.
    /// Kontrakt sprawdza i obciąża konto WŁAŚCICIELA BLOKU (<c>startBlock.OwnerId</c> —
    /// tożsamość założyciela). Dosypywanie tutaj nie odblokuje zleceń; robi to
    /// <see cref="ContractManager.Create"/> tuż przed <c>AddContract</c>.
    /// </summary>
    internal static class FactionFunds
    {
        public static long Balance(IMyFaction faction)
        {
            long balance;
            return faction != null && faction.TryGetBalanceInfo(out balance) ? balance : -1;
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
