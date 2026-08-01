using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 6 — CENNIK: druga strona hybrydy reputacji. Tamta przepisuje naszą relację na
    /// liczbę w oknie frakcji, ta przepisuje ją na to, ile gracz płaci przy ladzie. Sojusznik
    /// kupuje taniej, wróg płaci karę za to, że w ogóle go obsłużyli, a dość głęboka wrogość
    /// zamyka handel całkiem (embargo).
    ///
    /// Dlaczego to w ogóle działa, choć handel wykrywamy heurystyką: to DWIE różne części API.
    /// Transakcji nie da się podsłuchać (stąd <see cref="TradeWatcher"/>), ale cennik owszem —
    /// MODOWY <c>Sandbox.ModAPI.IMyStoreBlock</c> (nie ten z <c>Ingame</c>, który ma tylko
    /// Insert/Cancel/GetPlayerStoreItems) daje <c>GetStoreItems</c> na WSZYSTKIE oferty bloku,
    /// a <c>VRage.Game.ModAPI.IMyStoreItem.PricePerUnit</c> i <c>Amount</c> są zapisywalne.
    /// Nie trzeba więc anulować i wstawiać ofert od nowa — wystarczy nadpisać im cenę.
    ///
    /// KLUCZOWE: mnożnik liczymy zawsze od CENY BAZOWEJ, nigdy od bieżącej. Ceny siedzą
    /// w zapisie świata, więc bez pamiętania bazy mnożniki składałyby się przy każdym
    /// wczytaniu (1.6 -> 2.56 -> 4.1...). Baza idzie do storage moda, tak jak ID kontraktów.
    ///
    /// Embargo jest ODWRACALNE i celowo nie rusza cen: zapamiętujemy stan magazynu w chwili
    /// jego wprowadzenia i zerujemy <c>Amount</c>, a przy zniesieniu wracamy DOKŁADNIE do tej
    /// liczby. Gdybyśmy odtwarzali ilość bazową, gracz mógłby wykupić stację, zejść poniżej
    /// progu i odzyskać towar za darmo.
    /// </summary>
    internal sealed class PriceManager
    {
        // Pełny przebieg chodzi po wszystkich siatkach świata (FactionGrids), więc jest
        // wyraźnie droższy od pilnowania reputacji — stąd ~30 s, a nie ~5 s. Powtarzać trzeba,
        // bo stacje NPC same odnawiają asortyment: nowe oferty przychodzą z cenami z gry.
        private const int ReapplyEveryTicks = 1800;
        private const string StateFile = "prices_mod_state.txt";

        // Awarię zapisu meldujemy raz na sesję — próba wraca co przebieg, dopóki jest brudno.
        private bool _saveFailureReported;

        private const string StoreType = FactionEconomy.StoreType;

        private sealed class Cel
        {
            public double Modifier = 1.0;
            public bool Embargo;
            public double Value;      // nasza skala — tylko do raportu /zf ceny
            public bool Zastosowany;  // czy udało się już dosięgnąć sklepu tej frakcji
            public int Ofert;         // ile ofert przeliczyliśmy w ostatnim przebiegu
            public bool Blad;         // ostatni przebieg rzucił — nie powtarzaj skargi co 30 s
        }

        /// <summary>Cena i magazyn zapamiętane dla jednej oferty (klucz: blok + oferta).</summary>
        private struct Baza
        {
            public int Cena;            // cena bazowa, od której liczymy mnożnik
            public int IloscPrzedEmbargiem; // -1 = embargo nie obowiązuje
        }

        private readonly Type _owner;
        private readonly Dictionary<string, Cel> _cele = new Dictionary<string, Cel>();
        private readonly Dictionary<string, Baza> _baza = new Dictionary<string, Baza>();
        // Bufory wielokrotnego użytku — pełny przebieg leci co 30 s i nie ma powodu
        // produkować śmieci dla GC.
        private readonly List<IMyStoreItem> _items = new List<IMyStoreItem>();
        private readonly List<IMySlimBlock> _blocks = new List<IMySlimBlock>();
        private readonly HashSet<string> _widziane = new HashSet<string>();
        private readonly List<string> _doUsuniecia = new List<string>();
        private bool _brudny; // stan zmieniony od ostatniego zapisu

        public PriceManager(Type owner)
        {
            _owner = owner;
            LoadState();
        }

        /// <summary>Komenda price_update z brainu: zapamiętaj cennik i przepisz go do gry.</summary>
        public void Handle(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            object factionObj;
            data.TryGetValue("faction", out factionObj);
            string faction = factionObj as string;
            if (string.IsNullOrEmpty(faction))
            {
                return;
            }

            // JSON liczby parsujemy jako double (Json.ParseNumber) — stąd rzuty.
            object modifierObj;
            if (!data.TryGetValue("modifier", out modifierObj) || !(modifierObj is double))
            {
                return; // bez mnożnika nie ma czego stosować
            }

            var cel = new Cel();
            cel.Modifier = (double)modifierObj;

            object embargoObj;
            if (data.TryGetValue("embargo", out embargoObj) && embargoObj is bool)
            {
                cel.Embargo = (bool)embargoObj;
            }

            object valueObj;
            if (data.TryGetValue("value", out valueObj) && valueObj is double)
            {
                cel.Value = (double)valueObj;
            }

            Cel poprzedni;
            bool bylEmbargo = _cele.TryGetValue(faction, out poprzedni) && poprzedni.Embargo;

            _cele[faction] = cel;
            Apply(faction, cel);
            SaveStateIfDirty();

            // Embargo to zdarzenie fabularne, nie drobiazg — gracz ma wiedzieć, czemu sklep
            // nagle świeci pustkami (i kiedy znów go obsłużą).
            if (cel.Embargo && !bylEmbargo)
            {
                Show(faction + " wstrzymuje handel — wasze stosunki są za złe (relacja " +
                     cel.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + ")");
            }
            else if (!cel.Embargo && bylEmbargo)
            {
                Show(faction + " znów z tobą handluje.");
            }
        }

        /// <summary>
        /// Powtórka przebiegu: sklep frakcji może jeszcze nie istnieć, gdy przyszła komenda,
        /// a stacje NPC same odnawiają asortyment — nowe oferty mają ceny z gry, nie nasze.
        /// </summary>
        public void Update(int tick)
        {
            if (tick % ReapplyEveryTicks != 0 || _cele.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, Cel> kv in _cele)
            {
                Apply(kv.Key, kv.Value);
            }
            SaveStateIfDirty();
        }

        /// <summary>"/zf ceny" — co brain kazał zrobić z cennikiem i co z tego wyszło.</summary>
        public void Report()
        {
            if (_cele.Count == 0)
            {
                Show("Ceny: brain nie przysłał jeszcze żadnego cennika (czy brain działa? " +
                     "sync = true w [ceny]?)");
                return;
            }
            foreach (KeyValuePair<string, Cel> kv in _cele)
            {
                Cel cel = kv.Value;
                string stan;
                if (cel.Embargo)
                {
                    stan = "EMBARGO (sklep zamknięty)";
                }
                else
                {
                    stan = "ceny x" + cel.Modifier.ToString("0.00", CultureInfo.InvariantCulture);
                }
                string gdzie = cel.Zastosowany
                    ? " | ofert: " + cel.Ofert
                    : " | BRAK bloku sklepu frakcji — cennik nie ma na czym usiąść";
                Show(kv.Key + ": " + stan + " (relacja " +
                     cel.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + ")" + gdzie);
            }
        }

        /// <summary>
        /// Przepisuje cennik jednej frakcji na wszystkie jej bloki sklepu. Brak frakcji albo
        /// brak sklepu to zwykłe „jeszcze nie teraz" — próbujemy dalej co
        /// <see cref="ReapplyEveryTicks"/>.
        /// </summary>
        private void Apply(string faction, Cel cel)
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Factions == null ||
                MyAPIGateway.Session.Factions.TryGetFactionByTag(faction) == null)
            {
                return;
            }

            int ofert = 0;
            bool znalezionoSklep = false;
            try
            {
                List<IMyCubeGrid> grids = FactionEconomy.FactionGrids(faction);
                for (int g = 0; g < grids.Count; g++)
                {
                    _blocks.Clear();
                    grids[g].GetBlocks(_blocks);
                    for (int b = 0; b < _blocks.Count; b++)
                    {
                        IMyCubeBlock fat = _blocks[b].FatBlock;
                        if (fat == null)
                        {
                            continue;
                        }
                        string typeId = fat.BlockDefinition.TypeIdString;
                        if (typeId == null ||
                            typeId.IndexOf(StoreType, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        // MODOWY interfejs (Sandbox.ModAPI), nie ten z Ingame — tylko on
                        // widzi wszystkie oferty bloku, także wygenerowane przez grę.
                        var store = fat as IMyStoreBlock;
                        if (store == null)
                        {
                            continue;
                        }
                        znalezionoSklep = true;
                        ofert += ApplyToStore(store, cel);
                    }
                }
            }
            catch (Exception e)
            {
                // Cennik to ozdoba mechaniki, nie jej rdzeń (reguła: mostek nie wywala sesji).
                // Skarżymy się RAZ: przebieg wraca co 30 s, a powtarzalny błąd API zalałby czat.
                if (!cel.Blad)
                {
                    Show("Ceny " + faction + ": błąd zapisu (" + e.Message + ")");
                    cel.Blad = true;
                }
                return;
            }

            cel.Blad = false;
            cel.Zastosowany = znalezionoSklep;
            cel.Ofert = ofert;
        }

        /// <summary>Przelicza oferty jednego bloku sklepu. Zwraca ile ich było.</summary>
        private int ApplyToStore(IMyStoreBlock store, Cel cel)
        {
            _items.Clear();
            store.GetStoreItems(_items);

            long blockId = store.EntityId;
            _widziane.Clear();

            for (int i = 0; i < _items.Count; i++)
            {
                IMyStoreItem item = _items[i];
                if (item == null)
                {
                    continue;
                }
                string key = Key(blockId, item.Id);
                _widziane.Add(key);

                Baza baza;
                if (!_baza.TryGetValue(key, out baza))
                {
                    // Pierwszy kontakt z ofertą: to, co widzi gra, JEST ceną bazową.
                    baza.Cena = item.PricePerUnit;
                    baza.IloscPrzedEmbargiem = -1;
                    _baza[key] = baza;
                    _brudny = true;
                }

                // Cena zawsze z bazy — nigdy z bieżącej, bo mnożniki by się składały.
                int cena = (int)Math.Round(baza.Cena * cel.Modifier);
                if (cena < 1)
                {
                    cena = 1; // cena to int; zaokrąglenie w dół do zera robiłoby towar darmowy
                }
                if (item.PricePerUnit != cena)
                {
                    item.PricePerUnit = cena;
                }

                if (cel.Embargo)
                {
                    if (baza.IloscPrzedEmbargiem < 0)
                    {
                        // Zapamiętujemy STAN Z CHWILI EMBARGA, nie magazyn bazowy: inaczej
                        // wykupienie stacji tuż przed embargiem dawałoby darmową dostawę
                        // przy jego zniesieniu.
                        baza.IloscPrzedEmbargiem = item.Amount;
                        _baza[key] = baza;
                        _brudny = true;
                    }
                    if (item.Amount != 0)
                    {
                        item.Amount = 0;
                    }
                }
                else if (baza.IloscPrzedEmbargiem >= 0)
                {
                    if (item.Amount == 0)
                    {
                        item.Amount = baza.IloscPrzedEmbargiem;
                    }
                    baza.IloscPrzedEmbargiem = -1;
                    _baza[key] = baza;
                    _brudny = true;
                }
            }

            // Sprzątanie po ofertach, których już nie ma (stacje NPC wymieniają asortyment) —
            // bez tego plik stanu puchłby w nieskończoność. Kasujemy TYLKO klucze tego bloku,
            // żeby nie stracić baz stacji, której akurat nie odwiedziliśmy.
            string prefix = blockId.ToString(CultureInfo.InvariantCulture) + "|";
            _doUsuniecia.Clear();
            foreach (KeyValuePair<string, Baza> kv in _baza)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.Ordinal) && !_widziane.Contains(kv.Key))
                {
                    _doUsuniecia.Add(kv.Key);
                }
            }
            for (int i = 0; i < _doUsuniecia.Count; i++)
            {
                _baza.Remove(_doUsuniecia[i]);
                _brudny = true;
            }
            _doUsuniecia.Clear();

            return _items.Count;
        }

        private static string Key(long blockId, long itemId)
        {
            return blockId.ToString(CultureInfo.InvariantCulture) + "|" +
                   itemId.ToString(CultureInfo.InvariantCulture);
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }

        // --- Trwałość: ceny bazowe MUSZĄ przeżyć wczytanie świata ---
        // Ceny ofert siedzą w zapisie już przemnożone. Bez zapamiętanej bazy kolejny start
        // wziąłby je za bazę i pomnożył jeszcze raz — po kilku wczytaniach sklep byłby
        // nieosiągalny cenowo. To ten sam powód, dla którego ID kontraktów idą do storage.

        private void LoadState()
        {
            if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(StateFile, _owner))
            {
                return;
            }
            string content;
            using (System.IO.TextReader reader =
                   MyAPIGateway.Utilities.ReadFileInWorldStorage(StateFile, _owner))
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
                int cena;
                int ilosc;
                if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out cena) &&
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ilosc))
                {
                    _baza[parts[0]] = new Baza { Cena = cena, IloscPrzedEmbargiem = ilosc };
                }
            }
        }

        private void SaveStateIfDirty()
        {
            if (!_brudny)
            {
                return;
            }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, Baza> kv in _baza)
            {
                sb.Append(kv.Key).Append('\t')
                  .Append(kv.Value.Cena.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(kv.Value.IloscPrzedEmbargiem.ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
            }
            // Wyjątek stąd leci prosto z Update i wywalałby sesję (storage świata bywa martwy
            // przez całą grę — nazwa świata ≠ katalog zapisu, 2026-08-01). Przy porażce
            // ZOSTAWIAMY _brudny, żeby chwilowa awaria naprawiła się przy następnej próbie;
            // ceny bazowe muszą przetrwać, bo bez nich mnożnik składałby się przy wczytaniu.
            try
            {
                using (System.IO.TextWriter writer =
                       MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, _owner))
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
                        "UWAGA: nie mogę zapisać cen bazowych (" + StateFile + ") — po wczytaniu " +
                        "świata mnożnik frakcji nałoży się na już zmienione ceny. " +
                        e.GetType().Name + ": " + e.Message);
                }
                return;
            }
            _brudny = false;
        }
    }
}
