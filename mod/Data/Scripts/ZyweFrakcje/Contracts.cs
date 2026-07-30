using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Contracts;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 6 — kontrakty. Brain decyduje KIEDY, JAKIEGO TYPU i ZA ILE (commands.jsonl:
    /// contract_create), mod tworzy kontrakt w grze przez MyAPIGateway.ContractSystem
    /// i odsyła prawdziwe ID (events.jsonl: contract_created). Rozliczenie wraca jako
    /// contract_done.
    ///
    /// Typy zleceń = klasy z Sandbox.ModAPI.Contracts (każda ma inny konstruktor i inny
    /// CEL, którego trzeba poszukać w świecie — patrz Economy.cs):
    ///   dostawa      MyContractAcquisition  towar na blok frakcji
    ///   nagroda      MyContractBounty       tożsamość pilota wrogiej frakcji
    ///   transport    MyContractHauling      drugi blok ekonomiczny
    ///   naprawa      MyContractRepair       uszkodzona siatka frakcji
    ///   poszukiwania MyContractSearch       odległa siatka + promień
    ///   eskorta      MyContractEscort       trasa (dwa punkty) + właściciel konwoju
    ///   wlasne       MyContractCustom       definicja z mod/Data/ContractTypes.sbc
    /// Gdy celu nie ma w świecie (albo gra odrzuci kontrakt), schodzimy na DOSTAWĘ i to
    /// ona wraca w contract_created — brain utrwala typ, który naprawdę powstał, nie ten,
    /// o który prosił. Bez tego zlecenie po prostu przepadałoby bez śladu.
    ///
    /// Kontrakt powstaje na bloku kontraktów (albo sklepie) NALEŻĄCYM DO FRAKCJI — patrz
    /// <see cref="FactionEconomy.FindContractBlock"/>. Bez takiego bloku nie ma gdzie go
    /// wystawić i mówimy o tym wprost na czacie (to najczęstsza przyczyna „nie działa").
    ///
    /// Wykrywanie końca kontraktu ma DWIE drogi, bo delegaty nie przeżywają zapisu świata:
    ///  1. callbacki OnContractSucceeded/OnContractFailed — bieżąca sesja, natychmiast;
    ///  2. odpytywanie stanu (GetContractState) co ~5 s — po wczytaniu świata, gdy
    ///     callbacków już nie ma, a lista ID wraca z pliku stanu moda.
    /// </summary>
    internal sealed class ContractManager
    {
        private const int PollEveryTicks = 300; // ~5 s przy 60 Hz
        private const string StateFile = "contracts_mod_state.txt";

        // Poszukiwania: cel musi być dalej niż to od gracza (inaczej zlecenie „znajdź"
        // dotyczyłoby czegoś, na co gracz właśnie patrzy), a „znalezione" liczy się
        // w tym promieniu od celu.
        private const double SearchMinMeters = 5000;
        private const double SearchRadiusMeters = 2000;

        // Rekwizyty zleceń (mod/Data/Prefabs/ZF_ContractProps.sbc). Frakcja sama
        // przygotowuje sobie robotę: gubi moduł albo zostawia uszkodzony wrak. Dzięki temu
        // „poszukiwania" i „naprawa" nie zależą od tego, czy w świecie przypadkiem stoi
        // coś nadającego się na cel. Celem poszukiwań jest WYŁĄCZNIE nasz moduł — stacji
        // (którą wcześniej mógł wskazać wyszukiwacz) nie da się przywieźć pod stację.
        private const string PropSearchPrefab = "ZF_Zgubka";
        private const string PropSearchName = "Zgubiony modul"; // DisplayName z prefabu
        private const string PropWreckPrefab = "ZF_Wrak";
        private const double PropSearchMeters = 8000;  // gdzie frakcja gubi moduł (od gracza)
        private const double PropWreckMeters = 2500;   // wrak zostawiamy przy stacji frakcji
        private const float PropFreeRadius = 50;
        // Eskorta bez drugiej stacji w świecie: trasa prowadzi tyle metrów w stronę gracza.
        private const double EscortMeters = 20000;
        // Definicja własnego typu zlecenia (mod/Data/ContractTypes.sbc). Trzymana jako TEKST
        // i rozwijana przez MyDefinitionId.TryParse, żeby mod nie zależał od typu
        // MyObjectBuilder_ContractTypeDefinition (nie ma go w whiteliście ModAPI).
        // TryParse pilnuje tylko TYPU — brak samego podtypu wyjdzie dopiero na AddContract,
        // i wtedy też schodzimy na dostawę.
        private const string CustomContractDefinition =
            "MyObjectBuilder_ContractTypeDefinition/ZF_Zlecenie";

        private sealed class Tracked
        {
            public long Id;
            public string Faction;
            public string Kind;
        }

        private readonly EventWriter _events;
        private readonly List<Tracked> _tracked = new List<Tracked>();
        private readonly Type _owner;
        private readonly TradeWatcher _trade; // wyciszenie heurystyki handlu przy wypłacie nagrody
        private int _tick;

        public ContractManager(Type owner, EventWriter events, TradeWatcher trade)
        {
            _owner = owner;
            _events = events;
            _trade = trade;
            LoadState();
        }

        /// <summary>
        /// Co frakcja chce dostać (kontrakt typu „zdobądź i dostarcz"). Ilości dobrane tak,
        /// żeby dało się je wykonać wieczorem gry, a nie w tydzień. Zmiana tych trzech linii
        /// = zmiana charakteru zleceń każdej frakcji.
        /// </summary>
        private static void ItemForFaction(string faction, out MyDefinitionId itemId, out int amount,
                                           out string opis)
        {
            switch (faction)
            {
                case "HEL": // korporacja: elektronika do fabryk
                    itemId = new MyDefinitionId(typeof(MyObjectBuilder_Component), "Computer");
                    amount = 150;
                    opis = "dostawa 150 komputerów";
                    return;
                case "KRW": // piraci: kruszec, nie pytają skąd
                    itemId = new MyDefinitionId(typeof(MyObjectBuilder_Ingot), "Platinum");
                    amount = 20;
                    opis = "dostawa 20 sztabek platyny";
                    return;
                default: // WGR i reszta: stal na obudowy szybów
                    itemId = new MyDefinitionId(typeof(MyObjectBuilder_Component), "SteelPlate");
                    amount = 600;
                    opis = "dostawa 600 płyt stalowych";
                    return;
            }
        }

        /// <summary>
        /// Tworzy kontrakt frakcji w grze. reward w kredytach, duration w minutach,
        /// targetFaction ma znaczenie tylko dla typu "nagroda". Kaucja (collateral) to
        /// 1/10 nagrody — świat mściwy: zawalone zlecenie ma boleć nie tylko relacją.
        /// </summary>
        public void Create(string faction, string kind, long reward, int durationMin, string targetFaction)
        {
            if (MyAPIGateway.ContractSystem == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Kontrakty niedostępne w tej wersji gry (brak ContractSystem)");
                return;
            }

            EconomyBlock start = FactionEconomy.FindContractBlock(faction);
            if (start == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Kontrakt " + faction + " pominięty: frakcja nie ma bloku kontraktów ani sklepu (postaw stację frakcji)");
                return;
            }

            int money = reward > int.MaxValue ? int.MaxValue : (int)reward;
            int collateral = money / 10;
            int durationSeconds = durationMin * 60; // API bierze sekundy

            // Typy z rekwizytem: jeśli w świecie nie ma jeszcze celu, frakcja go najpierw
            // STAWIA, a kontrakt powstaje w callbacku spawnu (SpawnPrefab jest asynchroniczny).
            // Po spawnie cel znajdą te same wyszukiwarki co zwykle — rekwizyt należy do
            // frakcji, więc jest jej uszkodzoną/zgubioną własnością.
            long ignoredId;
            string ignoredName;
            if (kind == "poszukiwania" &&
                !FactionEconomy.TryFindProp(PropSearchName, PlayerPosition(start.Position),
                                            SearchMinMeters, out ignoredId, out ignoredName))
            {
                SpawnProp(PropSearchPrefab, faction,
                          OffsetFrom(PlayerPosition(start.Position), PropSearchMeters),
                          () => Finalize(faction, kind, reward, targetFaction, start,
                                         money, collateral, durationSeconds));
                return;
            }
            if (kind == "naprawa" &&
                !FactionEconomy.TryFindDamagedGrid(faction, out ignoredId, out ignoredName))
            {
                SpawnProp(PropWreckPrefab, faction, OffsetFrom(start.Position, PropWreckMeters),
                          () => Finalize(faction, kind, reward, targetFaction, start,
                                         money, collateral, durationSeconds));
                return;
            }

            Finalize(faction, kind, reward, targetFaction, start, money, collateral,
                     durationSeconds);
        }

        /// <summary>
        /// Wystawienie kontraktu, gdy cel już jest w świecie (albo właśnie go postawiliśmy).
        /// Stąd idzie łańcuch fallbacku: typ bez celu → dostawa.
        /// </summary>
        private void Finalize(string faction, string kind, long reward, string targetFaction,
                              EconomyBlock start, int money, int collateral, int durationSeconds)
        {
            // ID kontraktu znamy dopiero PO AddContract, a callbacki trzeba ustawić WCZEŚNIEJ —
            // stąd jednoelementowa tablica jako uchwyt domknięcia.
            long[] idBox = new long[1];

            string actualKind = string.IsNullOrEmpty(kind) ? "dostawa" : kind;
            long contractId;
            string opis;
            string powod;
            if (!TryAddOfKind(faction, actualKind, start, money, collateral, durationSeconds,
                              targetFaction, idBox, out contractId, out opis, out powod))
            {
                if (actualKind == "dostawa")
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Kontrakt " + faction + " pominięty: " + powod);
                    return;
                }
                // Typ nie ma celu w świecie (albo gra go odrzuciła) — zamiast gubić zlecenie
                // wystawiamy dostawę i mówimy dlaczego.
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Zlecenie " + faction + " typu \"" + actualKind + "\" niemożliwe (" + powod +
                    ") — wystawiam dostawę");
                actualKind = "dostawa";
                if (!TryAddOfKind(faction, actualKind, start, money, collateral, durationSeconds,
                                  targetFaction, idBox, out contractId, out opis, out powod))
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Kontrakt " + faction + " pominięty: " + powod);
                    return;
                }
            }
            idBox[0] = contractId;

            var tracked = new Tracked { Id = contractId, Faction = faction, Kind = actualKind };
            _tracked.Add(tracked);
            SaveState();

            _events.WriteContractCreated(contractId.ToString(), faction, actualKind, reward, opis,
                                         actualKind == "nagroda" ? targetFaction : null);
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "Nowe zlecenie " + faction + " (" + actualKind + "): " + opis + " za " + reward +
                " kr (" + (start.GridName ?? "stacja") + ")");
        }

        /// <summary>
        /// Buduje i dodaje kontrakt danego typu. false = typ niewykonalny (brak celu w świecie
        /// albo gra odrzuciła zlecenie); wtedy <paramref name="powod"/> mówi dlaczego, żeby
        /// gracz nie zgadywał, a wołający mógł zejść na dostawę.
        ///
        /// Każdy typ ma osobne wywołanie AddContract, bo klasy kontraktów NIE mają wspólnej
        /// klasy bazowej w ModAPI — nie da się ich trzymać w jednej zmiennej.
        /// </summary>
        private bool TryAddOfKind(string faction, string kind, EconomyBlock start, int money,
                                  int collateral, int durationSeconds, string targetFaction,
                                  long[] idBox, out long contractId, out string opis, out string powod)
        {
            contractId = 0;
            opis = null;
            powod = null;
            Action onSuccess = () => Finish(idBox[0], true);
            Action onFail = () => Finish(idBox[0], false);
            // Moment przyjęcia zlecenia przez gracza — stąd rusza reakcja świata (Taken).
            Action<long> onTaken = identity => Taken(idBox[0]);

            switch (kind)
            {
                case "nagroda":
                {
                    if (string.IsNullOrEmpty(targetFaction))
                    {
                        powod = "brain nie podał celu nagrody";
                        return false;
                    }
                    string targetGrid;
                    long identity = FactionEconomy.FindTargetIdentity(targetFaction, out targetGrid);
                    if (identity == 0)
                    {
                        powod = "frakcji " + targetFaction + " nie ma w tym świecie";
                        return false;
                    }
                    var c = new MyContractBounty(start.BlockId, money, collateral, durationSeconds, identity);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = "nagroda za głowę pilota " + targetFaction +
                           (targetGrid == null ? "" : " (" + targetGrid + ")");
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                case "transport":
                {
                    EconomyBlock target = FactionEconomy.FindHaulTarget(faction, start.GridId);
                    if (target == null)
                    {
                        powod = "w świecie nie ma drugiej stacji z blokiem kontraktów/sklepem";
                        return false;
                    }
                    var c = new MyContractHauling(start.BlockId, money, collateral, durationSeconds,
                                                  target.BlockId);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = "transport ładunku do " + (target.GridName ?? "innej stacji");
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                case "naprawa":
                {
                    long gridId;
                    string gridName;
                    if (!FactionEconomy.TryFindDamagedGrid(faction, out gridId, out gridName))
                    {
                        powod = "frakcja nie ma uszkodzonej siatki do naprawy";
                        return false;
                    }
                    var c = new MyContractRepair(start.BlockId, money, collateral, durationSeconds, gridId);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = "naprawa " + (gridName ?? "siatki frakcji");
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                case "poszukiwania":
                {
                    // Celem jest WYŁĄCZNIE nasz zgubiony moduł — vanillowe poszukiwania każą
                    // przywieźć znaleziony grid pod stację, a stacji nikt nie przywiezie.
                    long gridId;
                    string gridName;
                    if (!FactionEconomy.TryFindProp(PropSearchName, PlayerPosition(start.Position),
                                                    SearchMinMeters, out gridId, out gridName))
                    {
                        powod = "nie ma zgubionego modułu dalej niż " + (int)(SearchMinMeters / 1000) +
                                " km od gracza";
                        return false;
                    }
                    var c = new MyContractSearch(start.BlockId, money, collateral, durationSeconds,
                                                 gridId, SearchRadiusMeters);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = "odnalezienie " + (gridName ?? "zaginionej siatki");
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                case "eskorta":
                {
                    string ignored;
                    long owner = FactionEconomy.FindTargetIdentity(faction, out ignored);
                    if (owner == 0)
                    {
                        powod = "frakcja nie ma tożsamości właściciela konwoju";
                        return false;
                    }
                    // Trasa: ze stacji frakcji do drugiej stacji, a gdy jej nie ma — 20 km
                    // w stronę gracza (żeby konwój dało się w ogóle spotkać).
                    EconomyBlock target = FactionEconomy.FindHaulTarget(faction, start.GridId);
                    Vector3D end;
                    string gdzie;
                    if (target != null)
                    {
                        end = target.Position;
                        gdzie = target.GridName ?? "innej stacji";
                    }
                    else
                    {
                        Vector3D player = PlayerPosition(start.Position);
                        Vector3D dir = player - start.Position;
                        dir = dir.LengthSquared() > 1 ? Vector3D.Normalize(dir) : Vector3D.Right;
                        end = start.Position + dir * EscortMeters;
                        gdzie = "punktu spotkania " + (int)(EscortMeters / 1000) + " km od stacji";
                    }
                    var c = new MyContractEscort(start.BlockId, money, collateral, durationSeconds,
                                                 start.Position, end, owner);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = "eskorta konwoju do " + gdzie;
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                case "wlasne":
                {
                    MyDefinitionId definitionId;
                    if (!MyDefinitionId.TryParse(CustomContractDefinition, out definitionId))
                    {
                        powod = "gra nie zna typu " + CustomContractDefinition;
                        return false;
                    }
                    EconomyBlock target = FactionEconomy.FindHaulTarget(faction, start.GridId);
                    string nazwa;
                    string opisPelny;
                    CustomTextForFaction(faction, out nazwa, out opisPelny);
                    // reputationReward/failReputationPrice = 0: reputację prowadzi NASZ silnik
                    // relacji (hybryda, patrz Reputation.cs) — gra nie ma jej ruszać drugi raz.
                    var c = new MyContractCustom(definitionId, start.BlockId, money, collateral,
                                                 durationSeconds, nazwa, opisPelny, 0, 0,
                                                 target == null ? (long?)null : target.BlockId);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = nazwa;
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }

                default:
                {
                    MyDefinitionId itemId;
                    int amount;
                    string itemOpis;
                    ItemForFaction(faction, out itemId, out amount, out itemOpis);
                    var c = new MyContractAcquisition(start.BlockId, money, collateral, durationSeconds,
                                                      start.BlockId, itemId, amount);
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
                    opis = itemOpis;
                    return Added(MyAPIGateway.ContractSystem.AddContract(c), faction, out contractId, out powod);
                }
            }
        }

        /// <summary>Wynik AddContract na nasze out-paramy (gra potrafi odrzucić zlecenie bez podania powodu).</summary>
        private static bool Added(MyAddContractResultWrapper result, string faction, out long contractId,
                                  out string powod)
        {
            if (!result.Success)
            {
                contractId = 0;
                powod = "gra odrzuciła kontrakt frakcji " + faction;
                return false;
            }
            contractId = result.ContractId;
            powod = null;
            return true;
        }

        /// <summary>Nazwa i opis własnego zlecenia — jedyne miejsce, gdzie frakcja mówi w kontrakcie własnym głosem.</summary>
        private static void CustomTextForFaction(string faction, out string nazwa, out string opis)
        {
            switch (faction)
            {
                case "HEL":
                    nazwa = "Zlecenie Korporacji Helion";
                    opis = "Ładunek priorytetowy. Ma dotrzeć w terminie, bez pytań i bez opóźnień.";
                    return;
                case "KRW":
                    nazwa = "Kontrabanda Krwawej Ręki";
                    opis = "Towar jedzie tam, gdzie każemy. Nie zaglądasz do skrzyń.";
                    return;
                default:
                    nazwa = "Zlecenie Wolnych Górników";
                    opis = "Ruda musi dojechać do odbiorcy, inaczej szyb stoi.";
                    return;
            }
        }

        /// <summary>Pozycja gracza, a gdy go nie ma (świat bez gracza) — punkt zapasowy.</summary>
        private static Vector3D PlayerPosition(Vector3D fallback)
        {
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            return player == null ? fallback : player.GetPosition();
        }

        /// <summary>
        /// Punkt oddalony o <paramref name="meters"/> w pseudolosowym kierunku, z korektą na
        /// wolne miejsce. Bez System.Random (pewność whitelisty ModAPI, tak jak w Garble) —
        /// kierunek bierzemy z zegara, więc kolejne zlecenia nie lądują w tym samym miejscu.
        /// </summary>
        private static Vector3D OffsetFrom(Vector3D origin, double meters)
        {
            long ticks = DateTime.UtcNow.Ticks;
            var dir = new Vector3D(((ticks >> 3) & 255) - 127.5,
                                   ((ticks >> 11) & 255) - 127.5,
                                   ((ticks >> 19) & 255) - 127.5);
            dir = dir.LengthSquared() > 1 ? Vector3D.Normalize(dir) : Vector3D.Forward;
            Vector3D wanted = origin + dir * meters;
            Vector3D? free = MyAPIGateway.Entities.FindFreePlace(wanted, PropFreeRadius);
            return free.HasValue ? free.Value : wanted;
        }

        /// <summary>
        /// Stawia rekwizyt zlecenia i dopiero potem (callback SpawnPrefab jest asynchroniczny)
        /// tworzy kontrakt. Rekwizyt dostaje właściciela = frakcja wystawiająca: to jej
        /// zgubiony moduł / jej awaria, a przy okazji własność chroni grid przed sprzątaczem
        /// śmieci SE. Gdy spawn się nie uda, i tak wołamy dalej — wyszukiwarka nie znajdzie
        /// celu, więc zadziała normalny fallback na dostawę z czytelnym powodem.
        /// </summary>
        private void SpawnProp(string prefab, string faction, Vector3D pos, Action onDone)
        {
            string ignored;
            long owner = FactionEconomy.FindTargetIdentity(faction, out ignored);
            MatrixD m = MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up);
            var result = new List<IMyCubeGrid>();
            MyAPIGateway.PrefabManager.SpawnPrefab(
                result,
                prefab,
                pos,
                (Vector3)m.Forward,
                (Vector3)m.Up,
                Vector3.Zero,
                Vector3.Zero,
                null,
                SpawningOptions.None,
                owner,
                true,
                () =>
                {
                    if (result.Count == 0)
                    {
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            "Rekwizyt zlecenia " + faction + " nie powstał (prefab " + prefab + "?)");
                    }
                    else if (owner != 0)
                    {
                        // Bezpiecznik: prefab mógł przyjść bez właściciela mimo ownerId.
                        result[0].ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
                    }
                    onDone();
                });
        }

        /// <summary>
        /// Gracz PRZYJĄŁ zlecenie w terminalu (OnContractAcquired). To moment, w którym świat
        /// ma zareagować: brain wysyła konwój do eskorty, ochronę dla celu nagrody i odejmuje
        /// zaufanie u wrogów wystawcy. Bez tego kontrakt był martwym wpisem w terminalu.
        /// </summary>
        private void Taken(long contractId)
        {
            int index = IndexOf(contractId);
            if (index < 0)
            {
                return; // nie nasze zlecenie albo już rozliczone
            }
            _events.WriteContractTaken(contractId.ToString(), _tracked[index].Faction,
                                       _tracked[index].Kind);
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "Zlecenie " + _tracked[index].Faction + " przyjęte (" + _tracked[index].Kind + ")");
        }

        /// <summary>Woła sesja co tik: dopytanie o stan kontraktów (droga nr 2, po wczytaniu świata).</summary>
        public void Update()
        {
            _tick++;
            if (_tick % PollEveryTicks != 0 || _tracked.Count == 0 || MyAPIGateway.ContractSystem == null)
            {
                return;
            }

            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                MyCustomContractStateEnum state = MyAPIGateway.ContractSystem.GetContractState(_tracked[i].Id);
                if (state == MyCustomContractStateEnum.Finished)
                {
                    Finish(_tracked[i].Id, true);
                }
                else if (state == MyCustomContractStateEnum.Failed)
                {
                    Finish(_tracked[i].Id, false);
                }
                else if (state == MyCustomContractStateEnum.Disposed)
                {
                    // Kontrakt zniknął ze świata (wygasł, stacja przepadła) — przestajemy go
                    // pilnować, ale NIE zgłaszamy porażki: gracz nic nie zawalił.
                    _tracked.RemoveAt(i);
                    SaveState();
                }
            }
        }

        /// <summary>Rozliczenie: jedno zdarzenie na kontrakt, potem znika ze śledzenia.</summary>
        private void Finish(long contractId, bool success)
        {
            int index = IndexOf(contractId);
            if (index < 0)
            {
                return; // już rozliczony (callback i odpytywanie mogą trafić w to samo)
            }
            string faction = _tracked[index].Faction;
            _tracked.RemoveAt(index);
            SaveState();

            // Nagroda (albo przepadek kaucji) wpada na konto gracza przy stacji frakcji —
            // bez tego heurystyka handlu wzięłaby to za zakup i drugi raz ruszyła relację.
            if (_trade != null)
            {
                _trade.Suppress();
            }

            _events.WriteContractDone(contractId.ToString(), faction, success);
            MyAPIGateway.Utilities.ShowMessage("ZF",
                success ? "Zlecenie " + faction + " wykonane" : "Zlecenie " + faction + " zawalone");
        }

        private int IndexOf(long contractId)
        {
            for (int i = 0; i < _tracked.Count; i++)
            {
                if (_tracked[i].Id == contractId)
                {
                    return i;
                }
            }
            return -1;
        }

        // --- Trwałość: ID kontraktów muszą przeżyć wczytanie świata (CLAUDE.md) ---
        // Brain trzyma je w SQLite (przypisanie do frakcji), mod w swoim storage (co pilnować).

        private void LoadState()
        {
            if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(StateFile, _owner))
            {
                return;
            }
            string content;
            using (System.IO.TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(StateFile, _owner))
            {
                content = reader.ReadToEnd();
            }
            foreach (string rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('\t');
                if (parts.Length != 3)
                {
                    continue;
                }
                long id;
                if (long.TryParse(parts[0], out id))
                {
                    _tracked.Add(new Tracked { Id = id, Faction = parts[1], Kind = parts[2] });
                }
            }
        }

        private void SaveState()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _tracked.Count; i++)
            {
                sb.Append(_tracked[i].Id).Append('\t')
                  .Append(_tracked[i].Faction).Append('\t')
                  .Append(_tracked[i].Kind).Append('\n');
            }
            using (System.IO.TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, _owner))
            {
                writer.Write(sb.ToString());
            }
        }
    }
}
