using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Contracts;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Samosprawdzanie w grze — komenda <c>/zf autotest [sekcja]</c> (2026-08-01,
    /// rozszerzone 2026-08-02).
    ///
    /// PO CO TO JEST. Duża część mechaniki opiera się na założeniach o ModAPI, których
    /// nie da się potwierdzić poza grą: czy <c>GetStoreItems</c> w ogóle coś zwróci, czy
    /// <c>PricePerUnit</c> jest naprawdę zapisywalne, czy <c>SpawningOptions</c> ustawia
    /// flagę <c>IsNpcSpawnedGrid</c>, czy MES postawi kadłub z naszej grupy. Do tej pory
    /// każde takie pytanie kosztowało osobny przebieg ręcznej listy z docs/testy-reczne.md
    /// — wczytanie świata, dolot, patrzenie w terminal. Autotest zadaje te pytania sam
    /// i odpowiada PASS/FAIL, a wynik idzie na czat ORAZ do events.jsonl (typy
    /// <c>autotest_result</c> i <c>autotest_summary</c>), więc konsola brainu ma komplet.
    ///
    /// CZEGO NIE ZASTĄPI: rzeczy wymagających człowieka za sterami — dolecieć do rekwizytu,
    /// złapać go podwoziem, PRZYJĄĆ zlecenie w terminalu, ocenić brzmienie radia czy
    /// sylwetkę kadłuba. Te zostają w docs/testy-reczne.md. Autotest sprawdza wszystko
    /// PRZED tą granicą: że zlecenie powstało właściwego typu, że skrzynka stoi, że
    /// reputacja doszła do gry — czyli dokładnie te miejsca, w których dotąd psuło się
    /// po cichu.
    ///
    /// Sekcje są rozdzielone celowo: „szybkie" nic nie psują i nie ściągają na gracza
    /// wrogów, a „floty"/„boty" spawnują prawdziwe statki rajdowe i wymagają kosmosu.
    /// Bez argumentu lecą tylko szybkie.
    ///
    /// Konstrukcja: prosta maszyna kroków, bo połowa sprawdzeń jest asynchroniczna
    /// (SpawnPrefab woła callback, MES stawia statek po chwili, AiEnabled buduje mapę
    /// siatki). Każdy krok ma Start, czas oczekiwania i sprawdzenie.
    ///
    /// KAŻDY krok, który zmienia stan świata, musi zarejestrować przywrócenie
    /// (<see cref="Posprzataj"/>) — podsumowanie odwija je nawet wtedy, gdy krok padł
    /// w połowie. Test, który zostawia po sobie embargo albo wrogą reputację, jest
    /// gorszy niż brak testu.
    /// </summary>
    internal sealed class Autotest
    {
        private const int Sekunda = 60; // tików przy 60 Hz
        // Co ile tików ponawiać sprawdzenie monotoniczne (patrz Krok.Poll).
        private const int PollCoTikow = 15;

        // Meldunek „nadal czekam" dla kroków z długim oknem. Bez tego przebieg wygląda na
        // ZAWIESZONY: kroki czekające na stację frakcji mają okno 150 s, więc czat milczał
        // po kilka minut i nie było jak odróżnić pracy od zwisu (2026-08-02).
        // Meldujemy tylko przy oknach dłuższych niż PostepOdProgu — krótkie zamykają się
        // same, zanim ktokolwiek zdąży się zaniepokoić, a komunikat byłby szumem.
        private const int PostepCoTikow = 10 * Sekunda;
        private const int PostepOdProgu = 15 * Sekunda;

        // Stała, a nie literał w kodzie: tools/waliduj_sbc.py sprawdza, czy nazwa prefabu
        // spawnowanego przez mod naprawdę istnieje w mod/Data/Prefabs.
        private const string RekwizytPrefab = "ZF_Zgubka";

        // Nazwy rekwizytów z prefabów (DisplayName) — po nich sprzątamy po sekcji kontraktów.
        private const string NazwaZgubki = "Zgubiony modul";
        private const string NazwaWraku = "Uszkodzony modul frakcji";
        private const string NazwaSkrzynki = "Skrzynka zrzutu";

        // Parametry zleceń wystawianych przez autotest. Czas w MINUTACH — i to jest jedna
        // z rzeczy, które ten test pilnuje (gra bierze minuty, my kiedyś dawaliśmy sekundy).
        private const long KontraktNagroda = 5000;
        private const int KontraktCzasMin = 45;

        private static readonly string[] Tagi = { "HEL", "KRW", "WGR" };

        // Tagi frakcji vanilla, na których sprawdzamy, że NIC im nie ruszamy (M7, N12).
        // Lista jawna zamiast przeglądania kolekcji frakcji: TryGetFactionByTag jest jedynym
        // wejściem, którego mod używa i o którym wiemy, że jest na whiteliście.
        private static readonly string[] TagiVanilla = { "SPRT", "UNIV", "RTSL", "CLEN", "MA" };

        /// <summary>Jeden krok testu: zrób coś, odczekaj, sprawdź.</summary>
        private sealed class Krok
        {
            public string Nazwa;
            public Action Start;
            // Zwraca pusty string = PASS, tekst = opis niepowodzenia.
            public Func<string> Sprawdz;
            public int CzekajTikow;
            // Zależność miękka (AiEnabled) albo wynik dopuszczalny (typ zlecenia, który
            // legalnie schodzi na dostawę): niepowodzenie ma być OSTRZEŻENIEM, nie błędem.
            public bool Miekki;
            // Sprawdzenie MONOTONICZNE („coś się pojawiło/zniknęło po naszej akcji") — wolno
            // je ponawiać, bo raz spełnione nie przestanie być prawdą. Wtedy czekamy tylko
            // tyle, ile trzeba, zamiast zawsze pełnego CzekajTikow: bez tego `wszystko`
            // schodziło z samego czekania grubo ponad minutę, a na wolniejszej maszynie
            // sztywny czas i tak potrafił nie wystarczyć.
            // NIE ustawiaj tego na sprawdzeniach negatywnych („nic nie powstało") — takie
            // przeszłyby w pierwszym tiku, zanim rzecz zdążyłaby się w ogóle wydarzyć.
            public bool Poll;
            // Nazwa grupy kroków (zwykle sekcji). Kroki SPRZĄTAJĄCE zostawiaj bez grupy —
            // one mają lecieć zawsze, także po pominięciu reszty.
            public string Grupa;
            // Warunek konieczny grupy. Gdy padnie, pozostałe kroki tej samej grupy są
            // POMIJANE zamiast po kolei przewracać się na tym samym braku: na świeżym świecie
            // (stacje wstają ~30 s) sekcja kontraktów przepalała na to ponad pół minuty
            // czekania i wypluwała siedem identycznych porażek zamiast jednej wymownej.
            public bool Bramka;
            // Jak Miekki, ale rozstrzygane DOPIERO przy zgłaszaniu wyniku. Potrzebne tam, gdzie
            // „to tylko ostrzeżenie" zależy od stanu świata, którego przy budowaniu listy kroków
            // jeszcze nie znamy (np. czy AiEnabled zdążyło odpowiedzieć na rejestrację).
            public Func<bool> MiekkiGdy;
        }

        private readonly EventWriter _events;
        private readonly PriceManager _prices;
        private readonly ContractManager _contracts;
        private readonly ReputationSync _reputation;
        private readonly RansomManager _ransom;
        private readonly CrewSpawner _crew;
        private readonly CombatTracker _combat;

        private const ulong AiEnabledWorkshopId = 2596208372;

        /// <summary>
        /// Czy AiEnabled jest ZASUBSKRYBOWANE w tym świecie — czytane z listy modów świata,
        /// a NIE z uchwytu API. Rozstrzyga, czy brak botów to OSTRZEŻENIE (zależność miękka,
        /// moda po prostu nie ma), czy BŁĄD (mod jest, a boty się nie pojawiają).
        ///
        /// Dwie poprawki, obie z realnych przebiegów:
        /// 2026-08-04 — dotąd było to ZAWSZE ostrzeżenie, więc przebieg zameldował „bez
        /// AiEnabled to normalne" na świecie z aktywnym AiEnabled v1.9.
        /// 2026-08-05 — pierwsza wersja pytała o to `RemoteBotAPI.Valid` i myliła się tak samo,
        /// tylko subtelniej: `Valid` ustawia się dopiero, gdy AiEnabled ODPOWIE na rejestrację,
        /// więc mod obecny, lecz nieodpowiadający, dalej wychodził na „nieobecny". A to właśnie
        /// ten przypadek zachodzi i to on jest przyczyną pustych pokładów.
        /// </summary>
        private static bool AiEnabledWSwiecie
        {
            get
            {
                if (MyAPIGateway.Session == null || MyAPIGateway.Session.Mods == null)
                {
                    return false;
                }
                foreach (var mod in MyAPIGateway.Session.Mods)
                {
                    if (mod.PublishedFileId == AiEnabledWorkshopId)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>Czy uchwyt API zdążył się zarejestrować u AiEnabled.</summary>
        private bool ApiBotowGotowe
        {
            get { return _crew != null && _crew.ApiZarejestrowane; }
        }

        /// <summary>
        /// Czy gracz stoi dość blisko którejkolwiek stacji, żeby załoga miała prawo tam być.
        /// Bez tego sekcja P zgłaszała FAIL za coś, czego autotest nie mógł spełnić: stacje
        /// stają 8–15 km od gracza, a załogę dokładamy tylko w promieniu 3 km — więc dopóki
        /// nikt tam nie doleci, botów NIE MA i to jest zachowanie zamierzone (2026-08-05).
        /// </summary>
        private static bool StacjaWZasieguZalogi
        {
            get
            {
                double d = CrewSpawner.DystansDoNajblizszejStacji();
                return d >= 0 && d <= CrewSpawner.ZasiegZalogi;
            }
        }

        /// <summary>Wymówka dla kroków sekcji P, gdy gracz jest za daleko od stacji.</summary>
        private static string PowodPozaZasiegiem()
        {
            double d = CrewSpawner.DystansDoNajblizszejStacji();
            string gdzie = d < 0
                ? "w świecie nie ma jeszcze żadnej stacji frakcji"
                : "najbliższa stacja jest " + (int)(d / 1000) + " km stąd, a załogę stawiamy " +
                  "tylko w promieniu " + (int)(CrewSpawner.ZasiegZalogi / 1000) + " km";
            return "NIE DA SIĘ SPRAWDZIĆ STĄD: " + gdzie + ". To nie jest błąd — botów, " +
                   "których nikt nie widzi, celowo nie stawiamy. Dolec do stacji i powtórz " +
                   "`/zf autotest boty`.";
        }

        private List<Krok> _kroki;
        private string _sekcja;
        private int _index;
        private int _startTick;
        private bool _wystartowal;
        private int _pass;
        private int _fail;
        private int _warn;
        // Grupa, której bramka padła — jej pozostałe kroki pomijamy (patrz Krok.Bramka).
        private string _pominietaGrupa;

        // Stan dzielony między krokami sekcji „ceny" — bazowa oferta, na której mierzymy.
        private string _cenyTag;
        private long _cenyBlok;
        private long _cenyOferta;
        private int _cenyBaza;
        private int _cenyIlosc;
        // Cudzy sklep (frakcja spoza moda) — punkt kontrolny do N12.
        private long _obcyBlok;
        private long _obcaOferta;
        private int _obcaCena;

        // Sekcja „kontrakty": licznik rozstrzygnięć sprzed wywołania i lista do posprzątania.
        private int _kontraktLicznik;
        private readonly List<long> _kontraktyDoUsuniecia = new List<long>();

        // Sekcja „floty": pozycja statku sprzed okna obserwacji (czy w ogóle leci) oraz
        // liczba śledzonych siatek sprzed spawnu — po niej poznajemy NOWY kadłub.
        private Vector3D _flotaPozycja;
        private long _flotaGrid;
        private int _flotaPrzed;
        // Ile siatek KRW stało w świecie, ZANIM krok o załodze zamówił swój rajd.
        private int _botyPrzed;

        // Sekcja „boty": liczebność załogi sprzed powtórnego podejścia (P6).
        private int _zalogaPrzed;

        // Siatki do posprzątania po teście (rekwizyty), żeby autotest nie zaśmiecał świata.
        private readonly List<IMyCubeGrid> _doSprzatniecia = new List<IMyCubeGrid>();
        // Przywrócenia stanu świata (reputacja, cennik, żądania okupu). Odwijane ZAWSZE
        // w podsumowaniu — także gdy krok w środku sekcji padł.
        private readonly List<Action> _przywrocenia = new List<Action>();

        public Autotest(EventWriter events, PriceManager prices, ContractManager contracts,
                        ReputationSync reputation, RansomManager ransom, CrewSpawner crew,
                        CombatTracker combat)
        {
            _events = events;
            _prices = prices;
            _contracts = contracts;
            _reputation = reputation;
            _ransom = ransom;
            _crew = crew;
            _combat = combat;
        }

        public bool Trwa { get { return _kroki != null; } }

        public void Handle(string args)
        {
            string sekcja = (args ?? "").Trim().ToLowerInvariant();
            if (sekcja.Length == 0)
            {
                sekcja = "szybkie";
            }
            if (Trwa)
            {
                // Zapytanie o status, nie tylko odmowa: przy długich krokach (stacje, floty)
                // to jedyny sposób, żeby na żądanie odróżnić „pracuje" od „zwisł".
                string gdzie = _index < _kroki.Count ? _kroki[_index].Nazwa : "kończę";
                Powiedz("autotest TRWA (sekcja " + _sekcja + "): krok " + (_index + 1) + "/" +
                        _kroki.Count + " — " + gdzie);
                Powiedz("  dotąd: " + _pass + " PASS, " + _fail + " FAIL, " + _warn +
                        " OSTRZEŻEŃ. Koniec poznasz po linii \"=== AUTOTEST … koniec\".");
                return;
            }

            var kroki = new List<Krok>();
            switch (sekcja)
            {
                case "szybkie":
                    DodajSzybkie(kroki);
                    break;
                case "stacje":
                    DodajStacje(kroki);
                    break;
                case "ceny":
                    DodajCeny(kroki);
                    break;
                case "rekwizyt":
                    DodajRekwizyt(kroki);
                    break;
                case "despawn":
                    DodajDespawn(kroki);
                    break;
                case "kontrakty":
                    DodajKontrakty(kroki);
                    break;
                case "reputacja":
                    DodajReputacje(kroki);
                    break;
                case "okup":
                    DodajOkup(kroki);
                    break;
                case "floty":
                    DodajFloty(kroki);
                    break;
                case "boty":
                    DodajBoty(kroki);
                    break;
                case "wszystko":
                    DodajSzybkie(kroki);
                    DodajFloty(kroki);
                    DodajBoty(kroki);
                    break;
                default:
                    Powiedz("Nieznana sekcja \"" + sekcja + "\". Dozwolone: szybkie (domyślnie), " +
                            "stacje, ceny, rekwizyt, despawn, kontrakty, reputacja, okup, floty, " +
                            "boty, wszystko.");
                    return;
            }

            _sekcja = sekcja;
            _kroki = kroki;
            _index = 0;
            _wystartowal = false;
            _pass = 0;
            _fail = 0;
            _warn = 0;
            _pominietaGrupa = null;
            Powiedz("=== AUTOTEST [" + sekcja + "]: " + kroki.Count + " sprawdzeń ===");
            if (sekcja == "floty" || sekcja == "boty" || sekcja == "wszystko")
            {
                Powiedz("UWAGA: ta sekcja spawnuje prawdziwe statki rajdowe — rób ją w KOSMOSIE " +
                        "i na świecie testowym.");
            }
        }

        /// <summary>
        /// Zestaw „nic nie zepsuje": stan świata jest przywracany, żadne wrogie statki nie
        /// lecą. Kontrakty i okup ZMIENIAJĄ świat na chwilę (zlecenie w terminalu, skrzynka
        /// zrzutu), ale sprzątają po sobie w tym samym przebiegu.
        /// </summary>
        private void DodajSzybkie(List<Krok> kroki)
        {
            DodajStacje(kroki);
            DodajCeny(kroki);
            DodajRekwizyt(kroki);
            // Despawn stawia i kasuje własne rekwizyty, nie ściąga wrogów i niczego nie
            // zostawia — należy do zestawu bezpiecznego.
            DodajDespawn(kroki);
            DodajKontrakty(kroki);
            DodajReputacje(kroki);
            DodajOkup(kroki);
        }

        public void Update(int tick)
        {
            if (_kroki == null)
            {
                return;
            }
            if (_index >= _kroki.Count)
            {
                Podsumuj();
                return;
            }

            Krok krok = _kroki[_index];
            if (!_wystartowal && _pominietaGrupa != null && krok.Grupa == _pominietaGrupa)
            {
                ZglosPominiete(krok);
                Dalej();
                return;
            }
            if (!_wystartowal)
            {
                _wystartowal = true;
                _startTick = tick;
                if (krok.Start != null)
                {
                    try
                    {
                        krok.Start();
                    }
                    catch (Exception e)
                    {
                        Zglos(krok, "wyjątek w kroku: " + e.GetType().Name + ": " + e.Message);
                        Dalej();
                        return;
                    }
                }
                if (krok.CzekajTikow > 0)
                {
                    return;
                }
            }
            else if (tick - _startTick < krok.CzekajTikow)
            {
                int czekam = tick - _startTick;

                // Znak życia: bez niego długie okno wygląda jak zawieszony mod.
                if (krok.CzekajTikow >= PostepOdProgu && czekam > 0 && czekam % PostepCoTikow == 0)
                {
                    Powiedz("… czekam (" + (czekam / Sekunda) + " z " +
                            (krok.CzekajTikow / Sekunda) + " s): " + krok.Nazwa);
                }

                // Krok monotoniczny wolno zamknąć wcześniej, gdy warunek już zaszedł.
                // Porażka w trakcie czekania nic nie znaczy — czekamy dalej, do końca okna.
                if (!krok.Poll || krok.Sprawdz == null || czekam % PollCoTikow != 0)
                {
                    return;
                }
                string wczesniej;
                try
                {
                    wczesniej = krok.Sprawdz();
                }
                catch (Exception)
                {
                    return; // jeszcze nie gotowe (np. cel dopiero powstaje) — nie hałasuj
                }
                if (!string.IsNullOrEmpty(wczesniej))
                {
                    return;
                }
                Zglos(krok, "");
                Dalej();
                return;
            }

            string blad;
            try
            {
                blad = krok.Sprawdz == null ? "" : krok.Sprawdz();
            }
            catch (Exception e)
            {
                blad = "wyjątek przy sprawdzaniu: " + e.GetType().Name + ": " + e.Message;
            }
            Zglos(krok, blad);
            Dalej();
        }

        private void Dalej()
        {
            _index++;
            _wystartowal = false;
        }

        /// <summary>
        /// Krok pominięty, bo bramka jego grupy padła wcześniej. Liczy się do OSTRZEŻEŃ
        /// (jak dawniej), ale TERAZ trafia też do events.jsonl — dawniej szedł tylko na czat,
        /// więc audyt samego pliku (bez czatu pod ręką) widział 6 z 19 ostrzeżeń i wyglądał
        /// na niekompletny, choć CLAUDE.md obiecuje autotest_result "per krok" (2026-08-02).
        /// </summary>
        private void ZglosPominiete(Krok krok)
        {
            const string wynik = "OSTRZEŻENIE";
            _warn++;
            string opis = "POMINIĘTE — warunek konieczny sekcji (" + krok.Grupa + ") nie jest spełniony";
            Powiedz(wynik + " " + krok.Nazwa + " — " + opis);

            var data = new Dictionary<string, object>
            {
                { "sekcja", _sekcja },
                { "nazwa", krok.Nazwa },
                { "wynik", wynik },
                { "opis", opis },
            };
            Zdarzenie("autotest_result", data);
        }

        private void Zglos(Krok krok, string blad)
        {
            string wynik;
            if (string.IsNullOrEmpty(blad))
            {
                wynik = "PASS";
                _pass++;
            }
            else if (krok.Miekki || (krok.MiekkiGdy != null && krok.MiekkiGdy()))
            {
                wynik = "OSTRZEŻENIE";
                _warn++;
            }
            else
            {
                wynik = "FAIL";
                _fail++;
            }

            // Padła bramka — reszta jej grupy nie ma czego sprawdzać.
            if (!string.IsNullOrEmpty(blad) && krok.Bramka && krok.Grupa != null)
            {
                _pominietaGrupa = krok.Grupa;
            }

            Powiedz(wynik + " " + krok.Nazwa + (string.IsNullOrEmpty(blad) ? "" : " — " + blad));

            // Ten sam wynik do events.jsonl: konsola brainu ma komplet obok reszty zdarzeń,
            // a plik zostaje jako ślad po przebiegu (do wklejenia w zgłoszeniu).
            var data = new Dictionary<string, object>
            {
                { "sekcja", _sekcja },
                { "nazwa", krok.Nazwa },
                { "wynik", wynik },
                { "opis", blad ?? "" },
            };
            Zdarzenie("autotest_result", data);
        }

        private void Podsumuj()
        {
            Powiedz("=== AUTOTEST [" + _sekcja + "] koniec: " + _pass + " PASS, " + _fail +
                    " FAIL, " + _warn + " OSTRZEŻEŃ ===");

            // Zbiorczy wynik: bez niego konsola brainu widziała pojedyncze kroki, ale nie
            // miała jak stwierdzić, że przebieg się SKOŃCZYŁ (ani czy nie urwał się w połowie).
            // Liczby muszą iść jako double — Json.Stringify nie serializuje int/long.
            var podsumowanie = new Dictionary<string, object>
            {
                { "sekcja", _sekcja },
                { "pass", (double)_pass },
                { "fail", (double)_fail },
                { "warn", (double)_warn },
                { "krokow", (double)(_kroki == null ? 0 : _kroki.Count) },
            };
            Zdarzenie("autotest_summary", podsumowanie);

            Posprzataj();
            _kroki = null;
        }

        /// <summary>
        /// Odwija wszystko, co test zmienił w świecie. Wołane z podsumowania, więc leci także
        /// po przebiegu, w którym połowa kroków padła — inaczej nieudany test zostawiałby
        /// wrogą reputację albo skrzynkę zrzutu na stałe.
        /// </summary>
        private void Posprzataj()
        {
            for (int i = 0; i < _przywrocenia.Count; i++)
            {
                try
                {
                    _przywrocenia[i]();
                }
                catch (Exception e)
                {
                    Powiedz("sprzątanie: " + e.GetType().Name + ": " + e.Message);
                }
            }
            _przywrocenia.Clear();

            for (int i = 0; i < _doSprzatniecia.Count; i++)
            {
                if (_doSprzatniecia[i] != null && !_doSprzatniecia[i].MarkedForClose)
                {
                    _doSprzatniecia[i].Close();
                }
            }
            _doSprzatniecia.Clear();
        }

        private void Zdarzenie(string typ, Dictionary<string, object> data)
        {
            if (_events == null || _events.Failed)
            {
                return;
            }
            var obj = new Dictionary<string, object>
            {
                { "type", typ },
                { "data", data },
            };
            _events.WriteRawEvent(Json.Stringify(obj));
        }

        // ================= SEKCJA: STACJE (I1a-I1c) =================

        private void DodajStacje(List<Krok> kroki)
        {
            for (int i = 0; i < Tagi.Length; i++)
            {
                string tag = Tagi[i]; // kopia dla domknięcia
                kroki.Add(new Krok
                {
                    Nazwa = "stacja " + tag + ": frakcja ma gdzie wystawiać zlecenia",
                    // StationSpawner stawia stacje PO KOLEI, jedną na ~30 s (CheckEveryTicks),
                    // więc ostatnia frakcja w kolejce (WGR) potrafi czekać na swoją turę nawet
                    // ~90 s od wczytania świata + czas na async SpawnPrefab. Bez pollowania ten
                    // krok fałszywie krzyczał FAIL, gdy autotest po prostu wystartował za wcześnie.
                    Poll = true,
                    CzekajTikow = 150 * Sekunda,
                    Sprawdz = () =>
                    {
                        if (MyAPIGateway.Session.Factions.TryGetFactionByTag(tag) == null)
                        {
                            return "nie ma frakcji " + tag + " w świecie (stary zapis? Factions.sbc " +
                                   "wczytuje się przy GENEROWANIU świata)";
                        }
                        EconomyBlock blok = FactionEconomy.FindContractBlock(tag);
                        if (blok == null)
                        {
                            return "BRAK bloku kontraktów i sklepu — StationSpawner jeszcze nie " +
                                   "postawił stacji (czekaj do 150 s) albo spawn się nie udał";
                        }
                        if (!blok.IsContractBlock)
                        {
                            return "jest tylko sklep, bez terminala zleceń — AddBlock nie znalazł " +
                                   "wolnej kratki na kadłubie?";
                        }
                        return "";
                    },
                });

                kroki.Add(new Krok
                {
                    Nazwa = "stacja " + tag + ": ma sklep (bez niego cennik nie ma na czym usiąść)",
                    Poll = true,
                    CzekajTikow = 150 * Sekunda,
                    Sprawdz = () =>
                    {
                        long ignored;
                        List<IMyCubeGrid> siatki = FactionEconomy.FactionGrids(tag);
                        for (int g = 0; g < siatki.Count; g++)
                        {
                            if (FactionEconomy.HasBlockOfType(siatki[g], FactionEconomy.StoreType, out ignored))
                            {
                                return "";
                            }
                        }
                        return "żadna siatka " + tag + " nie ma bloku sklepu";
                    },
                });

                // I1b. Pełny test („zapisz i wczytaj świat") wymaga reloadu, ale objaw, którego
                // szukamy, jest widoczny od razu: druga stacja tej samej frakcji w świecie.
                // Miękki, bo `/zf stacja` (rusztowanie testowe) legalnie robi drugą siatkę.
                kroki.Add(new Krok
                {
                    Nazwa = "stacja " + tag + ": nie zdublowała się (I1b)",
                    Miekki = true,
                    Sprawdz = () =>
                    {
                        int ile = LiczSiatkiEkonomiczne(tag);
                        return ile <= 1
                            ? ""
                            : "frakcja ma " + ile + " siatek z blokiem ekonomicznym — jeśli żadnej " +
                              "nie oddałeś przez /zf stacja, StationSpawner postawił duplikat";
                    },
                });
            }
        }

        /// <summary>Ile siatek frakcji niesie blok kontraktów albo sklepu (I1b).</summary>
        private static int LiczSiatkiEkonomiczne(string tag)
        {
            long ignored;
            int ile = 0;
            List<IMyCubeGrid> siatki = FactionEconomy.FactionGrids(tag);
            for (int g = 0; g < siatki.Count; g++)
            {
                if (FactionEconomy.HasBlockOfType(siatki[g], FactionEconomy.ContractType, out ignored) ||
                    FactionEconomy.HasBlockOfType(siatki[g], FactionEconomy.StoreType, out ignored))
                {
                    ile++;
                }
            }
            return ile;
        }

        // ================= SEKCJA: CENY (N1-N8, N12) =================
        // To jest test rozstrzygający dla całej sekcji N: sygnatury IMyStoreBlock nie były
        // potwierdzone dekompilacją, więc pierwsze pytanie brzmi „czy w ogóle widzimy oferty".

        private void DodajCeny(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "ceny: mod widzi oferty w sklepie frakcji (GetStoreItems)",
                Grupa = "ceny",
                Bramka = true,
                // Świeżo dołożony StoreBlock startuje pusty. Asortyment dokłada MOD
                // (StationSpawner.ZatowarujSklep, z paru sekund opóźnienia, żeby nie wołać
                // CreateStoreItem na dopiero co powstałym bloku) — gra tego NIE zrobi nigdy,
                // bo jej generator chodzi wyłącznie po faction.Stations, a nasza stacja to
                // zwykły grid. Sprostowanie 2026-08-04: poprzedni komentarz twierdził tu coś
                // odwrotnego i kazał czekać na cykl gry, którego nie ma.
                // Okno zostaje długie, bo stacja potrafi dopiero wstawać, gdy sekcja rusza.
                Poll = true,
                CzekajTikow = 200 * Sekunda,
                Sprawdz = () =>
                {
                    if (_prices == null)
                    {
                        return "PriceManager nie wstał (BeforeStart) — cennika nie ma czym ruszyć";
                    }
                    // Punkt kontrolny do N12 bierzemy PRZED pierwszą zmianą mnożnika.
                    ZapamietajObcySklep();
                    for (int i = 0; i < Tagi.Length; i++)
                    {
                        int ofert;
                        if (PierwszaOferta(Tagi[i], out _cenyBlok, out _cenyOferta, out _cenyBaza,
                                           out _cenyIlosc, out ofert) && ofert > 0)
                        {
                            _cenyTag = Tagi[i];
                            // Cokolwiek pójdzie dalej nie tak, cennik ma wrócić do 1.00 bez embarga.
                            string tag = _cenyTag;
                            _przywrocenia.Add(() => Cennik(tag, 1.0, false, 0));
                            Powiedz("  (mierzę na " + _cenyTag + ": " + ofert + " ofert, cena bazowa " +
                                    _cenyBaza + " kr, ilość " + _cenyIlosc + ")");
                            return "";
                        }
                    }
                    return "żadna nasza frakcja nie ma sklepu z ofertami — albo nie ma stacji " +
                           "(patrz sekcja stacje), albo sklep NPC nie dostał asortymentu";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: PricePerUnit jest zapisywalne (mnożnik 1.5)",
                Grupa = "ceny",
                Start = () => Cennik(_cenyTag, 1.5, false, -50),
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () => SprawdzCene(1.5),
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: mnożnik liczony od BAZY, nie od bieżącej (N5 — brak składania)",
                Grupa = "ceny",
                Start = () => Cennik(_cenyTag, 2.0, false, -80),
                CzekajTikow = Sekunda,
                Poll = true,
                // Gdyby mnożnik składał się z poprzednim, zobaczylibyśmy baza*1.5*2.0.
                Sprawdz = () => SprawdzCene(2.0),
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: embargo zdejmuje towar ze sklepu (Amount = 0)",
                Grupa = "ceny",
                Start = () => Cennik(_cenyTag, 2.0, true, -80),
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    int cena, ilosc, ofert;
                    if (!ZnajdzOferte(_cenyTag, _cenyBlok, _cenyOferta, out cena, out ilosc, out ofert))
                    {
                        return "oferta zniknęła ze sklepu w trakcie testu";
                    }
                    return ilosc == 0 ? "" : "ilość dalej " + ilosc + " (miało być 0)";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: zniesienie embarga wraca do bazy i do ilości sprzed embarga",
                Grupa = "ceny",
                Start = () => Cennik(_cenyTag, 1.0, false, 0),
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    int cena, ilosc, ofert;
                    if (!ZnajdzOferte(_cenyTag, _cenyBlok, _cenyOferta, out cena, out ilosc, out ofert))
                    {
                        return "oferta zniknęła ze sklepu w trakcie testu";
                    }
                    if (cena != _cenyBaza)
                    {
                        return "cena " + cena + " kr zamiast bazowych " + _cenyBaza + " kr";
                    }
                    if (ilosc != _cenyIlosc)
                    {
                        return "ilość " + ilosc + " zamiast " + _cenyIlosc + " sprzed embarga";
                    }
                    return "";
                },
            });

            // N12 — cudzych sklepów nie ruszamy. Sprawdzane PO wszystkich zmianach mnożnika,
            // bo dopiero wtedy ewentualny wyciek byłby widoczny.
            kroki.Add(new Krok
            {
                Nazwa = "ceny: sklepy frakcji vanilla/MES nietknięte (N12)",
                Grupa = "ceny",
                Miekki = true,
                Sprawdz = () =>
                {
                    if (_obcyBlok == 0)
                    {
                        return "w świecie nie ma sklepu obcej frakcji — nie ma czego pilnować " +
                               "(to nie błąd, po prostu brak próbki)";
                    }
                    int cena, ilosc, ofert;
                    if (!SzukajOfertyWBloku(_obcyBlok, _obcaOferta, out cena, out ilosc, out ofert))
                    {
                        return "oferta obcego sklepu zniknęła w trakcie testu — brak rozstrzygnięcia";
                    }
                    return cena == _obcaCena
                        ? ""
                        : "cena w OBCYM sklepie zmieniła się z " + _obcaCena + " na " + cena +
                          " kr — mnożnik wycieka poza HEL/KRW/WGR";
                },
            });
        }

        /// <summary>Pierwsza oferta w sklepie frakcji SPOZA moda — punkt kontrolny do N12.</summary>
        private void ZapamietajObcySklep()
        {
            _obcyBlok = 0;
            _obcaOferta = 0;
            _obcaCena = 0;

            var pozycje = new List<IMyStoreItem>();
            var bloki = new List<IMySlimBlock>();
            List<IMyCubeGrid> siatki = FactionEconomy.FactionGrids(null);
            for (int g = 0; g < siatki.Count; g++)
            {
                if (NaszaFrakcja(siatki[g]))
                {
                    continue;
                }
                bloki.Clear();
                siatki[g].GetBlocks(bloki);
                for (int b = 0; b < bloki.Count; b++)
                {
                    var sklep = SklepZBloku(bloki[b]);
                    if (sklep == null)
                    {
                        continue;
                    }
                    pozycje.Clear();
                    sklep.GetStoreItems(pozycje);
                    for (int i = 0; i < pozycje.Count; i++)
                    {
                        if (pozycje[i] == null)
                        {
                            continue;
                        }
                        _obcyBlok = sklep.EntityId;
                        _obcaOferta = pozycje[i].Id;
                        _obcaCena = pozycje[i].PricePerUnit;
                        return;
                    }
                }
            }
        }

        private static bool NaszaFrakcja(IMyCubeGrid grid)
        {
            List<long> owners = grid.BigOwners;
            if (owners == null || owners.Count == 0)
            {
                return false;
            }
            IMyFaction owner = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[0]);
            if (owner == null)
            {
                return false;
            }
            for (int i = 0; i < Tagi.Length; i++)
            {
                if (owner.Tag == Tagi[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Wysyła do PriceManagera taki sam ładunek, jaki przysłałby brain.</summary>
        private void Cennik(string tag, double mnoznik, bool embargo, double relacja)
        {
            if (_prices == null || string.IsNullOrEmpty(tag))
            {
                return;
            }
            var data = new Dictionary<string, object>
            {
                { "faction", tag },
                { "modifier", mnoznik },
                { "embargo", embargo },
                { "value", relacja },
            };
            _prices.Handle(data);
        }

        private string SprawdzCene(double mnoznik)
        {
            int cena, ilosc, ofert;
            if (string.IsNullOrEmpty(_cenyTag))
            {
                return "nie ustalono sklepu do pomiaru (poprzedni krok nie przeszedł)";
            }
            if (!ZnajdzOferte(_cenyTag, _cenyBlok, _cenyOferta, out cena, out ilosc, out ofert))
            {
                return "oferta zniknęła ze sklepu w trakcie testu";
            }
            int oczekiwana = (int)Math.Round(_cenyBaza * mnoznik);
            if (oczekiwana < 1)
            {
                oczekiwana = 1;
            }
            if (cena != oczekiwana)
            {
                return "cena " + cena + " kr, a miała być " + oczekiwana + " kr (baza " + _cenyBaza +
                       " x" + mnoznik.ToString("0.00") + ")";
            }
            return "";
        }

        // ================= SEKCJA: REKWIZYT (I19a) =================

        /// <summary>
        /// Stawia rekwizyt testowy przed graczem i wrzuca wynik do <paramref name="wynik"/>.
        /// Wspólne dla sekcji „rekwizyt" i „despawn" — obie potrzebują jednorazowej siatki
        /// należącej do frakcji NPC, a druga kopia tego wywołania rozjechałaby się przy
        /// pierwszej zmianie SpawningOptions.
        /// </summary>
        private static void SpawnRekwizyt(List<IMyCubeGrid> wynik, string tag, double odlegloscM)
        {
            IMyPlayer gracz = MyAPIGateway.Session.Player;
            if (gracz == null || gracz.Character == null)
            {
                return;
            }
            string ignored;
            long wlasciciel = FactionEconomy.FindTargetIdentity(tag, out ignored);
            MatrixD widok = gracz.Character.WorldMatrix;
            Vector3D pozycja = widok.Translation + widok.Forward * odlegloscM;
            Vector3D? wolne = MyAPIGateway.Entities.FindFreePlace(pozycja, 30);
            if (wolne.HasValue)
            {
                pozycja = wolne.Value;
            }
            wynik.Clear();
            MyAPIGateway.PrefabManager.SpawnPrefab(
                wynik, RekwizytPrefab, pozycja, (Vector3)widok.Forward, (Vector3)widok.Up,
                Vector3.Zero, Vector3.Zero, null,
                // SetAuthorship musi lecieć razem z SetNpcSpawnedGrid — patrz komentarz
                // przy identycznym wywołaniu w Contracts.cs.SpawnProp (2026-08-02).
                SpawningOptions.SetNpcSpawnedGrid | SpawningOptions.SetAuthorship,
                wlasciciel, true, null);
        }

        private void DodajRekwizyt(List<Krok> kroki)
        {
            var wynik = new List<IMyCubeGrid>();
            kroki.Add(new Krok
            {
                Nazwa = "rekwizyt: SpawningOptions.SetNpcSpawnedGrid naprawdę ustawia flagę",
                Start = () => SpawnRekwizyt(wynik, "WGR", 300),
                CzekajTikow = 3 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    if (wynik.Count == 0)
                    {
                        return "prefab " + RekwizytPrefab + " nie powstał — czy mod/Data/Prefabs się wczytał?";
                    }
                    IMyCubeGrid grid = wynik[0];
                    if (!_doSprzatniecia.Contains(grid))
                    {
                        _doSprzatniecia.Add(grid);
                    }
                    if (!grid.IsNpcSpawnedGrid)
                    {
                        return "grid NIE ma flagi IsNpcSpawnedGrid — zlecenia poszukiwań będą się " +
                               "zawalać sekundę po przyjęciu (MyContractFind.Update woła Fail())";
                    }
                    return "";
                },
            });
        }

        // ================= SEKCJA: DESPAWN (kontrtest z Etapu 2) =================
        // DŁUG SPŁACANY TUTAJ. Od Etapu 2 w CLAUDE.md stało „do zrobienia przy okazji:
        // kontrtest, że despawn MES NIE generuje grid_destroyed". Zwykły test sprawdza, że
        // coś SIĘ DZIEJE, i taki mieliśmy (zestrzelenie daje zdarzenie). Tu sprawdzamy, że
        // coś się NIE dzieje — a to jest w tym miejscu ważniejsze: fałszywe grid_destroyed
        // zabiera -30 relacji za statek, którego gracz nie tknął, i nie zostawia śladu poza
        // spadkiem liczby w /zf rel. Objawem jest „frakcje same z siebie mnie nienawidzą",
        // czyli coś, co bardzo łatwo złożyć na karb świata mściwego.
        //
        // GRANICA. To kontrtest REGUŁY MODA, nie integracji z MES: siatkę usuwamy przez
        // Close(), tak jak robi to despawner MES, ale samego MES tu nie ma. Tego, że MES
        // despawnuje przez tę właśnie ścieżkę, ten test nie dowodzi — dowodzi, że nasza
        // reguła nie zgłasza zniszczenia siatce, której gracz nie ostrzelał świeżo.
        //
        // Sprawdzenia NEGATYWNE (Q1, Q4) nie mają Poll — przeszłyby w pierwszym tiku,
        // zanim usunięcie siatki zdążyłoby się w ogóle rozejść.
        private void DodajDespawn(List<Krok> kroki)
        {
            var pierwszy = new List<IMyCubeGrid>();
            var drugi = new List<IMyCubeGrid>();
            int licznikPrzed = 0;

            kroki.Add(new Krok
            {
                Nazwa = "despawn: bramka (CombatTracker wstał)",
                Grupa = "despawn",
                Bramka = true,
                Sprawdz = () => _combat == null
                    ? "CombatTracker nie istnieje — bez niego cała sekcja nie ma czego mierzyć"
                    : "",
            });

            kroki.Add(new Krok
            {
                Nazwa = "Q1. SEDNO: usunięcie siatki NIEOSTRZELANEJ nie daje grid_destroyed",
                Grupa = "despawn",
                Start = () =>
                {
                    licznikPrzed = _combat.ZgloszoneZniszczenia;
                    SpawnRekwizyt(pierwszy, "KRW", 300);
                },
                CzekajTikow = 5 * Sekunda,
                // Poll wolno, choć część sprawdzenia jest negatywna: bramkuje ją ISTNIENIE
                // siatki, a ta pojawia się dopiero z callbacku spawnu. Zakaz pollowania
                // dotyczy sprawdzeń, które przechodzą w pierwszym tiku, ZANIM rzecz zdąży
                // się wydarzyć — tu pierwszy tik po prostu wraca „prefab nie powstał".
                Poll = true,
                Sprawdz = () =>
                {
                    if (pierwszy.Count == 0)
                    {
                        return "prefab " + RekwizytPrefab + " nie powstał — nie ma czego despawnować";
                    }
                    IMyCubeGrid grid = pierwszy[0];
                    // Do sprzątania ZANIM cokolwiek sprawdzimy: przy porażce wychodzimy
                    // z tej metody przed Close() i rekwizyt zostałby w świecie na zawsze.
                    if (!_doSprzatniecia.Contains(grid))
                    {
                        _doSprzatniecia.Add(grid);
                    }
                    if (_combat.CzyDespawnZglosiZniszczenie(grid.EntityId))
                    {
                        return "tracker uważa świeżo postawioną, NIETKNIĘTĄ siatkę za zniszczoną " +
                               "przez gracza — despawn MES będzie kosztował relacje bez powodu";
                    }
                    if (!grid.MarkedForClose)
                    {
                        grid.Close(); // tą samą drogą, którą siatkę usuwa despawner MES
                    }
                    return "";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "Q2. licznik grid_destroyed stoi po despawnie",
                Grupa = "despawn",
                CzekajTikow = 2 * Sekunda, // NEGATYWNE — bez Poll, czekamy pełne okno
                Sprawdz = () => _combat.ZgloszoneZniszczenia != licznikPrzed
                    ? "despawn wygenerował grid_destroyed (" + licznikPrzed + " -> " +
                      _combat.ZgloszoneZniszczenia + ") — brain policzy graczowi zniszczenie " +
                      "siatki, której ten nawet nie ostrzelał"
                    : "",
            });

            kroki.Add(new Krok
            {
                Nazwa = "Q3. KONTROLA DODATNIA: świeże trafienie NADAL liczy się jako zniszczenie",
                Grupa = "despawn",
                Start = () => SpawnRekwizyt(drugi, "KRW", 350),
                CzekajTikow = 5 * Sekunda,
                Poll = true, // jak w Q1: czekamy na callback spawnu, nie na brak zdarzenia
                Sprawdz = () =>
                {
                    if (drugi.Count == 0)
                    {
                        return "prefab " + RekwizytPrefab + " nie powstał";
                    }
                    IMyCubeGrid grid = drugi[0];
                    if (!_doSprzatniecia.Contains(grid))
                    {
                        _doSprzatniecia.Add(grid);
                    }
                    // Bez tego kroku Q1 i Q2 przechodziłyby także wtedy, gdyby reguła zawsze
                    // mówiła „nie" — a wtedy zestrzelenie statku przestałoby cokolwiek znaczyć
                    // i nikt by tego nie zauważył. Pytamy PREDYKATEM, nie zamykając siatki:
                    // prawdziwe grid_destroyed zabrałoby -30 relacji, których autotest nie
                    // ma jak oddać.
                    _combat.ZarejestrujTrafienieDlaTestu(grid, "KRW", 0);
                    return _combat.CzyDespawnZglosiZniszczenie(grid.EntityId)
                        ? ""
                        : "świeżo ostrzelana siatka NIE liczy się jako zniszczona — zestrzelenie " +
                          "statku frakcji przestało cokolwiek znaczyć dla relacji";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "Q4. granica 30 s: stare trafienie + despawn = brak grid_destroyed",
                Grupa = "despawn",
                Start = () =>
                {
                    licznikPrzed = _combat.ZgloszoneZniszczenia;
                    if (drugi.Count > 0)
                    {
                        // Ten sam grid, ale trafienie POSTARZONE tuż za okno. To realny
                        // przebieg: gracz postrzelał patrol, odleciał, a MES posprzątał
                        // kadłub minutę później. Rozbrajamy przy okazji siatkę przed
                        // sprzątaniem — z trafieniem świeżym z Q3 jej zamknięcie wysłałoby
                        // prawdziwe grid_destroyed.
                        _combat.ZarejestrujTrafienieDlaTestu(
                            drugi[0], "KRW", CombatTracker.FreshDamageTicks + Sekunda);
                        if (!drugi[0].MarkedForClose)
                        {
                            drugi[0].Close();
                        }
                    }
                },
                CzekajTikow = 2 * Sekunda, // NEGATYWNE — bez Poll
                Sprawdz = () =>
                {
                    if (drugi.Count == 0)
                    {
                        return "nie było siatki z Q3 — krok nic nie sprawdził";
                    }
                    return _combat.ZgloszoneZniszczenia != licznikPrzed
                        ? "trafienie starsze niż okno " + (CombatTracker.FreshDamageTicks / Sekunda) +
                          " s dało grid_destroyed — despawn po dawnej potyczce liczy się jak " +
                          "zestrzelenie"
                        : "";
                },
            });
        }

        // ================= SEKCJA: KONTRAKTY (I2, I13-I19b) =================
        // Najważniejsza z nowych sekcji. Mod ma przy zleceniach OSTATNIE SŁOWO: gdy nie znajdzie
        // w świecie celu (wrogiej tożsamości, drugiego bloku, uszkodzonej siatki…), po cichu
        // wystawia DOSTAWĘ i to ona wraca w contract_created. Zlecenie powstaje, brain je
        // utrwala, na czacie jest komunikat — i nikt nie zauważa, że zamówiony typ od tygodni
        // nie działa. Tu każdy typ jest zamawiany po kolei i porównywany z tym, co NAPRAWDĘ
        // powstało (ContractManager.OstatniTyp).

        private void DodajKontrakty(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "kontrakty: bramka (ContractSystem, ekonomia świata, blok frakcji)",
                Grupa = "kontrakty",
                Bramka = true,
                Sprawdz = () =>
                {
                    if (_contracts == null)
                    {
                        return "ContractManager nie wstał (BeforeStart) — zlecenia nie powstaną";
                    }
                    if (MyAPIGateway.ContractSystem == null)
                    {
                        return "ta wersja gry nie ma ContractSystem";
                    }
                    if (MyAPIGateway.Session.SessionSettings != null &&
                        !MyAPIGateway.Session.SessionSettings.EnableEconomy)
                    {
                        return "w ustawieniach świata WYŁĄCZONA jest ekonomia — gra nie przyjmie " +
                               "żadnego zlecenia";
                    }
                    if (FactionEconomy.FindContractBlock("WGR") == null)
                    {
                        return "WGR nie ma bloku kontraktów ani sklepu (patrz sekcja stacje)";
                    }
                    // Zlecenia z tego przebiegu kasujemy nawet wtedy, gdy sekcja padnie w środku.
                    _przywrocenia.Add(UsunKontraktyTestowe);
                    return "";
                },
            });

            // dostawa musi wyjść ZAWSZE — to fallback dla wszystkich pozostałych typów.
            // Jeśli nie wychodzi ona, cała mechanika zleceń leży.
            DodajKontraktTyp(kroki, "WGR", "dostawa", null, false, 2 * Sekunda);

            // Typy z celem w świecie. Miękkie, bo zejście na dostawę BYWA poprawną odpowiedzią
            // (np. transport bez drugiej stacji) — chodzi o to, żeby było WIDAĆ, że zeszło,
            // i z jakiego powodu, zamiast dowiadywać się o tym po miesiącu.
            DodajKontraktTyp(kroki, "KRW", "nagroda", "HEL", true, 2 * Sekunda);
            DodajKontraktTyp(kroki, "WGR", "transport", null, true, 2 * Sekunda);
            // `eskorta` nie jest już zamawiana — typ usunięty 2026-08-09 razem z obsługą
            // w Contracts.cs, więc sekcja pokrywa SZEŚĆ typów, nie siedem.
            // Te dwa najpierw STAWIAJĄ rekwizyt (SpawnPrefab jest asynchroniczny), stąd
            // dłuższe okno — kontrakt powstaje dopiero w callbacku spawnu.
            DodajKontraktTyp(kroki, "WGR", "naprawa", null, true, 12 * Sekunda);
            DodajKontraktTyp(kroki, "WGR", "poszukiwania", null, true, 12 * Sekunda);
            DodajFundamentCustom(kroki);

            kroki.Add(new Krok
            {
                Nazwa = "kontrakty: Duration jest w MINUTACH, a zlecenie wisi na bloku frakcji",
                Grupa = "kontrakty",
                Sprawdz = () =>
                {
                    if (_kontraktyDoUsuniecia.Count == 0)
                    {
                        return "żadne zlecenie nie powstało — nie ma czego sprawdzić";
                    }
                    long id = _kontraktyDoUsuniecia[_kontraktyDoUsuniecia.Count - 1];
                    EconomyBlock blok = FactionEconomy.FindContractBlock("KRW");
                    if (blok == null)
                    {
                        blok = FactionEconomy.FindContractBlock("WGR");
                    }
                    if (blok == null)
                    {
                        return "nie ma bloku, na którym można by odczytać zlecenie";
                    }
                    var naBloku =
                        MyAPIGateway.ContractSystem.GetAvailableContractsForBlock(blok.BlockId);
                    foreach (IMyContract kontrakt in naBloku)
                    {
                        if (kontrakt.Id != id)
                        {
                            continue;
                        }
                        // 45 zamówione i 45 odczytane = gra bierze MINUTY. Gdy kiedyś wrócimy
                        // do sekund (durationMin * 60), zobaczymy tu 2700 i test krzyknie.
                        return kontrakt.Duration == KontraktCzasMin
                            ? ""
                            : "Duration = " + kontrakt.Duration + ", a zamawialiśmy " +
                              KontraktCzasMin + " (jednostka się rozjechała)";
                    }
                    // Zlecenie mogło powstać na bloku innej frakcji niż zgadywana — to nie błąd
                    // jednostki czasu, więc nie zgłaszamy FAIL-a o Duration.
                    return "zlecenia #" + id + " nie ma na bloku " + (blok.GridName ?? "?") +
                           " — sprawdź /zf kontrakty (powstało na innej stacji?)";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "kontrakty: sprzątanie (kasowanie zleceń i rekwizytów autotestu)",
                Start = () =>
                {
                    UsunKontraktyTestowe();
                    ZbierzRekwizytDoSprzatniecia(NazwaZgubki);
                    ZbierzRekwizytDoSprzatniecia(NazwaWraku);
                },
                Sprawdz = () => "",
            });
        }

        /// <summary>
        /// Jeden typ zlecenia: zamów u frakcji, odczekaj na rozstrzygnięcie, porównaj typ
        /// zamówiony z tym, który NAPRAWDĘ powstał.
        /// </summary>
        /// <summary>
        /// ETAP 0 rodziny „custom" (docs/zlecenia-custom.md) — jedyny krok tej sekcji, który
        /// jest TWARDY dla zejścia na dostawę.
        ///
        /// PO CO. Plan przebudowy zleceń zakłada, że `wlasne` przestaje być rodzajem roboty
        /// i staje się SZABLONEM, na którym staną nasze własne rodzaje (polowanie, trybut,
        /// konwoj, pakt) — takie, których vanilla nie potrafi wyrazić, bo warunek zwycięstwa
        /// piszemy my. Cała ta rodzina stoi na jednym niesprawdzonym założeniu: że gra
        /// wczytuje `mod/Data/ContractTypes.sbc` i przyjmuje `MyContractCustom` na naszym
        /// bloku kontraktów. Dopóki tego nie wiemy, projektowanie czterech rodzajów jest
        /// budowaniem na piasku — a dokładnie ten błąd kosztował trzy przebiegi przy eskorcie.
        ///
        /// CO TO DOWODZI. `OstatniTyp == "wlasne"` znaczy, że `AddContract` przyjął nasz
        /// custom kontrakt — czyli `MyDefinitionId` się rozwinął ORAZ podtyp `ZF_Zlecenie`
        /// istnieje w danych gry. Gdyby definicji nie było, `AddContract` odmówiłby i mod
        /// zszedłby na DOSTAWĘ; do 2026-08-09 było to tylko OSTRZEŻENIE, więc taki wynik
        /// przechodził jako łagodna żółta linijka.
        ///
        /// CZEGO NIE DOWODZI (i dlatego to nie koniec Etapu 0):
        ///  * czy terminal pokazuje NASZ tytuł, czy generyczną nazwę typu — to widzi tylko
        ///    człowiek, więc krok wypisuje na czacie, czego szukać;
        ///  * czy `TryFinishCustomContract` naprawdę domyka kontrakt i wypłaca — to jest
        ///    ryzyko Etapu 1 i powód, dla którego Etap 1 wiezie JEDEN rodzaj, nie cztery.
        /// </summary>
        private void DodajFundamentCustom(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "FUNDAMENT custom: gra przyjmuje MyContractCustom z definicji ZF_Zlecenie",
                Grupa = "kontrakty",
                Start = () =>
                {
                    if (_contracts == null)
                    {
                        return;
                    }
                    _kontraktLicznik = _contracts.LicznikRozstrzygniec;
                    _contracts.Create("KRW", "wlasne", KontraktNagroda, KontraktCzasMin, null);
                },
                CzekajTikow = 2 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    string blad = SprawdzKontrakt("wlasne");
                    if (blad.Length > 0)
                    {
                        return blad + ". TO BLOKUJE CAŁĄ RODZINĘ WŁASNYCH RODZAJÓW ZLECEŃ " +
                               "(polowanie/trybut/konwoj/pakt — docs/zlecenia-custom.md): bez " +
                               "działającego MyContractCustom nie ma jak wystawić zlecenia " +
                               "z własnym warunkiem wykonania. Sprawdź, czy gra wczytała " +
                               "mod/Data/ContractTypes.sbc (log SE, szukaj \"ZF_Zlecenie\")";
                    }
                    // Poll: to leci dokładnie raz, bo po pustym wyniku krok się kończy.
                    string nazwa = _contracts.OstatniaNazwaCustom;
                    Powiedz("  ^ fundament stoi. TERAZ TY: otwórz terminal zleceń KRW i sprawdź, " +
                            "czy zlecenie nazywa się \"" + (nazwa ?? "(mod nie podał nazwy)") +
                            "\". Jeśli widzisz tam generyczną nazwę typu, definicja się wczytała, " +
                            "ale UI jej nie używa — to zmienia projekt (jedna definicja wspólna " +
                            "kontra jedna na rodzaj, patrz docs/zlecenia-custom.md).");
                    return "";
                },
            });
        }

        private void DodajKontraktTyp(List<Krok> kroki, string tag, string typ, string cel,
                                      bool miekki, int okno)
        {
            kroki.Add(new Krok
            {
                Nazwa = "zlecenie " + tag + " \"" + typ + "\": powstaje i NIE schodzi po cichu na dostawę",
                Grupa = "kontrakty",
                // Łagodzimy WYŁĄCZNIE zejście typu na dostawę; „zlecenie w ogóle nie powstało"
                // jest zawsze twarde, niezależnie od typu.
                MiekkiGdy = () => miekki && !_kontraktNiePowstal,
                Start = () =>
                {
                    if (_contracts == null)
                    {
                        return;
                    }
                    _kontraktLicznik = _contracts.LicznikRozstrzygniec;
                    _contracts.Create(tag, typ, KontraktNagroda, KontraktCzasMin, cel);
                },
                CzekajTikow = okno,
                Poll = true,
                Sprawdz = () => SprawdzKontrakt(typ),
            });
        }

        /// <summary>
        /// Czy OSTATNIE sprawdzenie zlecenia skończyło się tym, że zlecenie w ogóle nie
        /// powstało. Flaga `miekki` przy typie zlecenia miała łagodzić JEDNĄ rzecz: zejście
        /// typu na dostawę, gdy w świecie nie ma celu (I15 — dopuszczalne i opisane).
        /// Łagodziła jednak wszystko, co zwróci <see cref="SprawdzKontrakt"/>, więc gdy gra
        /// odrzucała kontrakt CAŁKOWICIE, pięć z sześciu typów meldowało OSTRZEŻENIE, a
        /// „dostawa" (jedyna z miekki=false) FAIL — ten sam powód, dwie różne barwy w tym
        /// samym przebiegu. Stąd wrażenie, że wynik autotestu jest losowy (2026-08-05).
        /// </summary>
        private bool _kontraktNiePowstal;

        private string SprawdzKontrakt(string zadany)
        {
            _kontraktNiePowstal = false;
            if (_contracts == null)
            {
                return "ContractManager nie wstał (BeforeStart)";
            }
            if (_contracts.LicznikRozstrzygniec == _kontraktLicznik)
            {
                return "brak rozstrzygnięcia — Create() nie doszedł do końca (rekwizyt się nie " +
                       "postawił i callback spawnu nigdy nie przyszedł?)";
            }
            if (_contracts.OstatnieId != 0 && !_kontraktyDoUsuniecia.Contains(_contracts.OstatnieId))
            {
                _kontraktyDoUsuniecia.Add(_contracts.OstatnieId);
            }
            if (_contracts.OstatniTyp == null)
            {
                // To NIE jest przypadek, który wolno łagodzić flagą `miekki` — patrz komentarz
                // przy _kontraktNiePowstal.
                _kontraktNiePowstal = true;
                return "zlecenie NIE powstało: " + (_contracts.OstatniPowod ?? "gra odmówiła bez powodu");
            }
            if (_contracts.OstatniTyp != zadany)
            {
                return "zeszło na \"" + _contracts.OstatniTyp + "\" (" +
                       (_contracts.OstatniPowod ?? "bez podanego powodu") + ")";
            }
            return "";
        }

        private void UsunKontraktyTestowe()
        {
            if (_contracts == null)
            {
                return;
            }
            for (int i = 0; i < _kontraktyDoUsuniecia.Count; i++)
            {
                _contracts.UsunSledzony(_kontraktyDoUsuniecia[i]);
            }
            _kontraktyDoUsuniecia.Clear();
        }

        /// <summary>
        /// Rekwizyty postawione w trakcie testu idą do sprzątania razem z resztą. Zbieramy
        /// WSZYSTKIE pasujące siatki, nie najbliższą: jedno wywołanie `poszukiwania` gubi moduł
        /// 8 km stąd, a sekcja „rekwizyt" stawia drugi 300 m przed graczem — <c>TryFindProp</c>
        /// zwróciłby tylko jeden z nich i ten dalszy zostałby w świecie na zawsze.
        /// </summary>
        private void ZbierzRekwizytDoSprzatniecia(string nazwa)
        {
            var entities = new HashSet<VRage.ModAPI.IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            foreach (VRage.ModAPI.IMyEntity entity in entities)
            {
                var grid = entity as IMyCubeGrid;
                if (grid == null || grid.MarkedForClose || grid.DisplayName == null ||
                    !grid.DisplayName.StartsWith(nazwa, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!_doSprzatniecia.Contains(grid))
                {
                    _doSprzatniecia.Add(grid);
                }
            }
        }

        // ================= SEKCJA: REPUTACJA (M1-M3, M5, M7) =================
        // Hybryda: brain liczy relację, mod przepisuje ją na natywną reputację SE. Cała
        // sekcja M była dotąd wyłącznie ręczna, a to czysta arytmetyka na API — nie ma
        // powodu, żeby człowiek klikał okno frakcji i porównywał liczby.

        private void DodajReputacje(List<Krok> kroki)
        {
            const string tag = "HEL";
            const int celWrogi = -600;   // poniżej progu gry (-500): etykieta „wróg"
            const int celSojusz = 700;   // powyżej progu gry (+500): etykieta „sojusznik"

            kroki.Add(new Krok
            {
                Nazwa = "reputacja: bramka (jest frakcja, gracz i ReputationSync)",
                Grupa = "reputacja",
                Bramka = true,
                Sprawdz = () =>
                {
                    if (_reputation == null)
                    {
                        return "ReputationSync nie wstał (BeforeStart)";
                    }
                    if (MyAPIGateway.Session.Player == null)
                    {
                        return "brak sesji gracza";
                    }
                    if (MyAPIGateway.Session.Factions == null ||
                        MyAPIGateway.Session.Factions.TryGetFactionByTag(tag) == null)
                    {
                        return "nie ma frakcji " + tag + " w świecie (NOWY świat jest wymagany — " +
                               "frakcje IsDefault powstają przy generowaniu)";
                    }
                    // Zapamiętaj, co miał brain, i oddaj to po teście. Gdy brain nie działa,
                    // celu nie było — wtedy po prostu przestajemy pilnować naszego.
                    int bylVanilla;
                    double bylaWartosc;
                    bool byl = _reputation.TryGetCel(tag, "", out bylVanilla, out bylaWartosc);
                    int przedTestem = ReputacjaWGrze(tag);
                    _przywrocenia.Add(() =>
                    {
                        if (byl)
                        {
                            Reputacja(tag, "", bylVanilla, bylaWartosc);
                        }
                        else
                        {
                            _reputation.Zapomnij(tag, "");
                            UstawReputacjeWprost(tag, przedTestem);
                        }
                    });
                    ZapamietajObceReputacje();
                    return "";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "reputacja: cel z brainu ląduje w grze i daje etykietę WRÓG (M2/M3)",
                Grupa = "reputacja",
                Start = () => Reputacja(tag, "", celWrogi, -60),
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    int wGrze = ReputacjaWGrze(tag);
                    if (wGrze != celWrogi)
                    {
                        return "gra ma " + wGrze + ", a cel to " + celWrogi +
                               " — SetReputationBetweenPlayerAndFaction nie zadziałało";
                    }
                    return wGrze <= -500
                        ? ""
                        : "wartość zapisana, ale powyżej progu wroga (-500) — okno frakcji " +
                          "pokaże neutralność mimo naszej wojny";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "reputacja: dodatnia strona skali i etykieta SOJUSZNIK",
                Grupa = "reputacja",
                Start = () => Reputacja(tag, "", celSojusz, 60),
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    int wGrze = ReputacjaWGrze(tag);
                    if (wGrze != celSojusz)
                    {
                        return "gra ma " + wGrze + ", a cel to " + celSojusz;
                    }
                    return wGrze >= 500 ? "" : "wartość zapisana, ale poniżej progu sojusznika (+500)";
                },
            });

            // SEDNO hybrydy (M5). Gra sama rusza reputację (nagrody kontraktów vanilla);
            // bez tego przywracania to samo zdarzenie liczyłoby się dwa razy — raz u nas,
            // raz w ekonomii gry. ReputationSync sprawdza cele co ~5 s, więc okno musi być
            // dłuższe niż jeden taki cykl.
            kroki.Add(new Krok
            {
                Nazwa = "reputacja: mod przywraca cel, gdy gra ruszy wartość po swojemu (M5)",
                Grupa = "reputacja",
                Start = () =>
                {
                    Reputacja(tag, "", celWrogi, -60);
                    UstawReputacjeWprost(tag, 0); // udajemy nagrodę reputacyjną z kontraktu vanilla
                },
                CzekajTikow = 8 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    int wGrze = ReputacjaWGrze(tag);
                    return wGrze == celWrogi
                        ? ""
                        : "po 8 s gra dalej ma " + wGrze + " zamiast " + celWrogi +
                          " — cel nie jest pilnowany, nasza liczba i okno frakcji się rozjadą";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "reputacja: polityka frakcja↔frakcja (SetReputation)",
                Grupa = "reputacja",
                Start = () =>
                {
                    // Wartości początkowe są konieczne: przy _reputation == null wyrażenie
                    // zwiera się przed wywołaniem i kompilator nie uzna out-parametrów
                    // za przypisane.
                    int bylVanilla = 0;
                    double bylaWartosc = 0;
                    bool byl = _reputation != null &&
                               _reputation.TryGetCel("HEL", "KRW", out bylVanilla, out bylaWartosc);
                    int przed = bylVanilla;
                    double przedW = bylaWartosc;
                    _przywrocenia.Add(() =>
                    {
                        if (byl)
                        {
                            Reputacja("HEL", "KRW", przed, przedW);
                        }
                        else if (_reputation != null)
                        {
                            _reputation.Zapomnij("HEL", "KRW");
                        }
                    });
                    Reputacja("HEL", "KRW", -900, -70);
                },
                CzekajTikow = Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    IMyFactionCollection frakcje = MyAPIGateway.Session.Factions;
                    IMyFaction hel = frakcje.TryGetFactionByTag("HEL");
                    IMyFaction krw = frakcje.TryGetFactionByTag("KRW");
                    if (hel == null || krw == null)
                    {
                        return "brak HEL albo KRW w świecie";
                    }
                    int wGrze = frakcje.GetReputationBetweenFactions(hel.FactionId, krw.FactionId);
                    return wGrze == -900
                        ? ""
                        : "gra ma " + wGrze + " zamiast -900 — polityka frakcji nie doszła";
                },
            });

            // M7 — reputacji frakcji vanilla nie wolno nam ruszać.
            kroki.Add(new Krok
            {
                Nazwa = "reputacja: frakcje vanilla (SPRT/UNIV/…) nietknięte (M7)",
                Grupa = "reputacja",
                Miekki = true,
                Sprawdz = SprawdzObceReputacje,
            });
        }

        // Reputacje frakcji spoza moda sprzed testu — punkt kontrolny do M7.
        private readonly Dictionary<string, int> _obceReputacje = new Dictionary<string, int>();

        private void ZapamietajObceReputacje()
        {
            _obceReputacje.Clear();
            for (int i = 0; i < TagiVanilla.Length; i++)
            {
                if (MyAPIGateway.Session.Factions.TryGetFactionByTag(TagiVanilla[i]) == null)
                {
                    continue;
                }
                _obceReputacje[TagiVanilla[i]] = ReputacjaWGrze(TagiVanilla[i]);
            }
        }

        private string SprawdzObceReputacje()
        {
            if (_obceReputacje.Count == 0)
            {
                return "w świecie nie ma żadnej znanej frakcji vanilla — nie ma czego pilnować";
            }
            foreach (KeyValuePair<string, int> kv in _obceReputacje)
            {
                int teraz = ReputacjaWGrze(kv.Key);
                if (teraz != kv.Value)
                {
                    return kv.Key + ": reputacja zmieniła się z " + kv.Value + " na " + teraz +
                           " — synchronizujemy tylko HEL/KRW/WGR, coś wycieka";
                }
            }
            return "";
        }

        /// <summary>Wysyła do ReputationSync taki sam ładunek, jaki przysłałby brain.</summary>
        private void Reputacja(string tag, string other, int vanilla, double wartosc)
        {
            if (_reputation == null)
            {
                return;
            }
            var data = new Dictionary<string, object>
            {
                { "faction", tag },
                { "other", other ?? "" },
                { "vanilla", (double)vanilla },
                { "value", wartosc },
            };
            _reputation.Handle(data);
        }

        private static int ReputacjaWGrze(string tag)
        {
            IMyFactionCollection frakcje = MyAPIGateway.Session.Factions;
            IMyPlayer gracz = MyAPIGateway.Session.Player;
            if (frakcje == null || gracz == null)
            {
                return 0;
            }
            IMyFaction fac = frakcje.TryGetFactionByTag(tag);
            return fac == null
                ? 0
                : frakcje.GetReputationBetweenPlayerAndFaction(gracz.IdentityId, fac.FactionId);
        }

        /// <summary>Zapis z pominięciem ReputationSync — udajemy grę ruszającą reputację sama.</summary>
        private static void UstawReputacjeWprost(string tag, int wartosc)
        {
            IMyFactionCollection frakcje = MyAPIGateway.Session.Factions;
            IMyPlayer gracz = MyAPIGateway.Session.Player;
            if (frakcje == null || gracz == null)
            {
                return;
            }
            IMyFaction fac = frakcje.TryGetFactionByTag(tag);
            if (fac != null)
            {
                frakcje.SetReputationBetweenPlayerAndFaction(gracz.IdentityId, fac.FactionId, wartosc);
            }
        }

        // ================= SEKCJA: OKUP (K2, K5, K6) =================
        // Żądanie trybutu w surowcach. Samej DOSTAWY towaru autotest nie zrobi (to gracz
        // przekłada ładunek do skrzynki), ale wszystko dookoła — powstanie skrzynki, GPS,
        // odporność na sprzątacz śmieci i skasowanie żądania przy pokoju — jest sprawdzalne.

        private void DodajOkup(List<Krok> kroki)
        {
            const string tag = "KRW";

            kroki.Add(new Krok
            {
                Nazwa = "okup: żądanie trybutu stawia skrzynkę zrzutu (K2)",
                Grupa = "okup",
                Bramka = true,
                Start = () =>
                {
                    if (_ransom == null || MyAPIGateway.Session.Player == null)
                    {
                        return;
                    }
                    // Cokolwiek pójdzie dalej nie tak, żądanie ma zniknąć razem ze skrzynką.
                    _przywrocenia.Add(() => _ransom.Cancel(tag));
                    var data = new Dictionary<string, object>
                    {
                        { "faction", tag },
                        { "item", "Nickel" },
                        { "amount", 700.0 },
                        { "deadline_s", 900.0 },
                    };
                    _ransom.HandleDemand(data, _startTick);
                },
                CzekajTikow = 8 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    if (_ransom == null)
                    {
                        return "RansomManager nie wstał (BeforeStart)";
                    }
                    bool skrzynkaStoi;
                    if (!_ransom.MaPending(tag, out skrzynkaStoi))
                    {
                        return "żądanie nie zostało przyjęte (brak gracza? nieznany surowiec?)";
                    }
                    return skrzynkaStoi
                        ? ""
                        : "żądanie wisi, ale skrzynka nie powstała — sprawdź prefab ZF_DropCrate";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "okup: skrzynka stoi w świecie i ma GPS (K6 — nie zjadł jej sprzątacz)",
                Grupa = "okup",
                Sprawdz = () =>
                {
                    IMyPlayer gracz = MyAPIGateway.Session.Player;
                    if (gracz == null)
                    {
                        return "brak sesji gracza";
                    }
                    long gridId;
                    string gridName;
                    if (!FactionEconomy.TryFindProp(NazwaSkrzynki, gracz.GetPosition(), 0,
                                                    out gridId, out gridName))
                    {
                        return "w świecie nie ma siatki \"" + NazwaSkrzynki + "\" — prefab jest " +
                               "statyczny i ma baterię, więc sprzątacz śmieci nie powinien jej ruszyć";
                    }
                    return MaGps("ZRZUT " + tag)
                        ? ""
                        : "skrzynka stoi, ale nie ma punktu GPS \"ZRZUT " + tag +
                          "\" — gracz jej nie znajdzie";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "okup: pokój kasuje żądanie — skrzynka i GPS znikają (K5)",
                Grupa = "okup",
                Start = () =>
                {
                    if (_ransom != null)
                    {
                        _ransom.Cancel(tag);
                    }
                },
                CzekajTikow = 3 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    if (_ransom == null)
                    {
                        return "RansomManager nie wstał (BeforeStart)";
                    }
                    bool skrzynkaStoi;
                    if (_ransom.MaPending(tag, out skrzynkaStoi))
                    {
                        return "żądanie dalej wisi po Cancel — po deadline przyjdzie kara za " +
                               "złamaną obietnicę mimo zawartego pokoju";
                    }
                    if (MaGps("ZRZUT " + tag))
                    {
                        return "GPS \"ZRZUT " + tag + "\" został na mapie";
                    }
                    IMyPlayer gracz = MyAPIGateway.Session.Player;
                    long gridId;
                    string gridName;
                    if (gracz != null &&
                        FactionEconomy.TryFindProp(NazwaSkrzynki, gracz.GetPosition(), 0,
                                                   out gridId, out gridName))
                    {
                        return "skrzynka zrzutu została w świecie";
                    }
                    return "";
                },
            });
        }

        private static bool MaGps(string nazwa)
        {
            IMyPlayer gracz = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (gracz == null)
            {
                return false;
            }
            List<IMyGps> lista = MyAPIGateway.Session.GPS.GetGpsList(gracz.IdentityId);
            if (lista == null)
            {
                return false;
            }
            for (int i = 0; i < lista.Count; i++)
            {
                if (lista[i] != null && lista[i].Name != null &&
                    lista[i].Name.StartsWith(nazwa, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ================= SEKCJA: FLOTY (O1-O5) =================

        private void DodajFloty(List<Krok> kroki)
        {
            for (int i = 0; i < Tagi.Length; i++)
            {
                string tag = Tagi[i];
                kroki.Add(new Krok
                {
                    Nazwa = "flota " + tag + ": rajd faktycznie staje w świecie",
                    Start = () => TestSpawner.SpawnForFaction(tag, "raid"),
                    CzekajTikow = 8 * Sekunda,
                    Poll = true,
                    Sprawdz = () =>
                    {
                        List<IMyCubeGrid> siatki = TestSpawner.SledzoneSiatki(tag);
                        if (siatki.Count == 0)
                        {
                            if (!TestSpawner.MesAktywny)
                            {
                                return "MES nie jest aktywny — poszła ścieżka awaryjna (goły prefab " +
                                       "vanilla), której nie śledzimy. Floty per frakcja WYMAGAJĄ MES " +
                                       "(1521905890): sprawdź subskrypcję i kolejność modów";
                            }
                            return "żaden statek " + tag + " się nie pojawił — jesteś na planecie " +
                                   "(mod spawnuje rajdy tylko w kosmosie), MES odrzucił miejsce " +
                                   "(safety check) albo grupa ZF_Raid_" + tag + " nie istnieje";
                        }
                        IMyCubeGrid grid = siatki[siatki.Count - 1];
                        return grid == null || grid.MarkedForClose
                            ? "statek " + tag + " zniknął zaraz po spawnie"
                            : "";
                    },
                });

                kroki.Add(new Krok
                {
                    Nazwa = "flota " + tag + ": rajdowy kadłub ma pilota (blok zdalnego sterowania)",
                    Sprawdz = () =>
                    {
                        List<IMyCubeGrid> siatki = TestSpawner.SledzoneSiatki(tag);
                        if (siatki.Count == 0)
                        {
                            return "brak statku do sprawdzenia (poprzedni krok nie przeszedł)";
                        }
                        IMyCubeGrid grid = siatki[siatki.Count - 1];
                        return MaBlok<Sandbox.ModAPI.Ingame.IMyRemoteControl>(grid)
                            ? ""
                            : "kadłub \"" + grid.CustomName + "\" nie ma bloku zdalnego sterowania — " +
                              "RivalAI go nie poprowadzi (manipulacja ZF_ManipulacjaGrupa_Pilot " +
                              "nie zadziałała?), statek będzie dryfował";
                    },
                });
            }

            // O3 — obecność bloku zdalnego sterowania to dopiero POŁOWA odpowiedzi. Pytanie,
            // które naprawdę bolało (dryfujące konwoje), brzmi: czy RivalAI tego bloku UŻYWA.
            // Widać to wyłącznie po tym, że kadłub zmienia pozycję. Statek KRW już stoi
            // w świecie po krokach wyżej — mierzymy jego, bez nowego spawnu.
            DodajRuch(kroki, "KRW", "raid", false,
                      "rajdowy statek KRW LECI (RivalAI prowadzi, nie dryf)");

            // O5 — konwój na zachowaniu ZF_Konwoj ma jechać trasą, a nie dryfować po prostej.
            // Tu spawn jest KONIECZNY: w śledzonych siatkach WGR wisi już rajd z kroków wyżej,
            // a to jego zmierzylibyśmy zamiast konwoju.
            DodajRuch(kroki, "WGR", "convoy", true,
                      "konwój WGR jedzie trasą (ZF_Konwoj + autopilot MES)");

            kroki.Add(new Krok
            {
                Nazwa = "flota: sprzątanie (stand_down wszystkich rajdów autotestu)",
                Start = () =>
                {
                    for (int i = 0; i < Tagi.Length; i++)
                    {
                        TestSpawner.HandleStandDown(Tagi[i]);
                    }
                },
                Sprawdz = () => "",
            });
        }

        /// <summary>
        /// Czy statek tej frakcji faktycznie się przemieszcza. Dryfujący kadłub też zmienia
        /// pozycję, więc próg jest wysoki (200 m w oknie 30 s) — tyle nie da się „przypadkiem"
        /// przelecieć z samej bezwładności po spawnie.
        /// <paramref name="spawnuj"/> mówi, czy zamówić NOWY kadłub (konwój), czy zmierzyć ten,
        /// który stoi już w świecie po wcześniejszych krokach sekcji (rajd).
        /// </summary>
        private void DodajRuch(List<Krok> kroki, string tag, string rodzaj, bool spawnuj, string nazwa)
        {
            const double ProgMetrow = 200;
            // Okno 90 s, nie 30 (2026-08-05). Ten sam krok dał PASS w jednym przebiegu i 96 m
            // w następnym — nie dlatego, że coś się zepsuło, tylko dlatego, że RivalAI musi
            // najpierw namierzyć cel i rozpędzić kadłub, a 30 s bywało krótsze niż ten rozruch.
            // Wydłużenie NIC nie kosztuje, gdy statek leci: krok jest monotoniczny i pollowany,
            // więc zamyka się w chwili przekroczenia progu. Dłużej czekamy tylko wtedy, gdy
            // faktycznie jest na co czekać.
            const int OknoTikow = 90 * Sekunda;

            kroki.Add(new Krok
            {
                Nazwa = "flota: " + nazwa,
                Miekki = true, // bez MES albo na planecie nie ma czego mierzyć
                Start = () =>
                {
                    _flotaGrid = 0;
                    _flotaPrzed = TestSpawner.SledzoneSiatki(tag).Count;
                    if (spawnuj)
                    {
                        // Nowy kadłub złapiemy dopiero, gdy lista śledzonych urośnie —
                        // inaczej mierzylibyśmy statek z poprzednich kroków sekcji.
                        TestSpawner.SpawnForFaction(tag, rodzaj);
                        return;
                    }
                    if (_flotaPrzed == 0)
                    {
                        return;
                    }
                    IMyCubeGrid grid = TestSpawner.SledzoneSiatki(tag)[_flotaPrzed - 1];
                    if (grid != null)
                    {
                        _flotaGrid = grid.EntityId;
                        _flotaPozycja = grid.GetPosition();
                    }
                },
                CzekajTikow = OknoTikow,
                Poll = true,
                Sprawdz = () =>
                {
                    // Statek zamówiony dopiero w Start — złap go przy pierwszym sprawdzeniu.
                    if (_flotaGrid == 0)
                    {
                        List<IMyCubeGrid> siatki = TestSpawner.SledzoneSiatki(tag);
                        if (siatki.Count <= (spawnuj ? _flotaPrzed : 0))
                        {
                            return "statek " + tag + " (" + rodzaj + ") się nie pojawił — patrz " +
                                   "poprzednie kroki sekcji";
                        }
                        IMyCubeGrid swiezy = siatki[siatki.Count - 1];
                        if (swiezy == null)
                        {
                            return "statek zniknął zaraz po spawnie";
                        }
                        _flotaGrid = swiezy.EntityId;
                        _flotaPozycja = swiezy.GetPosition();
                        return "dopiero co złapany — mierzę dystans od teraz";
                    }
                    var grid2 = MyAPIGateway.Entities.GetEntityById(_flotaGrid) as IMyCubeGrid;
                    if (grid2 == null || grid2.MarkedForClose)
                    {
                        // Konwój, który doleciał do końca trasy i zdespawnował, ZDAŁ ten test.
                        return "";
                    }
                    double dystans = Vector3D.Distance(_flotaPozycja, grid2.GetPosition());
                    return dystans >= ProgMetrow
                        ? ""
                        : "przebył " + (int)dystans + " m w oknie " + (OknoTikow / Sekunda) +
                          " s (próg " + (int)ProgMetrow + " m) — kadłub stoi albo dryfuje, " +
                          "autopilot go nie prowadzi";
                },
            });
        }

        // ================= SEKCJA: BOTY (P1-P3, P6) =================

        private void DodajBoty(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga na stacji frakcji (zależność miękka — AiEnabled)",
                MiekkiGdy = () => !AiEnabledWSwiecie || !StacjaWZasieguZalogi,
                CzekajTikow = 5 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    for (int i = 0; i < Tagi.Length; i++)
                    {
                        EconomyBlock stacja = FactionEconomy.FindContractBlock(Tagi[i]);
                        if (stacja == null)
                        {
                            continue;
                        }
                        if (PoliczPostacie(stacja.Position, 150) > 0)
                        {
                            return "";
                        }
                    }
                    if (!AiEnabledWSwiecie)
                    {
                        return "na żadnej stacji nie ma postaci NPC. Moda AiEnabled " +
                               "(2596208372) nie ma w świecie — to zależność miękka i taki " +
                               "wynik jest normalny";
                    }
                    // Mod JEST. Najpierw powiedz, czy w ogóle się z nami przywitał — bez tego
                    // wysyłalibyśmy szukającego w stronę [BotType], choć problem leży piętro
                    // niżej: AiEnabled nie odpowiedziało na rejestrację, więc żadne wywołanie
                    // API nie miało prawa zadziałać.
                    if (!StacjaWZasieguZalogi)
                    {
                        return PowodPozaZasiegiem();
                    }
                    return "na żadnej stacji nie ma postaci NPC, a AiEnabled JEST w świecie. " +
                           (ApiBotowGotowe
                               // NAZWY SĄ JUŻ SPRAWDZONE (2026-08-05) i NIE są przyczyną:
                               // `Default_Astronaut` figuruje w Characters.sbc z Name równym
                               // SubtypeId, a AiEnabled buduje RobotSubtypes właśnie z pola
                               // Name; `Soldier` jest w BotRoleEnemy. Zostaje więc droga
                               // spawnu na stacji, nie słownik nazw.
                               ? "API odpowiedziało na rejestrację, a [BotType] i rola są " +
                                 "poprawne (sprawdzone w plikach gry i AiEnabled). Szukaj " +
                                 "dalej w Crew.cs: IsValidForPathfinding / IsGridMapReady / " +
                                 "GetAvailableGridNodes — stacja musi mieć mapę siatki i wolne " +
                                 "węzły w środku, inaczej SpawnBotQueued nie ma gdzie postawić bota"
                               : "API NIE odpowiedziało na rejestrację (RemoteBotAPI.Valid == " +
                                 "false): to jest przyczyna, a nie nazwy botów. Sprawdź " +
                                 "kolejność modów i czy AiEnabled wstało bez błędu w logu SE");
                },
            });

            // P3 — SEDNO integracji z AiEnabled: bot musi NALEŻEĆ do frakcji, bo tylko wtedy
            // jego wrogość leci po natywnej reputacji, czyli po naszej hybrydzie. Bezpański
            // NPC nigdy nie zmieni nastawienia po zmianie relacji.
            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga należy do frakcji, a nie jest bezpańska (P3)",
                MiekkiGdy = () => !AiEnabledWSwiecie || !StacjaWZasieguZalogi,
                Sprawdz = () =>
                {
                    for (int i = 0; i < Tagi.Length; i++)
                    {
                        EconomyBlock stacja = FactionEconomy.FindContractBlock(Tagi[i]);
                        if (stacja == null)
                        {
                            continue;
                        }
                        int wszystkie, weFrakcji;
                        PoliczZaloge(stacja.Position, 150, Tagi[i], out wszystkie, out weFrakcji);
                        if (wszystkie == 0)
                        {
                            continue;
                        }
                        return weFrakcji > 0
                            ? ""
                            : "przy stacji " + Tagi[i] + " jest " + wszystkie + " postaci NPC, ale " +
                              "ŻADNA nie należy do frakcji — MES/AiEnabled nie zawołało " +
                              "SetPlayersFaction, więc bot nie zareaguje na zmianę relacji";
                    }
                    return StacjaWZasieguZalogi
                        ? "nie ma przy stacjach żadnej postaci NPC do sprawdzenia" +
                          (AiEnabledWSwiecie ? " — a AiEnabled JEST w świecie" : " (brak AiEnabled)")
                        : PowodPozaZasiegiem();
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga na statku rajdowym (trigger PlayerNear 1,5 km)",
                // UWAGA: ten krok NIE zależy od odległości do STACJI (poprawka 2026-08-05).
                // Załogę na statku stawia MES przez trigger PlayerNear 1500 m na samym
                // kadłubie, więc liczy się dystans do STATKU. Podpięcie tu bramki
                // stacyjnej ukrywałoby prawdziwą porażkę: gracz stojący 200 m od rajdu
                // ma prawo oczekiwać załogi niezależnie od tego, gdzie stoją stacje.
                MiekkiGdy = () => !AiEnabledWSwiecie,
                Start = () =>
                {
                    // Zapamiętujemy STAN SPRZED zamówienia (2026-08-05). Bez tego krok brał
                    // ostatnią śledzoną siatkę KRW — a `CustomSpawnRequest` jest asynchroniczny,
                    // więc przez pierwsze sekundy była nią siatka, która stała w świecie WCZEŚNIEJ.
                    // W praktyce trafiał na KONWÓJ z ręcznego `/zf raid KRW` (brain bez podanego
                    // rodzaju dobiera flotę do nastroju frakcji i potrafi wybrać `convoy`), czyli
                    // mierzył załogę na transportowcu, który z definicji ŻADNEJ nie ma:
                    // `BehaviorKonwoj` nie zawiera `[Triggers:...]`. Krok meldował wtedy porażkę
                    // botów, choć sprawdzał nie ten statek.
                    _botyPrzed = TestSpawner.SledzoneSiatki("KRW").Count;
                    TestSpawner.SpawnForFaction("KRW", "raid");
                },
                CzekajTikow = 40 * Sekunda,
                Poll = true,
                Sprawdz = () =>
                {
                    List<IMyCubeGrid> siatki = TestSpawner.SledzoneSiatki("KRW");
                    if (siatki.Count <= _botyPrzed)
                    {
                        return "zamówiony RAJD KRW jeszcze nie stanął — bez niego nie ma czego " +
                               "sprawdzać (konwój z wcześniejszego spawnu się NIE liczy, " +
                               "transportowiec nie ma triggera załogi)";
                    }
                    IMyCubeGrid grid = siatki[siatki.Count - 1];
                    if (grid == null || grid.MarkedForClose)
                    {
                        return "statek KRW zniknął przed sprawdzeniem";
                    }
                    // Trigger PlayerNear mierzy dystans do KADŁUBA. Gdy gracz jest dalej, to nie
                    // jest porażka botów — to brak warunku, i trzeba to powiedzieć wprost.
                    IMyPlayer gracz = MyAPIGateway.Session.Player;
                    double dystans = gracz == null
                        ? -1
                        : Vector3D.Distance(gracz.GetPosition(), grid.GetPosition());
                    if (dystans > 1500)
                    {
                        return "NIE DA SIĘ SPRAWDZIĆ STĄD: rajd KRW stanął " + (int)dystans +
                               " m stąd, a trigger załogi (PlayerNear) sięga 1500 m. Podleć " +
                               "bliżej i powtórz `/zf autotest boty`.";
                    }
                    int ile = PoliczPostacie(grid.GetPosition(), 200);
                    return ile > 0 ? "" : "na pokładzie RAJDU nie ma nikogo mimo dystansu " +
                                          (int)dystans + " m (profil ZF_Bot_KRW_* / akcja " +
                                          "AddBotsToGrid / APIs.AiEnabled.Valid po stronie MES)";
                },
            });

            // P6 — załoga nie może się mnożyć przy każdym przebiegu CrewSpawnera. Idempotencja
            // jest liczona po POSTACIACH w promieniu, nie po zapisie w storage, więc jedyny
            // sposób, by to sprawdzić, to policzyć dwa razy w odstępie dłuższym niż jego cykl.
            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga się nie mnoży przy kolejnych przebiegach (P6)",
                MiekkiGdy = () => !AiEnabledWSwiecie || !StacjaWZasieguZalogi,
                Start = () => { _zalogaPrzed = PoliczZalogeStacji(); },
                CzekajTikow = 12 * Sekunda,
                Sprawdz = () =>
                {
                    if (_zalogaPrzed == 0)
                    {
                        return StacjaWZasieguZalogi
                            ? "nie ma przy stacjach załogi do policzenia" +
                              (AiEnabledWSwiecie ? " — a AiEnabled JEST w świecie" : " (brak AiEnabled)")
                            : PowodPozaZasiegiem();
                    }
                    int teraz = PoliczZalogeStacji();
                    return teraz <= _zalogaPrzed
                        ? ""
                        : "załoga urosła z " + _zalogaPrzed + " do " + teraz +
                          " bez powodu — CrewSpawner dosypuje botów przy każdym przebiegu";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "boty: sprzątanie (stand_down KRW)",
                Start = () => TestSpawner.HandleStandDown("KRW"),
                Sprawdz = () => "",
            });
        }

        // ================= POMOCNIKI =================

        /// <summary>Pierwsza oferta w sklepie frakcji — punkt odniesienia dla sekcji „ceny".</summary>
        private static bool PierwszaOferta(string tag, out long blokId, out long ofertaId, out int cena,
                                           out int ilosc, out int ofert)
        {
            return SzukajOferty(tag, 0, 0, out blokId, out ofertaId, out cena, out ilosc, out ofert);
        }

        /// <summary>Ta sama oferta co poprzednio (klucz: blok + id oferty) po zmianie cennika.</summary>
        private static bool ZnajdzOferte(string tag, long blokId, long ofertaId, out int cena,
                                         out int ilosc, out int ofert)
        {
            long ignoredBlok, ignoredOferta;
            return SzukajOferty(tag, blokId, ofertaId, out ignoredBlok, out ignoredOferta, out cena,
                                out ilosc, out ofert);
        }

        /// <summary>Oferta w KONKRETNYM bloku, bez wiedzy o frakcji — do kontroli obcych sklepów.</summary>
        private static bool SzukajOfertyWBloku(long blokId, long ofertaId, out int cena, out int ilosc,
                                               out int ofert)
        {
            cena = 0;
            ilosc = 0;
            ofert = 0;
            var sklep = MyAPIGateway.Entities.GetEntityById(blokId) as IMyStoreBlock;
            if (sklep == null)
            {
                return false;
            }
            var pozycje = new List<IMyStoreItem>();
            sklep.GetStoreItems(pozycje);
            ofert = pozycje.Count;
            for (int i = 0; i < pozycje.Count; i++)
            {
                if (pozycje[i] == null || pozycje[i].Id != ofertaId)
                {
                    continue;
                }
                cena = pozycje[i].PricePerUnit;
                ilosc = pozycje[i].Amount;
                return true;
            }
            return false;
        }

        // Ten sam sposób dobierania się do ofert co PriceManager: MODOWY IMyStoreBlock
        // (ten z Ingame ma tylko Insert/Cancel/GetPlayerStoreItems i nie widzi ofert gry).
        private static bool SzukajOferty(string tag, long szukanyBlok, long szukanaOferta,
                                         out long blokId, out long ofertaId, out int cena,
                                         out int ilosc, out int ofert)
        {
            blokId = 0;
            ofertaId = 0;
            cena = 0;
            ilosc = 0;
            ofert = 0;
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }

            var pozycje = new List<IMyStoreItem>();
            var bloki = new List<IMySlimBlock>();
            List<IMyCubeGrid> siatki = FactionEconomy.FactionGrids(tag);
            for (int g = 0; g < siatki.Count; g++)
            {
                bloki.Clear();
                siatki[g].GetBlocks(bloki);
                for (int b = 0; b < bloki.Count; b++)
                {
                    IMyStoreBlock sklep = SklepZBloku(bloki[b]);
                    if (sklep == null)
                    {
                        continue;
                    }
                    if (szukanyBlok != 0 && sklep.EntityId != szukanyBlok)
                    {
                        continue;
                    }
                    pozycje.Clear();
                    sklep.GetStoreItems(pozycje);
                    ofert = pozycje.Count;
                    for (int i = 0; i < pozycje.Count; i++)
                    {
                        IMyStoreItem pozycja = pozycje[i];
                        if (pozycja == null)
                        {
                            continue;
                        }
                        if (szukanaOferta != 0 && pozycja.Id != szukanaOferta)
                        {
                            continue;
                        }
                        blokId = sklep.EntityId;
                        ofertaId = pozycja.Id;
                        cena = pozycja.PricePerUnit;
                        ilosc = pozycja.Amount;
                        return true;
                    }
                }
            }
            return false;
        }

        private static IMyStoreBlock SklepZBloku(IMySlimBlock slim)
        {
            IMyCubeBlock fat = slim == null ? null : slim.FatBlock;
            if (fat == null)
            {
                return null;
            }
            string typeId = fat.BlockDefinition.TypeIdString;
            if (typeId == null ||
                typeId.IndexOf(FactionEconomy.StoreType, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }
            return fat as IMyStoreBlock;
        }

        private static bool MaBlok<T>(IMyCubeGrid grid) where T : class
        {
            if (grid == null)
            {
                return false;
            }
            var bloki = new List<IMySlimBlock>();
            grid.GetBlocks(bloki);
            for (int i = 0; i < bloki.Count; i++)
            {
                if (bloki[i].FatBlock as T != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Postacie NPC (nie gracz) w promieniu — tak samo liczy je CrewSpawner.</summary>
        private static int PoliczPostacie(Vector3D srodek, double promien)
        {
            int wszystkie, ignored;
            PoliczZaloge(srodek, promien, null, out wszystkie, out ignored);
            return wszystkie;
        }

        /// <summary>
        /// Postacie NPC w promieniu, z podziałem na „w ogóle" i „należące do frakcji"
        /// <paramref name="tag"/>. Przynależność idzie przez tożsamość kontrolera — tą samą
        /// drogą, którą atrybucja bojowa rozpoznaje właściciela (CombatEvents.cs).
        /// </summary>
        private static void PoliczZaloge(Vector3D srodek, double promien, string tag,
                                         out int wszystkie, out int weFrakcji)
        {
            wszystkie = 0;
            weFrakcji = 0;
            var kula = new BoundingSphereD(srodek, promien);
            List<VRage.ModAPI.IMyEntity> encje = MyAPIGateway.Entities.GetEntitiesInSphere(ref kula);
            for (int i = 0; i < encje.Count; i++)
            {
                var postac = encje[i] as IMyCharacter;
                if (postac == null || postac.IsPlayer)
                {
                    continue;
                }
                wszystkie++;
                if (tag == null || postac.ControllerInfo == null)
                {
                    continue;
                }
                IMyFaction fac = MyAPIGateway.Session.Factions.TryGetPlayerFaction(
                    postac.ControllerInfo.ControllingIdentityId);
                if (fac != null && fac.Tag == tag)
                {
                    weFrakcji++;
                }
            }
        }

        /// <summary>Łączna załoga przy wszystkich stacjach naszych frakcji (P6).</summary>
        private static int PoliczZalogeStacji()
        {
            int razem = 0;
            for (int i = 0; i < Tagi.Length; i++)
            {
                EconomyBlock stacja = FactionEconomy.FindContractBlock(Tagi[i]);
                if (stacja != null)
                {
                    razem += PoliczPostacie(stacja.Position, 150);
                }
            }
            return razem;
        }

        private static void Powiedz(string tekst)
        {
            MyAPIGateway.Utilities.ShowMessage("AUTOTEST", tekst);
        }
    }
}
