using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions; // StoreItemTypes (oferta vs zamówienie)
using VRage.ModAPI;                          // IMyEntity (skan siatek po nazwie)
using VRage.ObjectBuilders;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Frakcja sama stawia sobie stację (Etap 6 — ostatni brakujący element).
    ///
    /// Do 2026-08-01 blok kontraktów i sklep mogła mieć wyłącznie siatka oddana frakcji
    /// ręcznie przez `/zf stacja`, czyli rusztowanie testowe. W normalnej rozgrywce
    /// frakcje nie miały gdzie wystawiać zleceń ani handlować.
    ///
    /// STAWIAMY GOTOWCE Z VANILLI, nie własny prefab. Pierwsza wersja stawiała ręcznie
    /// napisaną płytę 3x2 z czterema blokami na wierzchu — działała, ale wyglądała jak
    /// prototyp z warsztatu i nie miała nic wspólnego ze stacją. Vanilla ma gotowe,
    /// zaprojektowane stacje (3362 bloki u Helionu), więc bierzemy je i DOKŁADAMY im
    /// dwa brakujące bloki ekonomiczne — żaden vanillowy prefab nie ma ani terminala
    /// zleceń, ani sklepu (sprawdzone: wszystkie kandydatki mają 0 i 0).
    ///
    /// Bloki dokładamy przez <c>IMyCubeGrid.AddBlock</c> po spawnie, a nie edycją XML
    /// prefabu: dopiero na żywej siatce da się zapytać <c>CanAddCubes</c> o wolne miejsce,
    /// więc blok ląduje na kadłubie zamiast w środku innego bloku.
    ///
    /// Warunek postawienia jest STANEM ŚWIATA, nie zapisem w storage: stawiamy tylko
    /// frakcji, która nie ma żadnego bloku ekonomicznego. Dzięki temu rzecz jest
    /// idempotentna — po wczytaniu świata stacja już stoi, a gdy gracz ją zburzy,
    /// frakcja po karencji odbuduje się sama. Żadnego pliku stanu do zgubienia.
    /// </summary>
    internal sealed class StationSpawner
    {
        private const int CheckEveryTicks = 1800;   // ~30 s przy 60 Hz — to nie jest pilne
        private const int RetryTicks = 18000;       // ~5 min karencji na frakcję po próbie

        // Na tyle daleko, żeby stacja nie wyrosła graczowi na głowie, i na tyle blisko,
        // żeby dało się do niej dolecieć. Radiolatarnie stacji sięgają dużo dalej.
        private const double MinMeters = 8000;
        private const double MaxMeters = 15000;
        private const float FreeRadius = 400; // stacje vanilli bywają wielkie

        // Gotowe stacje z vanilli, dobrane do charakteru frakcji. Wszystkie statyczne,
        // duża siatka, z własnym zasilaniem (reaktory/baterie/panele).
        private static readonly string[] Tags = { "HEL", "KRW", "WGR" };
        private static readonly string[] Prefabs =
        {
            "GE_LogisticsFacility", // HEL: korporacyjne centrum logistyczne, 3362 bloki
            "RE19_PirateDepot",     // KRW: piracki skład, 591 bloków
            "RE05_StagingStation",  // WGR: przemysłowa stacja przeładunkowa, 766 bloków
        };

        // Karencja per frakcja: bez niej nieudany spawn wracałby co 30 s i zalewał czat.
        private readonly Dictionary<string, int> _nextTry = new Dictionary<string, int>();

        // SpawnPrefab jest asynchroniczny — jedna stacja naraz, żeby dwie nie wylądowały
        // w tym samym miejscu (FindFreePlace nie wie o siatce, która jeszcze nie powstała).
        private bool _spawning;
        private int _spawningOdTiku;

        // Gdy callback spawnu nie przyjdzie (a potrafi nie przyjść), `_spawning` zostawał
        // na true NA ZAWSZE i żadna kolejna frakcja nie dostawała już stacji. Po tym czasie
        // odblokowujemy się sami — lepiej spróbować drugi raz niż zamilknąć do końca sesji.
        private const int SpawnWatchdogTicks = 3600; // ~60 s przy 60 Hz

        // Sklep zatowarowujemy DOPIERO po kilku tikach od dołożenia bloku: świeżo dodany
        // blok nie jest jeszcze w pełni zainicjalizowany i wywołanie na nim CreateStoreItem
        // potrafi rzucić wyjątkiem (co 2026-08-02 zablokowało cały StationSpawner).
        private const int OpoznienieTowaruTikow = 120; // ~2 s

        private struct Zamowienie
        {
            public long BlokId;
            public string Tag;
            public int OdTiku;
        }

        private readonly List<Zamowienie> _doZatowarowania = new List<Zamowienie>();
        private int _tick;

        public void Update(int tick)
        {
            _tick = tick;
            ZatowarujOczekujace(tick);

            if (_spawning)
            {
                if (tick - _spawningOdTiku < SpawnWatchdogTicks)
                {
                    return;
                }
                _spawning = false;
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Spawn stacji nie zgłosił zakończenia — próbuję dalej");
            }
            if (tick % CheckEveryTicks != 0)
            {
                return;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return; // bez gracza nie ma od czego odmierzyć dystansu
            }

            for (int i = 0; i < Tags.Length; i++)
            {
                string tag = Tags[i];
                if (MyAPIGateway.Session.Factions.TryGetFactionByTag(tag) == null)
                {
                    continue; // frakcji nie ma w świecie (Factions.sbc się nie wczytał)
                }
                if (FactionEconomy.FindContractBlock(tag) != null)
                {
                    continue; // ma już gdzie handlować — także wtedy, gdy to siatka z `/zf stacja`
                }

                // PUŁAPKA DUPLIKATÓW (naprawione 2026-08-02, objaw: pięć „KRW.Beacon" na HUD).
                // Warunkiem był WYŁĄCZNIE działający blok kontraktów, więc gdy stacja stanęła,
                // ale bloku nie udało się wykryć (nie dołożył się, nie miał zasilania, siatka
                // jeszcze nie zdążyła się zarejestrować u właściciela), spawner uznawał, że
                // frakcja NADAL nie ma stacji — i co `RetryTicks` stawiał następną. Kolejne
                // kadłuby po 3 tys. bloków to nie kosmetyka, tylko realny koszt symulacji.
                // Teraz „czy stacja istnieje" i „czy ma komplet bloków" to DWA różne pytania:
                // istniejącą stację UZUPEŁNIAMY, a nową stawiamy tylko wtedy, gdy jej nie ma.
                IMyCubeGrid istniejaca = ZnajdzStacjeFrakcji(tag);
                if (istniejaca != null)
                {
                    // Karencja TAKŻE tutaj (2026-08-04). Bez niej ta gałąź chodziła co 30 s
                    // i zalewała czat komunikatem „uzupełniam — terminal zleceń i sklep na
                    // miejscu", który sam sobie przeczy: skoro oba bloki są na miejscu, to
                    // nie ma czego uzupełniać, a mimo to warunek wyżej znów nas tu wysyłał.
                    int nextFix;
                    if (_nextTry.TryGetValue(tag, out nextFix) && tick < nextFix)
                    {
                        continue;
                    }
                    _nextTry[tag] = tick + RetryTicks;
                    Uzupelnij(tag, istniejaca);
                    return;
                }

                int next;
                if (_nextTry.TryGetValue(tag, out next) && tick < next)
                {
                    continue;
                }
                Spawn(tag, Prefabs[i], i, player.GetPosition(), tick);
                return; // jedna stacja na przebieg
            }
        }

        /// <summary>Nazwa, którą nadajemy stacji — jednocześnie znacznik „ta frakcja już ma stację".</summary>
        private static string NazwaStacji(string tag)
        {
            return "Stacja " + tag;
        }

        private static long OwnerDla(string tag)
        {
            string ignored;
            return FactionEconomy.FindTargetIdentity(tag, out ignored);
        }

        /// <summary>
        /// Stacja tej frakcji, jeśli już stoi w świecie. Szukamy PO NAZWIE wśród wszystkich
        /// siatek, a nie przez FactionGrids — właśnie wykrywanie właściciela (BigOwners)
        /// bywa zawodne tuż po spawnie, a to ono odpowiadało za lawinę duplikatów.
        /// Nazwę nadaje sam mod (<see cref="NazwaStacji"/>), więc jest wiarygodnym znacznikiem.
        /// </summary>
        private static IMyCubeGrid ZnajdzStacjeFrakcji(string tag)
        {
            string nazwa = NazwaStacji(tag);
            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            foreach (IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                if (string.Equals(grid.DisplayName, nazwa, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(grid.CustomName, nazwa, StringComparison.OrdinalIgnoreCase))
                {
                    return grid;
                }
            }
            return null;
        }

        /// <summary>
        /// Stacja stoi, ale <see cref="FactionEconomy.FindContractBlock"/> jej nie widzi.
        /// Dokładamy brakujące bloki i — co najważniejsze — PONAWIAMY nadanie własności,
        /// bo to zwykle ona jest tu problemem, nie brak bloków (patrz komentarz przy
        /// <see cref="PrzypiszFrakcji"/>). Komunikat mówi, czy naprawa poskutkowała:
        /// dawniej twierdził „terminal zleceń i sklep na miejscu" nawet wtedy, gdy zaraz
        /// potem frakcja dalej nie miała gdzie wystawiać zleceń.
        /// </summary>
        private void Uzupelnij(string tag, IMyCubeGrid stacja)
        {
            long owner = OwnerDla(tag);
            string czego = DodajBlokiEkonomiczne(stacja, owner, tag);
            PrzypiszFrakcji(stacja, owner);

            bool widoczna = FactionEconomy.FindContractBlock(tag) != null;
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "Stacja " + tag + " była niekompletna — uzupełniam" + czego +
                (widoczna
                    ? " Frakcja ma już gdzie wystawiać zlecenia."
                    : " UWAGA: mimo to frakcja NADAL nie ma widocznego bloku — sprawdź " +
                      "właściciela siatki (`/zf stations`)."));
        }

        /// <summary>
        /// Nadaje siatkę frakcji. Wołane ZAWSZE PO dołożeniu bloków (2026-08-04).
        ///
        /// Dawniej leciało to raz, tuż po spawnie i PRZED <see cref="DodajBlokiEkonomiczne"/>,
        /// więc świeżo dołożony terminal zleceń, sklep i bateria nie przechodziły przez nadanie
        /// własności w ogóle. To wystarczyło, żeby frakcja „nie miała stacji": wyszukiwanie
        /// (<see cref="FactionEconomy.FactionGrids"/>) idzie po <c>BigOwners</c>, a te — wg
        /// dekompilacji <c>MyCubeGridOwnershipManager</c> — to właściciele o maksymalnej liczbie
        /// FUNKCJONALNYCH bloków, przeliczani dopiero przy zgłoszonej zmianie właściciela.
        /// Na kadłubie z 3 tys. bloków (HEL) nie było tego widać, bo grid i tak należał już do
        /// frakcji; na 18-blokowym gruzie i 1-blokowej siatce (KRW/WGR) nie należał do nikogo,
        /// więc stacja była dla moda niewidzialna mimo poprawnie dołożonych bloków.
        /// </summary>
        private static void PrzypiszFrakcji(IMyCubeGrid grid, long owner)
        {
            if (grid == null || owner == 0)
            {
                return;
            }
            grid.ChangeGridOwnership(owner, MyOwnershipShareModeEnum.Faction);
        }

        /// <summary>
        /// Właściwa stacja spośród siatek zwróconych przez <c>SpawnPrefab</c>.
        ///
        /// KLUCZOWE (2026-08-04): <c>result[0]</c> to NIE jest stacja. Vanillowe prefaby
        /// encounterów wożą po kilka siatek i ta pierwsza bywa dekoracją:
        ///   GE_LogisticsFacility  [0] „Factorum Logistics Facility" 2666 bloków  ← akurat ta
        ///   RE19_PirateDepot      [0] „Debris" 18 bloków        (stacja to [2], 425 bloków)
        ///   RE05_StagingStation   [0] „Dead Engineer" 1 blok    (stacja to [2], 549 bloków)
        /// Dlatego KRW dostawał terminal zleceń przykręcony do kawałka gruzu, a WGR do zwłok
        /// inżyniera — obok stała nietknięta stacja. Zgadza się to co do bloku z zapisem świata
        /// (18+3=21 i 1+3=4). HEL działał wyłącznie dlatego, że u niego siatka zerowa jest tą
        /// właściwą — czyli przez przypadek.
        ///
        /// Bierzemy NAJWIĘKSZĄ siatkę: jest to odporne niezależnie od tego, w jakiej kolejności
        /// gra zwraca siatki, i nie wymaga wpisywania indeksów per prefab (te zmieniają się
        /// z każdą aktualizacją gry, a błąd byłby znów cichy).
        /// </summary>
        private static IMyCubeGrid GlownaSiatka(List<IMyCubeGrid> siatki)
        {
            IMyCubeGrid best = null;
            int bestCount = -1;
            var bloki = new List<IMySlimBlock>();
            for (int i = 0; i < siatki.Count; i++)
            {
                IMyCubeGrid grid = siatki[i];
                if (grid == null || grid.MarkedForClose)
                {
                    continue;
                }
                bloki.Clear();
                grid.GetBlocks(bloki);
                if (bloki.Count > bestCount)
                {
                    bestCount = bloki.Count;
                    best = grid;
                }
            }
            return best;
        }

        private void Spawn(string tag, string prefab, int index, Vector3D playerPos, int tick)
        {
            long owner = OwnerDla(tag);
            if (owner == 0)
            {
                // Bez właściciela stacja byłaby niczyja: nie miałaby kto opłacać kontraktów
                // (gra ściąga nagrodę z konta WŁAŚCICIELA BLOKU), a sprzątacz śmieci SE
                // kasuje bezpańskie siatki. Spróbujemy przy następnym przebiegu — BEZ
                // nakładania pełnej karencji, bo nic się jeszcze nie wydarzyło (tożsamość
                // frakcji potrafi nie istnieć przez pierwsze sekundy po wczytaniu świata,
                // a 5 minut kary za to sprawiało, że stacje pojawiały się z opóźnieniem).
                return;
            }

            // Karencja dopiero TERAZ, gdy naprawdę zamawiamy spawn.
            _nextTry[tag] = tick + RetryTicks;

            Vector3D pos = PlaceNear(playerPos, index, tick);
            MatrixD m = MatrixD.CreateWorld(pos, Vector3D.Forward, Vector3D.Up);
            var result = new List<IMyCubeGrid>();
            _spawning = true;
            _spawningOdTiku = tick;

            MyAPIGateway.PrefabManager.SpawnPrefab(
                result,
                prefab,
                pos,
                (Vector3)m.Forward,
                (Vector3)m.Up,
                Vector3.Zero,
                Vector3.Zero,
                null,
                // SetAuthorship razem z SetNpcSpawnedGrid: bez BuiltBy=NPC na choć jednym
                // bloku silnik cofa IsNpcSpawnedGrid na false zaraz po Init (dekompilacja
                // MyCubeGrid.Init, 2026-08-02 — patrz Contracts.cs.SpawnProp). Tu flaga nie
                // jest krytyczna funkcjonalnie, ale bez tego dopisku byłaby cicho fałszywa.
                SpawningOptions.SetNpcSpawnedGrid | SpawningOptions.SetAuthorship,
                owner,
                true,
                () =>
                {
                    // CAŁE ciało callbacku w try/catch — wyjątek stąd nie ucieka do gry.
                    // 2026-08-02: nieobsłużony wyjątek w tym miejscu (zatowarowanie sklepu)
                    // rozsypał kolejkę callbacków PrefabManagera: HEL zdążył dostać bloki,
                    // ale callback KRW już NIGDY nie wystartował, więc `_spawning` został
                    // na true i spawner zamilkł do końca sesji — WGR nie dostał nawet próby.
                    // Stąd też watchdog w Update: mod nie może polegać na tym, że callback
                    // gry zawsze przyjdzie.
                    _spawning = false;
                    try
                    {
                        if (result.Count == 0)
                        {
                            MyAPIGateway.Utilities.ShowMessage("ZF",
                                "Stacja " + tag + " nie powstała (brak prefabu " + prefab + "?)");
                            return;
                        }
                        IMyCubeGrid grid = GlownaSiatka(result);
                        if (grid == null)
                        {
                            MyAPIGateway.Utilities.ShowMessage("ZF",
                                "Stacja " + tag + ": prefab " + prefab + " nie dał żadnej siatki");
                            return;
                        }
                        // Bezpiecznik jak przy rekwizytach zleceń: prefab potrafi przyjść bez
                        // właściciela mimo ownerId, a stacja bez właściciela jest bezużyteczna.
                        PrzypiszFrakcji(grid, owner);
                        // Nazwa MUSI być ustawiona przed czymkolwiek, co może rzucić wyjątkiem:
                        // to po niej ZnajdzStacjeFrakcji rozpoznaje, że frakcja ma już stację.
                        // Bez tego nieudane dokładanie bloków znów robiłoby duplikaty.
                        grid.CustomName = NazwaStacji(tag);

                        string czego = DodajBlokiEkonomiczne(grid, owner, tag);
                        // DRUGI raz, już PO dołożeniu bloków — patrz PrzypiszFrakcji.
                        PrzypiszFrakcji(grid, owner);

                        double km = Vector3D.Distance(playerPos, pos) / 1000.0;
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            tag + " postawiła stację " + km.ToString("0.0") + " km stąd" + czego);
                    }
                    catch (Exception e)
                    {
                        MyAPIGateway.Utilities.ShowMessage("ZF",
                            "Stacja " + tag + ": błąd po spawnie (" + e.GetType().Name + ": " +
                            e.Message + ") — pozostałe frakcje stawiają dalej");
                    }
                });
        }

        /// <summary>
        /// Dokłada terminal zleceń i sklep, bo żaden vanillowy prefab stacji ich nie ma.
        /// Zwraca dopisek do komunikatu — gdy się nie uda, gracz ma o tym WIEDZIEĆ, inaczej
        /// stacja stoi i wygląda dobrze, a zleceń nie ma i nie wiadomo dlaczego.
        /// </summary>
        private string DodajBlokiEkonomiczne(IMyCubeGrid grid, long owner, string tag)
        {
            long ignored;
            bool maKontrakty = FactionEconomy.HasBlockOfType(grid, FactionEconomy.ContractType, out ignored);
            bool maSklep = FactionEconomy.HasBlockOfType(grid, FactionEconomy.StoreType, out ignored);
            // Szukamy NASZEJ baterii po nazwie, nie „jakiejkolwiek baterii" (poprawka 2026-08-04).
            // Vanillowe prefaby mają baterie ROZŁADOWANE (RE19_PirateDepot: SmallBlockSmallBattery
            // z CurrentStoredPower 0.05) albo reaktor bez paliwa — sprawdzanie samej obecności
            // sprawiało, że przy takim kadłubie nasza pełna bateria nie powstawała NIGDY, czyli
            // kod dokładający zasilanie był martwy dokładnie tam, gdzie był potrzebny.
            bool maBaterie = MaNaszeZasilanie(grid);

            // ZASILANIE PIERWSZE — bez niego reszta jest martwa (2026-08-02).
            // Vanillowe prefaby, których używamy, to REKWIZYTY encounterów, nie działające
            // stacje: RE19_PirateDepot ma dwie baterie z `CurrentStoredPower 0.05` i ZERO
            // reaktorów, GE_LogisticsFacility ma 18 baterii (i dlatego jako jedyny działał).
            // Blok kontraktów bez prądu ma `IsWorking == false`, a wtedy `Contracts.Create`
            // odrzuca zlecenie komunikatem „blok kontraktów nie działa (zasilanie?)" — to była
            // przyczyna 6 z 7 nieudanych typów zleceń w autoteście dla KRW i WGR.
            // Dokładamy własną, PEŁNĄ baterię: `MyBatteryBlock.Init` bierze
            // `ob.CurrentStoredPower`, gdy jest >= 0, a setter robi
            // `MathHelper.Clamp(value, 0, MaxStoredPower)` — stąd wolno podać z góry dużą
            // wartość i nie trzeba znać pojemności definicji (dekompilacja Sandbox.Game.dll).
            // Uzupełniamy tylko brakujące: ta metoda bywa wołana ponownie na istniejącej
            // stacji (naprawa niekompletnej), więc nie wolno jej dokładać drugiej baterii.
            if (!maBaterie)
            {
                DodajZasilanie(grid, owner);
            }

            if (!maKontrakty)
            {
                maKontrakty = Dolóż(grid, new MyObjectBuilder_ContractBlock(),
                                    "ContractBlock", "Terminal zlecen", owner) != null;
            }
            if (maSklep)
            {
                // Sklep JEST, ale może być pusty — np. stacja stanęła pod starszą wersją moda
                // albo dokładanie ofert padło. Kolejkujemy go tak samo jak świeżo dołożony;
                // ZatowarujSklep sam pominie taki, który ma już asortyment.
                long sklepId;
                if (FactionEconomy.HasBlockOfType(grid, FactionEconomy.StoreType, out sklepId) && sklepId != 0)
                {
                    _doZatowarowania.Add(new Zamowienie
                    {
                        BlokId = sklepId,
                        Tag = tag,
                        OdTiku = _tick,
                    });
                }
            }
            else
            {
                IMySlimBlock sklep = Dolóż(grid, new MyObjectBuilder_StoreBlock(),
                                           "StoreBlock", "Sklep frakcji", owner);
                maSklep = sklep != null;
                if (maSklep && sklep.FatBlock != null)
                {
                    // NIE zatowarowujemy tu i teraz: blok dopiero co powstał i wywołanie na nim
                    // CreateStoreItem potrafi rzucić wyjątkiem. Kolejka w Update robi to po
                    // OpoznienieTowaruTikow, gdy blok jest już w pełni zainicjalizowany.
                    _doZatowarowania.Add(new Zamowienie
                    {
                        BlokId = sklep.FatBlock.EntityId,
                        Tag = tag,
                        OdTiku = _tick,
                    });
                }
            }

            if (maKontrakty && maSklep)
            {
                return " — terminal zleceń i sklep na miejscu.";
            }
            return " — UWAGA: nie udało się dołożyć " +
                   (maKontrakty ? "sklepu" : (maSklep ? "terminala zleceń" : "terminala ani sklepu")) +
                   " (brak wolnego miejsca na kadłubie?).";
        }

        // Pojemności nie znamy i nie musimy: gra przycina do MaxStoredPower definicji.
        private const float BateriaDuzo = 1000000f;

        // Nazwa jest jednocześnie znacznikiem „to nasza bateria, drugiej nie dokładaj".
        private const string NazwaZasilania = "Zasilanie frakcji";

        /// <summary>Czy stacja ma już baterię dołożoną przez nas (rozpoznawaną po nazwie).</summary>
        private static bool MaNaszeZasilanie(IMyCubeGrid grid)
        {
            if (grid == null)
            {
                return false;
            }
            var bloki = new List<IMySlimBlock>();
            grid.GetBlocks(bloki);
            for (int i = 0; i < bloki.Count; i++)
            {
                var terminal = bloki[i].FatBlock as IMyTerminalBlock;
                if (terminal != null &&
                    string.Equals(terminal.CustomName, NazwaZasilania, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Dokłada naładowaną baterię, żeby bloki ekonomiczne miały z czego żyć.</summary>
        private static void DodajZasilanie(IMyCubeGrid grid, long owner)
        {
            var bateria = new MyObjectBuilder_BatteryBlock
            {
                CurrentStoredPower = BateriaDuzo, // clamp w grze => pełna
                ProducerEnabled = true,
            };
            Dolóż(grid, bateria, "LargeBlockBatteryBlock", NazwaZasilania, owner);
        }

        // MyObjectBuilder_FunctionalBlock, bo stąd biorą się CustomName (z TerminalBlock)
        // i Enabled — terminal zleceń, sklep i bateria to bloki funkcyjne i muszą wstać włączone.
        // Zwraca dodany blok (albo null), bo do sklepu trzeba jeszcze wstawić oferty.
        private static IMySlimBlock Dolóż(IMyCubeGrid grid, MyObjectBuilder_FunctionalBlock ob,
                                          string subtype, string nazwa, long owner)
        {
            Vector3I pos;
            if (!ZnajdzWolneMiejsce(grid, out pos))
            {
                return null;
            }
            ob.Enabled = true;
            ob.SubtypeName = subtype;
            ob.Min = pos;
            ob.BlockOrientation = new SerializableBlockOrientation(
                Base6Directions.Direction.Forward, Base6Directions.Direction.Up);
            ob.Owner = owner;
            ob.ShareMode = MyOwnershipShareModeEnum.Faction;
            ob.CustomName = nazwa;
            return grid.AddBlock(ob, false);
        }

        /// <summary>
        /// Jedna pozycja w sklepie frakcji: typ przedmiotu, ilość i cena bazowa.
        /// </summary>
        private struct Towar
        {
            public string Typ;      // np. "MyObjectBuilder_Ingot"
            public string Podtyp;   // np. "Iron"
            public int Ilosc;
            public int Cena;

            public Towar(string typ, string podtyp, int ilosc, int cena)
            {
                Typ = typ; Podtyp = podtyp; Ilosc = ilosc; Cena = cena;
            }
        }

        // Asortyment per frakcja — charakter frakcji ma być widoczny przy ladzie, nie tylko
        // w radiu: HEL sprzedaje przetworzone komponenty, WGR surowce i rudę, KRW amunicję
        // i to, co „znalazł". Ceny są BAZOWE: PriceManager przy pierwszym kontakcie zapamięta
        // je jako punkt odniesienia i dopiero od nich liczy mnożnik relacji.
        private static readonly Towar[] TowarHEL =
        {
            new Towar("MyObjectBuilder_Component", "SteelPlate", 2000, 25),
            new Towar("MyObjectBuilder_Component", "Construction", 1200, 30),
            new Towar("MyObjectBuilder_Component", "Computer", 400, 180),
            new Towar("MyObjectBuilder_Component", "Motor", 300, 220),
            new Towar("MyObjectBuilder_Component", "MetalGrid", 250, 320),
        };
        private static readonly Towar[] TowarWGR =
        {
            new Towar("MyObjectBuilder_Ingot", "Iron", 5000, 8),
            new Towar("MyObjectBuilder_Ingot", "Nickel", 1500, 45),
            new Towar("MyObjectBuilder_Ingot", "Silicon", 1200, 60),
            new Towar("MyObjectBuilder_Ingot", "Cobalt", 800, 110),
            new Towar("MyObjectBuilder_Ore", "Ice", 3000, 3),
        };
        private static readonly Towar[] TowarKRW =
        {
            new Towar("MyObjectBuilder_AmmoMagazine", "NATO_25x184mm", 300, 140),
            new Towar("MyObjectBuilder_AmmoMagazine", "Missile200mm", 120, 500),
            new Towar("MyObjectBuilder_Component", "SteelPlate", 800, 35),
            new Towar("MyObjectBuilder_Ingot", "Uranium", 200, 900),
            new Towar("MyObjectBuilder_Component", "Explosives", 150, 450),
        };

        private static Towar[] TowarDla(string tag)
        {
            switch (tag)
            {
                case "HEL": return TowarHEL;
                case "KRW": return TowarKRW;
                case "WGR": return TowarWGR;
                default: return TowarWGR;
            }
        }

        /// <summary>
        /// Realizuje odroczone zatowarowanie. Wyjątek z pojedynczego sklepu nie może przewrócić
        /// pętli — dlatego try/catch wokół każdej pozycji z osobna.
        /// </summary>
        private void ZatowarujOczekujace(int tick)
        {
            for (int i = _doZatowarowania.Count - 1; i >= 0; i--)
            {
                Zamowienie z = _doZatowarowania[i];
                if (tick - z.OdTiku < OpoznienieTowaruTikow)
                {
                    continue;
                }
                _doZatowarowania.RemoveAt(i);
                try
                {
                    var store = MyAPIGateway.Entities.GetEntityById(z.BlokId) as IMyStoreBlock;
                    if (store != null)
                    {
                        ZatowarujSklep(store, z.Tag);
                    }
                }
                catch (Exception e)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF",
                        "Sklep " + z.Tag + ": nie udało się wystawić towaru (" +
                        e.GetType().Name + ": " + e.Message + ")");
                }
            }
        }

        /// <summary>
        /// Wypełnia sklep frakcji ofertami.
        ///
        /// KLUCZOWE (dekompilacja Sandbox.Game.dll, 2026-08-02): gra NIGDY nie zatowaruje
        /// tego sklepu sama. <c>MyFactionTypeBaseStrategy.UpdateStationsStoreItems</c> chodzi
        /// wyłącznie po <c>faction.Stations</c>, czyli po stacjach zarejestrowanych przez
        /// vanillowy generator ekonomii przy tworzeniu świata. Nasza stacja to zwykły grid,
        /// więc jej w tej kolekcji nie ma i asortyment nigdy nie przyszedł — stąd
        /// „żadna nasza frakcja nie ma sklepu z ofertami" w autoteście i martwa cała sekcja N.
        /// Skoro gra tego nie zrobi, robi to mod.
        ///
        /// Idempotentne (2026-08-04): sklep, który już ma oferty, zostaje nietknięty. Dzięki
        /// temu wolno tu wejść także dla sklepu, który stał w świecie przed tą wersją moda —
        /// bez tego pusty sklep na istniejącej stacji nie dostawał towaru NIGDY, bo kolejka
        /// karmiła wyłącznie bloki dopiero co dołożone przez mod.
        /// </summary>
        private static void ZatowarujSklep(IMyStoreBlock store, string tag)
        {
            if (store == null)
            {
                return;
            }

            var istniejace = new List<IMyStoreItem>();
            store.GetStoreItems(istniejace);
            if (istniejace.Count > 0)
            {
                return; // ma już asortyment — drugi raz byłby darmową dostawą dla gracza
            }

            Towar[] towary = TowarDla(tag);
            for (int i = 0; i < towary.Length; i++)
            {
                Towar t = towary[i];
                var id = new MyDefinitionId();
                if (!MyDefinitionId.TryParse(t.Typ, t.Podtyp, out id))
                {
                    continue; // literówka w nazwie nie może wywrócić stawiania stacji
                }
                IMyStoreItem oferta = store.CreateStoreItem(id, t.Ilosc, t.Cena, StoreItemTypes.Offer);
                if (oferta != null)
                {
                    store.InsertStoreItem(oferta);
                }
            }
        }

        // Kolejność ma znaczenie: najpierw w górę, żeby blok wylądował na wierzchu kadłuba,
        // a nie wciśnięty gdzieś z boku między inne bloki.
        private static readonly Vector3I[] Kierunki =
        {
            Vector3I.Up, Vector3I.Forward, Vector3I.Backward,
            Vector3I.Left, Vector3I.Right, Vector3I.Down,
        };

        /// <summary>
        /// Wolna kratka STYKAJĄCA SIĘ z istniejącym blokiem — inaczej blok albo poleciałby
        /// w próżnię, albo wszedł w kolizję. Pytamy żywą siatkę (CanAddCubes), bo tylko ona
        /// zna swoją geometrię; przy edycji prefabu w XML trzeba by to zgadywać.
        /// Wybieramy kratkę najwyżej położoną (największe Y) — dach stacji jest widoczny
        /// i osiągalny, a nie schowany w kadłubie.
        /// </summary>
        private static bool ZnajdzWolneMiejsce(IMyCubeGrid grid, out Vector3I pos)
        {
            var bloki = new List<IMySlimBlock>();
            grid.GetBlocks(bloki);

            bool znaleziono = false;
            Vector3I najlepsza = Vector3I.Zero;
            for (int i = 0; i < bloki.Count; i++)
            {
                Vector3I p = bloki[i].Position;
                for (int d = 0; d < Kierunki.Length; d++)
                {
                    Vector3I c = p + Kierunki[d];
                    if (znaleziono && c.Y <= najlepsza.Y)
                    {
                        continue; // już mamy wyżej — nie ma po co pytać siatki
                    }
                    if (grid.GetCubeBlock(c) == null && grid.CanAddCubes(c, c))
                    {
                        najlepsza = c;
                        znaleziono = true;
                    }
                }
            }
            pos = najlepsza;
            return znaleziono;
        }

        /// <summary>
        /// Punkt w widełkach MinMeters..MaxMeters od gracza. Kierunek jest ROZŁOŻONY PER
        /// FRAKCJA (co ~120°) z drobnym rozrzutem z licznika tików — pierwsza wersja losowała
        /// kierunek wyłącznie z tików i trzy stacje potrafiły wylądować kilkaset metrów od
        /// siebie, co wyglądało jak osiedle, a nie jak trzy niezależne frakcje.
        /// FindFreePlace odsuwa stację, gdy trafi w asteroidę albo w cudzą siatkę.
        /// </summary>
        private static Vector3D PlaceNear(Vector3D origin, int index, int tick)
        {
            double kat = index * (2.0 * Math.PI / 3.0) + ((tick >> 5) & 31) / 31.0 * 0.6;
            double wys = (((tick >> 10) & 31) / 31.0 - 0.5) * 0.8; // lekko nad/pod płaszczyzną
            var dir = new Vector3D(Math.Cos(kat), wys, Math.Sin(kat));
            dir = Vector3D.Normalize(dir);
            double meters = MinMeters + ((tick >> 3) & 63) / 63.0 * (MaxMeters - MinMeters);
            Vector3D wanted = origin + dir * meters;
            Vector3D? free = MyAPIGateway.Entities.FindFreePlace(wanted, FreeRadius);
            return free.HasValue ? free.Value : wanted;
        }
    }
}
