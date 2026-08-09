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
    ///   wlasne       MyContractCustom       definicja z mod/Data/ContractTypes.sbc
    /// Typu `eskorta` (MyContractEscort) tu NIE MA — usunięty 2026-08-09. Klasa dalej jest
    /// w API, ale gra nie wozi definicji ContractTypeEscort, więc AddContract zwracało Error
    /// przy KAŻDEJ próbie i to bez wpisu do logu. Implementacja i pełne ustalenie
    /// z dekompilacji zostają w historii gita — nie w żywym kodzie.
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

        // Awarię zapisu meldujemy raz na sesję — SaveState() leci przy każdej zmianie kontraktu.
        private bool _saveFailureReported;

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

        // ---- Ślad po ostatnim wystawieniu: wyłącznie dla /zf autotest (Autotest.cs) ----
        // Create() jest „wystrzel i zapomnij" — mówi graczowi na czat, co się stało, i tyle.
        // Autotest musi to samo WIEDZIEĆ, żeby odróżnić „powstała naprawa" od „naprawa zeszła
        // po cichu na dostawę". To drugie jest najgroźniejszym cichym błędem tej mechaniki
        // (I15 w docs/testy-reczne.md): zlecenie powstaje, contract_created wraca z typem
        // „dostawa", brain go utrwala i wszystko wygląda zdrowo — a zamówiony typ od tygodni
        // nie działa. Liczy się każde ROZSTRZYGNIĘCIE, także odmowa przed AddContract.
        public string OstatniZadanyTyp { get; private set; }
        public string OstatniTyp { get; private set; }   // null = nic nie powstało
        public long OstatnieId { get; private set; }
        public string OstatniPowod { get; private set; } // powód odmowy albo zejścia na dostawę
        public int LicznikRozstrzygniec { get; private set; }

        private void Rozstrzygniete(string zadany, string powstal, long id, string powod)
        {
            OstatniZadanyTyp = zadany;
            OstatniTyp = powstal;
            OstatnieId = id;
            OstatniPowod = powod;
            LicznikRozstrzygniec++;
        }

        /// <summary>
        /// Kasuje zlecenie i przestaje je śledzić — po to, by `/zf autotest kontrakty` nie
        /// zostawiał w terminalu kilkunastu zleceń po sobie. Zwykła rozgrywka tego nie używa:
        /// kontrakty kończy gracz albo czas.
        /// </summary>
        public bool UsunSledzony(long contractId)
        {
            bool usuniete = MyAPIGateway.ContractSystem != null &&
                            MyAPIGateway.ContractSystem.RemoveContract(contractId);
            int index = IndexOf(contractId);
            if (index >= 0)
            {
                _tracked.RemoveAt(index);
                SaveState();
            }
            return usuniete;
        }

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
            string zadany = string.IsNullOrEmpty(kind) ? "dostawa" : kind;
            if (MyAPIGateway.ContractSystem == null)
            {
                Rozstrzygniete(zadany, null, 0, "brak ContractSystem w tej wersji gry");
                MyAPIGateway.Utilities.ShowMessage("ZF", "Kontrakty niedostępne w tej wersji gry (brak ContractSystem)");
                return;
            }

            EconomyBlock start = FactionEconomy.FindContractBlock(faction);
            if (start == null)
            {
                Rozstrzygniete(zadany, null, 0, "frakcja nie ma bloku kontraktów ani sklepu");
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Kontrakt " + faction + " pominięty: frakcja nie ma bloku kontraktów ani sklepu (postaw stację frakcji)");
                return;
            }

            // Gra na odmowę zwraca samo Success=false — MyAddContractResultWrapper ma tylko
            // { Success, ContractId, ContractConditionId }, żadnego powodu. Dwa warunki, które
            // wywracają AddContract najczęściej, sprawdzamy więc sami i mówimy o nich wprost.
            if (MyAPIGateway.Session.SessionSettings != null && !MyAPIGateway.Session.SessionSettings.EnableEconomy)
            {
                Rozstrzygniete(zadany, null, 0, "w ustawieniach świata wyłączona jest ekonomia");
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Kontrakt " + faction + " pominięty: w ustawieniach świata WYŁĄCZONA jest ekonomia " +
                    "— bez niej gra nie przyjmie żadnego zlecenia");
                return;
            }

            var block = MyAPIGateway.Entities.GetEntityById(start.BlockId) as IMyFunctionalBlock;
            if (block != null && !block.IsWorking)
            {
                Rozstrzygniete(zadany, null, 0, "blok kontraktów nie działa (zasilanie?)");
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Kontrakt " + faction + " pominięty: blok \"" + block.CustomName +
                    "\" nie działa (brak zasilania albo niedokończony)");
                return;
            }

            // Wystawca musi mieć czym zapłacić. Kod gry (MySessionComponentContractSystem
            // .GenerateCustomContract, odczytany dekompilatorem 2026-07-29):
            //     if (MyBankingSystem.GetBalance(startBlock.OwnerId) < MoneyReward)
            //         return Fail_NotEnoughFunds;
            //     ... ChangeBalance(startBlock.OwnerId, -moneyReward);
            // Liczy się konto WŁAŚCICIELA BLOKU (tożsamość założyciela frakcji), a nie konto
            // frakcji — to dwa różne konta w MyBankingSystem. Nagroda jest z niego ŚCIĄGANA
            // przy tworzeniu, więc konto realnie się wyczerpuje: nasze testowe zlecenia zjadły
            // startowe ~14 tys. i kolejne przestały wchodzić. Dosypujemy więc dokładnie tyle,
            // ile frakcja właśnie obiecuje, i dokładnie temu, kogo gra pyta o saldo.
            if (block != null && block.OwnerId != 0)
            {
                MyAPIGateway.Players.RequestChangeBalance(block.OwnerId, reward);
            }

            int money = reward > int.MaxValue ? int.MaxValue : (int)reward;
            int collateral = money / 10;
            // API bierze MINUTY, nie sekundy: w MyContractGenerator jest
            // RemainingTimeInS = MyTimeSpan.FromMinutes(contractData.Duration).
            // Wcześniejsze durationMin * 60 zamawiało 45 GODZIN zamiast 45 minut.
            int duration = durationMin;

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
                                         money, collateral, duration));
                return;
            }
            if (kind == "naprawa" &&
                !FactionEconomy.TryFindDamagedGrid(faction, out ignoredId, out ignoredName))
            {
                SpawnProp(PropWreckPrefab, faction, OffsetFrom(start.Position, PropWreckMeters),
                          () => Finalize(faction, kind, reward, targetFaction, start,
                                         money, collateral, duration));
                return;
            }

            Finalize(faction, kind, reward, targetFaction, start, money, collateral,
                     duration);
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

            string zadanyKind = string.IsNullOrEmpty(kind) ? "dostawa" : kind;
            string actualKind = zadanyKind;
            string powodZejscia = null;
            long contractId;
            string opis;
            string powod;
            if (!TryAddOfKind(faction, actualKind, start, money, collateral, durationSeconds,
                              targetFaction, idBox, out contractId, out opis, out powod))
            {
                if (actualKind == "dostawa")
                {
                    Rozstrzygniete(zadanyKind, null, 0, powod);
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Kontrakt " + faction + " pominięty: " + powod);
                    return;
                }
                // Typ nie ma celu w świecie (albo gra go odrzuciła) — zamiast gubić zlecenie
                // wystawiamy dostawę i mówimy dlaczego.
                powodZejscia = powod;
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Zlecenie " + faction + " typu \"" + actualKind + "\" niemożliwe (" + powod +
                    ") — wystawiam dostawę");
                actualKind = "dostawa";
                if (!TryAddOfKind(faction, actualKind, start, money, collateral, durationSeconds,
                                  targetFaction, idBox, out contractId, out opis, out powod))
                {
                    Rozstrzygniete(zadanyKind, null, 0, powod);
                    MyAPIGateway.Utilities.ShowMessage("ZF", "Kontrakt " + faction + " pominięty: " + powod);
                    return;
                }
            }
            idBox[0] = contractId;
            Rozstrzygniete(zadanyKind, actualKind, contractId, powodZejscia);

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
                    EconomyBlock target = FactionEconomy.FindHaulTarget(faction, start.GridId, FactionEconomy.BlockOwner(start.BlockId));
                    MyContractHauling c;
                    if (target != null)
                    {
                        c = new MyContractHauling(start.BlockId, money, collateral, durationSeconds,
                                                  target.BlockId);
                        opis = "transport ładunku do " + (target.GridName ?? "innej stacji");
                    }
                    else
                    {
                        // DRUGA DROGA (2026-08-05): celem jest STACJA FRAKCJI, nie drugi blok.
                        // Nasz spawner stawia frakcji dokładnie JEDNĄ stację, więc warunek
                        // „dwa bloki tego samego właściciela" w normalnej grze nie miał szans —
                        // transport schodził na dostawę zawsze, a nie tylko wyjątkowo.
                        // Generator gry sprawdza właściciela WYŁĄCZNIE wtedy, gdy cel jest
                        // blokiem (`endBlock != null`); przy EndFactionStationId ta kontrola
                        // w ogóle się nie wykonuje, a frakcje mają po kilka stacji vanilla
                        // (WGR w świecie testowym: 7).
                        long stacja = FactionEconomy.FirstFactionStationId(faction);
                        if (stacja == 0)
                        {
                            powod = "frakcja nie ma ani drugiego bloku, ani własnej stacji vanilla";
                            return false;
                        }
                        c = new MyContractHauling(start.BlockId, money, collateral, durationSeconds, 0);
                        c.EndFactionStationId = stacja;
                        opis = "transport ładunku do stacji frakcji";
                    }
                    c.OnContractSucceeded = onSuccess;
                    c.OnContractFailed = onFail;
                    c.OnContractAcquired = onTaken;
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

                case "wlasne":
                {
                    MyDefinitionId definitionId;
                    if (!MyDefinitionId.TryParse(CustomContractDefinition, out definitionId))
                    {
                        powod = "gra nie zna typu " + CustomContractDefinition;
                        return false;
                    }
                    EconomyBlock target = FactionEconomy.FindHaulTarget(faction, start.GridId, FactionEconomy.BlockOwner(start.BlockId));
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

        /// <summary>
        /// Czy pokazaliśmy już w tej sesji wyjaśnienie o kontach w banku. Ogranicza SAM
        /// KOMUNIKAT, a NIE próby wystawiania zleceń.
        ///
        /// PIERWSZA WERSJA TEJ FLAGI (2026-08-05) BYŁA BŁĘDEM i trzeba to zapisać, żeby nikt
        /// nie wrócił do tamtego pomysłu: blokowała wszystkie kolejne próby do końca sesji,
        /// „bo skoro raz odmówiono, to odmówi zawsze". Przebieg 18:10 pokazał, że to nieprawda —
        /// w TEJ SAMEJ sesji `dostawa` (WGR) i `nagroda` (KRW) powstały bez problemu, a dopiero
        /// czwarty typ dostał odmowę. Blokada zjadła wtedy cztery kolejne typy i zamieniła
        /// jedną odmowę w pięć porażek. Odmowa dotyczy konkretnej frakcji i konkretnej próby,
        /// nie całej sesji.
        /// </summary>
        private static bool _ostrzezonoOKoncie;

        /// <summary>Wynik AddContract na nasze out-paramy (gra potrafi odrzucić zlecenie bez podania powodu).</summary>
        private static bool Added(MyAddContractResultWrapper result, string faction, out long contractId,
                                  out string powod)
        {
            if (!result.Success)
            {
                contractId = 0;
                // NAJCZĘSTSZA PRZYCZYNA, ustalona dekompilacją + logiem SE (2026-08-05):
                // tożsamość właściciela bloku NIE MA KONTA w banku gry. `MyBankingSystem
                // .GetBalance` zwraca wtedy -1 (a nie 0), więc warunek gry
                //     GetBalance(startBlock.OwnerId) < MoneyReward
                // jest spełniony ZAWSZE i leci Fail_NotEnoughFunds — niezależnie od tego,
                // ile frakcja ma na swoim koncie i ile jej dosypiemy. Nasze dosypanie też
                // przepada po cichu: ChangeBalanceInternal na brakującym koncie tylko loguje
                // „Target Identifier <id> does not contain account" i zwraca false.
                // Konta zakłada gra przy tworzeniu tożsamości NPC i przy WCZYTYWANIU świata
                // (MyPlayerCollection.LoadIdentities), więc świeżo wygenerowane frakcje
                // z Factions.sbc bywają bez konta aż do pierwszego zapisu i wczytania.
                // Z ModAPI konta założyć się nie da (MyBankingSystem poza whitelistą).
                // NIE zgadujemy przyczyny (poprawka 2026-08-05). Wcześniej dopisywaliśmy tu
                // „właściciel bloku nie ma konta w banku" — i to była nieprawda w przebiegu 18:17,
                // gdzie ta sama frakcja w tej samej sesji wystawiła trzy inne typy, a konta były
                // obciążane normalnie. Odmowa ma wiele przyczyn i gra POTRAFI je nazwać, tylko
                // pisze o nich do własnego logu, a nie do wrappera wyniku.
                powod = "gra odrzuciła kontrakt frakcji " + faction;
                if (!_ostrzezonoOKoncie)
                {
                    _ostrzezonoOKoncie = true;
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Wskazówka (raz na sesję): powód odmowy zlecenia gra zapisuje do SWOJEGO " +
                        "logu, nie oddaje go modowi. Zajrzyj do %APPDATA%\\SpaceEngineers\\" +
                        "SpaceEngineers_*.log i poszukaj \"CreateCustom\" albo \"does not contain " +
                        "account\" — tam stoi konkretna przyczyna.");
                }
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
                // SetNpcSpawnedGrid JEST KONIECZNE dla poszukiwań: MyContractFind.Update
                // w pierwszym ticku po przyjęciu robi `if (grid != null && !grid.IsNpcSpawnedGrid)
                // Fail()`, więc bez tej flagi zlecenie zawalało się ~1 s po przyjęciu
                // (dekompilacja Sandbox.Game.dll, 2026-07-31). Flaga jest tylko do odczytu
                // w ModAPI (IMyCubeGrid.IsNpcSpawnedGrid { get; }) — da się ją ustawić
                // WYŁĄCZNIE tu, przy spawnie. Vanilla stawia swój rekwizyt poszukiwań
                // dokładnie tak samo (MyContractWithSpawnableGrid.SpawnPrefab).
                // DOPISEK 2026-08-02 (dekompilacja MyCubeGrid.Init): SAMO SetNpcSpawnedGrid
                // NIE WYSTARCZY — silnik zaraz po ustawieniu flagi skanuje bloki i jeśli ŻADEN
                // nie ma BuiltBy ustawionego na tożsamość NPC, cofa flagę na false
                // (`if (Sync.IsServer && !flag && m_isNpcSpawnedGrid.Value) m_isNpcSpawnedGrid.Value
                // = false;`). BuiltBy ustawia dopiero SetAuthorship (`cubeBlock.BuiltBy = ownerId`),
                // więc obie flagi muszą lecieć razem, a ownerId (niżej) musi być tożsamością NPC.
                SpawningOptions.SetNpcSpawnedGrid | SpawningOptions.SetAuthorship,
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
            // Gra ŚCIĄGA kaucję w chwili przyjęcia, i to stojąc przy stacji frakcji — czyli
            // dokładnie tam, gdzie heurystyka handlu jest najczulsza. Bez wyciszenia samo
            // wzięcie zlecenia dawało graczowi +1..+3 relacji za nic i wysyłało do brainu
            // fałszywy `trade` (zaobserwowane 2026-08-01: „Handel z WGR: 3716 kr" dwie
            // sekundy po przyjęciu — co do złotówki zabezpieczenie kontraktu).
            if (_trade != null)
            {
                _trade.Suppress();
            }

            _events.WriteContractTaken(contractId.ToString(), _tracked[index].Faction,
                                       _tracked[index].Kind);
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "Zlecenie " + _tracked[index].Faction + " przyjęte (" + _tracked[index].Kind + ")");
        }

        /// <summary>
        /// Diagnostyka `/zf kontrakty`: co gra NAPRAWDĘ trzyma na bloku frakcji i na jej
        /// stacjach. Rozstrzyga pytanie „zlecenie powstało, ale nie widać go w terminalu":
        /// jeśli kontrakt jest w GetAvailableContractsForBlock, to potok tworzenia działa,
        /// a problem siedzi w powiązaniu ze stacją (AddContract ma drugi, pomijany przez nas
        /// parametr factionStationId — vanilla UI listuje zlecenia per stacja frakcji).
        /// </summary>
        public void Report()
        {
            if (MyAPIGateway.ContractSystem == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Brak ContractSystem w tej wersji gry");
                return;
            }

            MyAPIGateway.Utilities.ShowMessage("ZF", "śledzonych przez mod: " + _tracked.Count);
            for (int i = 0; i < _tracked.Count; i++)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "  #" + _tracked[i].Id + " " + _tracked[i].Faction + " stan=" +
                    MyAPIGateway.ContractSystem.GetContractState(_tracked[i].Id));
            }

            string[] tags = { "HEL", "KRW", "WGR" };
            for (int t = 0; t < tags.Length; t++)
            {
                long blockId;
                string gridName;
                if (!FactionEconomy.TryFindContractBlock(tags[t], out blockId, out gridName))
                {
                    continue; // brak bloku — to już mówi /zf stations
                }

                var onBlock = MyAPIGateway.ContractSystem.GetAvailableContractsForBlock(blockId);
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    tags[t] + " blok " + blockId + " (" + (gridName ?? "?") + "): zleceń na bloku = " +
                    onBlock.Count);
                foreach (IMyContract contract in onBlock)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "    #" + contract.Id + " nagroda=" + contract.MoneyReward +
                        " kaucja=" + contract.Collateral + " czas=" + contract.Duration);
                }

                IMyFaction faction = MyAPIGateway.Session.Factions.TryGetFactionByTag(tags[t]);
                if (faction == null)
                {
                    continue;
                }
                foreach (IMyFactionStation station in faction.Stations)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "    stacja " + station.Id + ": zleceń = " +
                        MyAPIGateway.ContractSystem.GetAvailableContractsForFactionStation(station.Id).Count);
                }
            }
        }

        /// <summary>
        /// `/zf kontrakt-test <frakcja>` — macierz wariantów AddContract. Gra na odmowę zwraca
        /// samo Success=false, a każda hipoteza kosztuje przeładowanie świata, więc zamiast
        /// zgadywać po jednej, przepuszczamy wszystkie i pytamy grę, która przechodzi.
        /// Udane zlecenia od razu kasujemy (RemoveContract), żeby nie zostawić śmieci.
        /// </summary>
        public void SelfTest(string faction)
        {
            if (MyAPIGateway.ContractSystem == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Brak ContractSystem w tej wersji gry");
                return;
            }

            long blockId;
            string gridName;
            if (!FactionEconomy.TryFindContractBlock(faction, out blockId, out gridName))
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "test " + faction + ": brak bloku kontraktów");
                return;
            }

            // Pierwsza stacja frakcji z Economy — kandydat na brakujący drugi argument
            // AddContract (vanilla listuje zlecenia per stacja, my zawsze dawaliśmy 0).
            long stationId = 0;
            IMyFaction f = MyAPIGateway.Session.Factions.TryGetFactionByTag(faction);
            if (f != null)
            {
                foreach (IMyFactionStation station in f.Stations)
                {
                    stationId = station.Id;
                    break;
                }
            }

            MyDefinitionId itemId;
            int amount;
            string opis;
            ItemForFaction(faction, out itemId, out amount, out opis);

            // Właściciel bloku to konto, które gra sprawdza i obciąża przy tworzeniu zlecenia.
            var blok = MyAPIGateway.Entities.GetEntityById(blockId) as IMyCubeBlock;
            long ownerId = blok != null ? blok.OwnerId : 0;
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "test AddContract " + faction + ": blok=" + blockId + " stacja=" + stationId +
                " wlasciciel=" + ownerId + " (kasa frakcji=" + FactionFunds.Balance(f) + ")");

            // Sprzątanie PRZED pomiarem: zlecenia z poprzednich przebiegów zostają na bloku
            // i zaśmiecają terminal. „zostało" > 0 oznacza, że RemoveContract nie zadziałało.
            var wiszace = MyAPIGateway.ContractSystem.GetAvailableContractsForBlock(blockId);
            var doUsuniecia = new List<long>();
            foreach (IMyContract stary in wiszace)
            {
                doUsuniecia.Add(stary.Id);
            }
            int usunieto = 0;
            for (int i = 0; i < doUsuniecia.Count; i++)
            {
                if (MyAPIGateway.ContractSystem.RemoveContract(doUsuniecia[i]))
                {
                    usunieto++;
                }
            }
            int zostalo = MyAPIGateway.ContractSystem.GetAvailableContractsForBlock(blockId).Count;
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "sprzątanie bloku: było " + doUsuniecia.Count + ", usunięto " + usunieto +
                ", zostało " + zostalo);

            // Sprawdzian po naprawie (2026-07-29): przyczyną odmów było konto właściciela bloku
            // — gra wymaga na nim pełnej nagrody i ściąga ją przy tworzeniu. Każdy wariant
            // dosypuje więc tyle, ile obiecuje; jeśli któryś padnie mimo tego, przyczyna jest
            // inna niż kasa i trzeba wrócić do dekompilacji GenerateCustomContract.
            TryVariant("1. 15000 kr / 45 min", ownerId, blockId, 15000, 1500, 45, blockId, itemId, amount, 0);
            TryVariant("2. 60000 kr (górne widełki)", ownerId, blockId, 60000, 6000, 45, blockId, itemId, amount, 0);
            TryVariant("3. 15000 kr + stacja frakcji", ownerId, blockId, 15000, 1500, 45, blockId, itemId, amount, stationId);
            TryVariant("4. bez dosypania (kontrola)", 0, blockId, 60000, 6000, 45, blockId, itemId, amount, 0);
        }

        private static void TryVariant(string opis, long ownerId, long startBlock, int money, int collateral,
                                       int duration, long endBlock, MyDefinitionId itemId, int amount,
                                       long stationId)
        {
            try
            {
                if (ownerId != 0)
                {
                    MyAPIGateway.Players.RequestChangeBalance(ownerId, money);
                }
                var contract = new MyContractAcquisition(startBlock, money, collateral, duration,
                                                         endBlock, itemId, amount);
                MyAddContractResultWrapper result = MyAPIGateway.ContractSystem.AddContract(contract, stationId);
                if (result.Success)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "  OK  " + opis + " -> id=" + result.ContractId);
                    MyAPIGateway.ContractSystem.RemoveContract(result.ContractId);
                }
                else
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", "  nie  " + opis);
                }
            }
            catch (Exception e)
            {
                // Zły parametr może rzucić zamiast zwrócić false — to też jest wynik testu.
                MyAPIGateway.Utilities.ShowMessage("ZF", "  WYJĄTEK " + opis + ": " + e.Message);
            }
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

        /// <summary>
        /// Nieudany zapis nie może wywalić gry — leci z Update i z callbacków kontraktów.
        /// Storage świata potrafi być niedostępny przez całą sesję (nazwa świata ≠ katalog
        /// zapisu, 2026-08-01), a wtedy pękałby tu każdy kontrakt. Meldujemy raz: ID zostają
        /// w pamięci, więc sesja działa, ale po wczytaniu świata mod ich nie odtworzy.
        /// </summary>
        private void SaveState()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _tracked.Count; i++)
            {
                sb.Append(_tracked[i].Id).Append('\t')
                  .Append(_tracked[i].Faction).Append('\t')
                  .Append(_tracked[i].Kind).Append('\n');
            }
            try
            {
                using (System.IO.TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, _owner))
                {
                    writer.Write(sb.ToString());
                }
            }
            catch (Exception e)
            {
                if (!_saveFailureReported)
                {
                    _saveFailureReported = true;
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "UWAGA: nie mogę zapisać stanu kontraktów (" + StateFile + ") — po " +
                        "wczytaniu świata zlecenia nie zostaną odtworzone. " +
                        e.GetType().Name + ": " + e.Message);
                }
            }
        }
    }
}
