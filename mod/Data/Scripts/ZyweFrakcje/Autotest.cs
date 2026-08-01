using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Samosprawdzanie w grze — komenda <c>/zf autotest [sekcja]</c> (2026-08-01).
    ///
    /// PO CO TO JEST. Duża część mechaniki opiera się na założeniach o ModAPI, których
    /// nie da się potwierdzić poza grą: czy <c>GetStoreItems</c> w ogóle coś zwróci, czy
    /// <c>PricePerUnit</c> jest naprawdę zapisywalne, czy <c>SpawningOptions</c> ustawia
    /// flagę <c>IsNpcSpawnedGrid</c>, czy MES postawi kadłub z naszej grupy. Do tej pory
    /// każde takie pytanie kosztowało osobny przebieg ręcznej listy z docs/testy-reczne.md
    /// — wczytanie świata, dolot, patrzenie w terminal. Autotest zadaje te pytania sam
    /// i odpowiada PASS/FAIL, a wynik idzie na czat ORAZ do events.jsonl (typ
    /// <c>autotest_result</c>), więc konsola brainu pokazuje komplet.
    ///
    /// CZEGO NIE ZASTĄPI: rzeczy wymagających człowieka za sterami — dolecieć do rekwizytu,
    /// złapać go podwoziem, przyjąć zlecenie w terminalu, ocenić, czy radio brzmi sensownie.
    /// Te zostają w docs/testy-reczne.md.
    ///
    /// Sekcje są rozdzielone celowo: „szybkie" (stacje, ceny, rekwizyt) nic nie psują
    /// i nie ściągają na gracza wrogów, a „floty"/„boty" spawnują prawdziwe statki rajdowe
    /// i wymagają kosmosu. Bez argumentu lecą tylko szybkie.
    ///
    /// Konstrukcja: prosta maszyna kroków, bo połowa sprawdzeń jest asynchroniczna
    /// (SpawnPrefab woła callback, MES stawia statek po chwili, AiEnabled buduje mapę
    /// siatki). Każdy krok ma Start, czas oczekiwania i sprawdzenie.
    /// </summary>
    internal sealed class Autotest
    {
        private const int Sekunda = 60; // tików przy 60 Hz

        // Stała, a nie literał w kodzie: tools/waliduj_sbc.py sprawdza, czy nazwa prefabu
        // spawnowanego przez mod naprawdę istnieje w mod/Data/Prefabs.
        private const string RekwizytPrefab = "ZF_Zgubka";

        private static readonly string[] Tagi = { "HEL", "KRW", "WGR" };

        /// <summary>Jeden krok testu: zrób coś, odczekaj, sprawdź.</summary>
        private sealed class Krok
        {
            public string Nazwa;
            public Action Start;
            // Zwraca pusty string = PASS, tekst = opis niepowodzenia.
            public Func<string> Sprawdz;
            public int CzekajTikow;
            // Zależność miękka (AiEnabled): niepowodzenie ma być OSTRZEŻENIEM, nie błędem.
            public bool Miekki;
        }

        private readonly EventWriter _events;
        private readonly PriceManager _prices;

        private List<Krok> _kroki;
        private string _sekcja;
        private int _index;
        private int _startTick;
        private bool _wystartowal;
        private int _pass;
        private int _fail;
        private int _warn;

        // Stan dzielony między krokami sekcji „ceny" — bazowa oferta, na której mierzymy.
        private string _cenyTag;
        private long _cenyBlok;
        private long _cenyOferta;
        private int _cenyBaza;
        private int _cenyIlosc;

        // Siatki do posprzątania po teście (rekwizyty), żeby autotest nie zaśmiecał świata.
        private readonly List<IMyCubeGrid> _doSprzatniecia = new List<IMyCubeGrid>();

        public Autotest(EventWriter events, PriceManager prices)
        {
            _events = events;
            _prices = prices;
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
                Powiedz("autotest już trwa (sekcja " + _sekcja + ") — poczekaj na podsumowanie.");
                return;
            }

            var kroki = new List<Krok>();
            switch (sekcja)
            {
                case "szybkie":
                    DodajStacje(kroki);
                    DodajCeny(kroki);
                    DodajRekwizyt(kroki);
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
                case "floty":
                    DodajFloty(kroki);
                    break;
                case "boty":
                    DodajBoty(kroki);
                    break;
                case "wszystko":
                    DodajStacje(kroki);
                    DodajCeny(kroki);
                    DodajRekwizyt(kroki);
                    DodajFloty(kroki);
                    DodajBoty(kroki);
                    break;
                default:
                    Powiedz("Nieznana sekcja \"" + sekcja + "\". Dozwolone: szybkie (domyślnie), " +
                            "stacje, ceny, rekwizyt, floty, boty, wszystko.");
                    return;
            }

            _sekcja = sekcja;
            _kroki = kroki;
            _index = 0;
            _wystartowal = false;
            _pass = 0;
            _fail = 0;
            _warn = 0;
            Powiedz("=== AUTOTEST [" + sekcja + "]: " + kroki.Count + " sprawdzeń ===");
            if (sekcja == "floty" || sekcja == "boty" || sekcja == "wszystko")
            {
                Powiedz("UWAGA: ta sekcja spawnuje prawdziwe statki rajdowe — rób ją w KOSMOSIE " +
                        "i na świecie testowym.");
            }
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

        private void Zglos(Krok krok, string blad)
        {
            string wynik;
            if (string.IsNullOrEmpty(blad))
            {
                wynik = "PASS";
                _pass++;
            }
            else if (krok.Miekki)
            {
                wynik = "OSTRZEŻENIE";
                _warn++;
            }
            else
            {
                wynik = "FAIL";
                _fail++;
            }

            Powiedz(wynik + " " + krok.Nazwa + (string.IsNullOrEmpty(blad) ? "" : " — " + blad));

            // Ten sam wynik do events.jsonl: konsola brainu ma komplet obok reszty zdarzeń,
            // a plik zostaje jako ślad po przebiegu (do wklejenia w zgłoszeniu).
            if (_events != null && !_events.Failed)
            {
                var data = new Dictionary<string, object>
                {
                    { "sekcja", _sekcja },
                    { "nazwa", krok.Nazwa },
                    { "wynik", wynik },
                    { "opis", blad ?? "" },
                };
                var obj = new Dictionary<string, object>
                {
                    { "type", "autotest_result" },
                    { "data", data },
                };
                _events.WriteRawEvent(Json.Stringify(obj));
            }
        }

        private void Podsumuj()
        {
            Powiedz("=== AUTOTEST [" + _sekcja + "] koniec: " + _pass + " PASS, " + _fail +
                    " FAIL, " + _warn + " OSTRZEŻEŃ ===");
            for (int i = 0; i < _doSprzatniecia.Count; i++)
            {
                if (_doSprzatniecia[i] != null && !_doSprzatniecia[i].MarkedForClose)
                {
                    _doSprzatniecia[i].Close();
                }
            }
            _doSprzatniecia.Clear();
            _kroki = null;
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
                                   "postawił stacji (czekaj ~30 s) albo spawn się nie udał";
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
            }
        }

        // ================= SEKCJA: CENY (N1-N8) =================
        // To jest test rozstrzygający dla całej sekcji N: sygnatury IMyStoreBlock nie były
        // potwierdzone dekompilacją, więc pierwsze pytanie brzmi „czy w ogóle widzimy oferty".

        private void DodajCeny(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "ceny: mod widzi oferty w sklepie frakcji (GetStoreItems)",
                Sprawdz = () =>
                {
                    if (_prices == null)
                    {
                        return "PriceManager nie wstał (BeforeStart) — cennika nie ma czym ruszyć";
                    }
                    for (int i = 0; i < Tagi.Length; i++)
                    {
                        int ofert;
                        if (PierwszaOferta(Tagi[i], out _cenyBlok, out _cenyOferta, out _cenyBaza,
                                           out _cenyIlosc, out ofert) && ofert > 0)
                        {
                            _cenyTag = Tagi[i];
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
                Start = () => Cennik(1.5, false, -50),
                CzekajTikow = Sekunda,
                Sprawdz = () => SprawdzCene(1.5),
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: mnożnik liczony od BAZY, nie od bieżącej (N5 — brak składania)",
                Start = () => Cennik(2.0, false, -80),
                CzekajTikow = Sekunda,
                // Gdyby mnożnik składał się z poprzednim, zobaczylibyśmy baza*1.5*2.0.
                Sprawdz = () => SprawdzCene(2.0),
            });

            kroki.Add(new Krok
            {
                Nazwa = "ceny: embargo zdejmuje towar ze sklepu (Amount = 0)",
                Start = () => Cennik(2.0, true, -80),
                CzekajTikow = Sekunda,
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
                Start = () => Cennik(1.0, false, 0),
                CzekajTikow = Sekunda,
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
        }

        /// <summary>Wysyła do PriceManagera taki sam ładunek, jaki przysłałby brain.</summary>
        private void Cennik(double mnoznik, bool embargo, double relacja)
        {
            if (_prices == null || string.IsNullOrEmpty(_cenyTag))
            {
                return;
            }
            var data = new Dictionary<string, object>
            {
                { "faction", _cenyTag },
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

        private void DodajRekwizyt(List<Krok> kroki)
        {
            var wynik = new List<IMyCubeGrid>();
            kroki.Add(new Krok
            {
                Nazwa = "rekwizyt: SpawningOptions.SetNpcSpawnedGrid naprawdę ustawia flagę",
                Start = () =>
                {
                    IMyPlayer gracz = MyAPIGateway.Session.Player;
                    if (gracz == null || gracz.Character == null)
                    {
                        return;
                    }
                    string ignored;
                    long wlasciciel = FactionEconomy.FindTargetIdentity("WGR", out ignored);
                    MatrixD widok = gracz.Character.WorldMatrix;
                    Vector3D pozycja = widok.Translation + widok.Forward * 300;
                    Vector3D? wolne = MyAPIGateway.Entities.FindFreePlace(pozycja, 30);
                    if (wolne.HasValue)
                    {
                        pozycja = wolne.Value;
                    }
                    wynik.Clear();
                    MyAPIGateway.PrefabManager.SpawnPrefab(
                        wynik, RekwizytPrefab, pozycja, (Vector3)widok.Forward, (Vector3)widok.Up,
                        Vector3.Zero, Vector3.Zero, null,
                        SpawningOptions.SetNpcSpawnedGrid, wlasciciel, true, null);
                },
                CzekajTikow = 3 * Sekunda,
                Sprawdz = () =>
                {
                    if (wynik.Count == 0)
                    {
                        return "prefab " + RekwizytPrefab + " nie powstał — czy mod/Data/Prefabs się wczytał?";
                    }
                    IMyCubeGrid grid = wynik[0];
                    _doSprzatniecia.Add(grid);
                    if (!grid.IsNpcSpawnedGrid)
                    {
                        return "grid NIE ma flagi IsNpcSpawnedGrid — zlecenia poszukiwań będą się " +
                               "zawalać sekundę po przyjęciu (MyContractFind.Update woła Fail())";
                    }
                    return "";
                },
            });
        }

        // ================= SEKCJA: FLOTY (nowe, 2026-08-01) =================

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

        // ================= SEKCJA: BOTY (nowe, 2026-08-01) =================

        private void DodajBoty(List<Krok> kroki)
        {
            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga na stacji frakcji (zależność miękka — AiEnabled)",
                Miekki = true,
                CzekajTikow = 5 * Sekunda,
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
                    return "na żadnej stacji nie ma postaci NPC. Bez moda AiEnabled (2596208372) " +
                           "to normalne. Z nim: pierwsze podejrzane jest [BotType] w Crew.cs / " +
                           "ZF_Boty.sbc — wiki MES mówi, że to pole Name z SBC, a nie SubtypeId";
                },
            });

            kroki.Add(new Krok
            {
                Nazwa = "boty: załoga na statku rajdowym (trigger PlayerNear 1,5 km)",
                Miekki = true,
                Start = () => TestSpawner.SpawnForFaction("KRW", "raid"),
                CzekajTikow = 20 * Sekunda,
                Sprawdz = () =>
                {
                    List<IMyCubeGrid> siatki = TestSpawner.SledzoneSiatki("KRW");
                    if (siatki.Count == 0)
                    {
                        return "nie udało się postawić statku KRW (patrz sekcja floty)";
                    }
                    IMyCubeGrid grid = siatki[siatki.Count - 1];
                    if (grid == null || grid.MarkedForClose)
                    {
                        return "statek KRW zniknął przed sprawdzeniem";
                    }
                    int ile = PoliczPostacie(grid.GetPosition(), 200);
                    return ile > 0 ? "" : "na pokładzie nie ma nikogo (profil ZF_Bot_KRW_* / akcja " +
                                          "AddBotsToGrid / trigger PlayerNear — podejdź bliżej niż 1,5 km)";
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
                    IMyCubeBlock fat = bloki[b].FatBlock;
                    if (fat == null)
                    {
                        continue;
                    }
                    string typeId = fat.BlockDefinition.TypeIdString;
                    if (typeId == null ||
                        typeId.IndexOf(FactionEconomy.StoreType, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    var sklep = fat as IMyStoreBlock;
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
            var kula = new BoundingSphereD(srodek, promien);
            List<VRage.ModAPI.IMyEntity> encje = MyAPIGateway.Entities.GetEntitiesInSphere(ref kula);
            int ile = 0;
            for (int i = 0; i < encje.Count; i++)
            {
                var postac = encje[i] as IMyCharacter;
                if (postac != null && !postac.IsPlayer)
                {
                    ile++;
                }
            }
            return ile;
        }

        private static void Powiedz(string tekst)
        {
            MyAPIGateway.Utilities.ShowMessage("AUTOTEST", tekst);
        }
    }
}
