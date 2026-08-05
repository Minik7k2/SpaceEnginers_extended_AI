using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;            // MyPositionAndOrientation
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ZyweFrakcje
{
    /// <summary>
    /// Załoga na stacjach frakcji (Etap B, 2026-08-01) — droga PROGRAMOWA przez AiEnabled.
    ///
    /// Czemu nie deklaratywnie jak na statkach: profile botów MES działają dla siatek
    /// spawnowanych PRZEZ MES, a nasze stacje stawia własny <see cref="StationSpawner"/>
    /// przez PrefabManager. Dlatego tutaj wołamy API wprost — a przy okazji dostajemy to,
    /// czego droga deklaratywna nie da: własne imiona botów i uchwyty (entityId) do
    /// wydawania im rozkazów, gdy brain zacznie sterować postaciami (Etap C/D).
    ///
    /// ZALEŻNOŚĆ MIĘKKA: bez moda AiEnabled (Workshop 2596208372) API zgłasza się jako
    /// niegotowe i po prostu nie stawiamy nikogo. Gra działa jak dotąd, gracz dostaje
    /// jedno ostrzeżenie na sesję i tyle.
    ///
    /// Idempotencja tak samo jak przy stacjach: stan świata, nie plik. Liczymy postacie
    /// wokół stacji i dostawiamy do celu, więc wczytanie świata nie zdubluje załogi,
    /// a wybici bądź wygasli wracają po karencji.
    /// </summary>
    internal sealed class CrewSpawner
    {
        // 15 s, nie 60: statek rajdowy potrafi zostać wycofany (stand_down) szybciej, niż
        // doczekałby się załogi, a autotest daje na to okno 40 s.
        private const int CheckEveryTicks = 900;
        // Zasięg dla STATKÓW — ten sam próg co trigger PlayerNear w ZF_Boty.sbc (1,5 km).
        private const double StatekRange = 1500;
        private const int RetryTicks = 36000;      // ~10 min karencji na stację
        private const int CrewPerStation = 3;
        private const double CrewRadius = 150;     // w tym promieniu liczymy „załogę tej stacji"

        // Załogę stawiamy dopiero, gdy gracz jest w okolicy: postacie kosztują, a nikt ich
        // nie zobaczy z drugiego końca układu. Ten sam pomysł co trigger PlayerNear na statkach.
        private const double PlayerRange = 3000;

        private static readonly string[] Tags = { "HEL", "KRW", "WGR" };

        // Rola AiEnabled per frakcja. „Soldier" to rola wroga — bot jest członkiem frakcji,
        // więc do gracza strzela dopiero wtedy, gdy reputacja jest wroga (patrz ZF_Boty.sbc).
        // Role są WERYFIKOWANE (2026-08-02, kod AiEnabled): AllowedBotRoles to suma enumów
        // BotRoleFriendly/Enemy/Neutral, a porównanie idzie przez role.ToUpperInvariant(),
        // więc „Soldier" trafia w SOLDIER, a „Grinder" w GRINDER.
        private static readonly string[] Role = { "Soldier", "Soldier", "Soldier" };

        // PUŁAPKA (naprawione 2026-08-02): było tu „Police_Bot" — nazwa wzięta z opisu moda
        // na Workshopie. TAKIEJ POSTACI NIE MA ani w grze, ani w AiEnabled (sprawdzone
        // grepem po Content i po plikach moda). AiEnabled buduje AllowedBotSubtypes z
        // MyDefinitionManager.Static.Characters (`charDef.Name ?? SubtypeId`), a
        // BotFactory.CreateBotObject przerywa spawn dla podtypu spoza tej listy —
        // zapisując ostrzeżenie WYŁĄCZNIE do własnego logu AiEnabled. Stąd „nikogo nie ma
        // na pokładzie" bez śladu w logu SE.
        // Default_Astronaut to domyślny wybór samego AiEnabled (CreateBotObject podstawia
        // go za pusty subType) i Skeleton=Humanoid, czyli bot chodzi i używa narzędzi.
        private static readonly string[] BotType =
            { "Default_Astronaut", "Default_Astronaut", "Default_Astronaut" };

        // Kolory frakcji — te same, co w Factions.sbc.
        private static readonly Color[] Kolor =
        {
            new Color(40, 90, 160),   // HEL — niebieski
            new Color(150, 30, 30),   // KRW — czerwony
            new Color(190, 150, 40),  // WGR — żółty
        };

        private readonly RemoteBotAPI _api;
        private readonly Dictionary<long, int> _nextTry = new Dictionary<long, int>();
        private bool _ostrzezono;

        public CrewSpawner()
        {
            // Konstruktor RemoteBotAPI rejestruje handler wiadomości i czeka na odpowiedź
            // AiEnabled — dlatego Valid bywa fałszywe przez pierwsze tiki po wczytaniu świata.
            _api = new RemoteBotAPI();
        }

        /// <summary>
        /// Czy AiEnabled ODPOWIEDZIAŁO na rejestrację (odesłało słownik API).
        ///
        /// UWAGA — to NIE jest to samo co „mod jest w świecie" (2026-08-05). `Valid` ustawia
        /// się dopiero w odpowiedzi na nasz komunikat rejestracyjny, więc `false` znaczy albo
        /// „moda nie ma", albo „mod jest, ale uścisk dłoni nie doszedł do skutku" — a to drugie
        /// jest właśnie najciekawsze, bo oznacza, że botów nie będzie mimo zasubskrybowanego
        /// moda. Obecność samego moda sprawdza <see cref="Autotest"/> po liście modów świata.
        /// </summary>
        public bool ApiZarejestrowane
        {
            get { return _api != null && _api.Valid; }
        }

        /// <summary>Czy API zgłasza gotowość do stawiania botów (osobny etap po rejestracji).</summary>
        public bool ApiGotowe
        {
            get { return _api != null && _api.Valid && _api.CanSpawn; }
        }

        /// <summary>Zasięg gracza, w którym w ogóle stawiamy załogę (metry).</summary>
        public static double ZasiegZalogi { get { return PlayerRange; } }

        /// <summary>
        /// Dystans do NAJBLIŻSZEJ stacji frakcji, albo -1 gdy żadna nie stoi.
        ///
        /// Potrzebne autotestowi, bo tu leży pułapka, która przez trzy przebiegi udawała błąd
        /// nazw botów (2026-08-05): StationSpawner stawia stacje 8–15 km od gracza, a załogę
        /// dokładamy tylko w promieniu <see cref="ZasiegZalogi"/> (3 km). Świeżo postawiona
        /// stacja jest więc ZAWSZE poza zasięgiem załogi, dopóki gracz do niej nie doleci —
        /// i to jest zachowanie zamierzone (nie stawiamy botów, których nikt nie zobaczy).
        /// Autotest nie umie tam polecieć, więc nie wolno mu z tego robić FAIL-a.
        /// </summary>
        public static double DystansDoNajblizszejStacji()
        {
            IMyPlayer gracz = MyAPIGateway.Session.Player;
            if (gracz == null || gracz.Character == null)
            {
                return -1;
            }
            Vector3D pozycja = gracz.GetPosition();
            double best = -1;
            for (int i = 0; i < Tags.Length; i++)
            {
                EconomyBlock stacja = FactionEconomy.FindContractBlock(Tags[i]);
                if (stacja == null)
                {
                    continue;
                }
                double d = Vector3D.Distance(pozycja, stacja.Position);
                if (best < 0 || d < best)
                {
                    best = d;
                }
            }
            return best;
        }

        /// <summary>
        /// `/zf zaloga` — przechodzi tę samą ścieżkę co <see cref="Uzupelnij"/> i MELDUJE każdy
        /// warunek. Powstało 2026-08-05: boty nie pojawiały się także na stacji, przy graczu
        /// w zasięgu, a wszystkie gałęzie odmowy były ciche albo prawie ciche — nie było jak
        /// odróżnić „API milczy" od „brak mapy siatki" od „brak wolnych węzłów".
        /// Bierze NAJBLIŻSZĄ siatkę frakcji: stację albo statek, co akurat jest pod ręką.
        /// </summary>
        public void Diagnostyka()
        {
            Powiedz("=== diagnostyka załogi ===");
            Powiedz("AiEnabled API: Valid=" + (_api != null && _api.Valid) +
                    ", CanSpawn=" + (_api != null && _api.Valid && _api.CanSpawn));
            if (_api == null || !_api.Valid)
            {
                Powiedz("KONIEC: bez zarejestrowanego API nie ma o czym mówić.");
                return;
            }

            IMyPlayer gracz = MyAPIGateway.Session.Player;
            if (gracz == null || gracz.Character == null)
            {
                Powiedz("KONIEC: brak gracza.");
                return;
            }
            Vector3D pozycjaGracza = gracz.GetPosition();

            IMyCubeGrid cel = null;
            string celTag = null;
            double celDystans = -1;
            for (int i = 0; i < Tags.Length; i++)
            {
                var kandydaci = new List<IMyCubeGrid>(FactionEconomy.FactionGrids(Tags[i]));
                foreach (IMyCubeGrid g in kandydaci)
                {
                    if (g == null || g.MarkedForClose || g.GridSizeEnum != MyCubeSize.Large)
                    {
                        continue;
                    }
                    double d = Vector3D.Distance(pozycjaGracza, g.GetPosition());
                    if (celDystans < 0 || d < celDystans)
                    {
                        cel = g; celTag = Tags[i]; celDystans = d;
                    }
                }
            }
            if (cel == null)
            {
                Powiedz("KONIEC: w świecie nie ma ŻADNEJ dużej siatki naszych frakcji.");
                return;
            }
            Powiedz("cel: " + (cel.DisplayName ?? "?") + " (" + celTag + "), " +
                    (int)celDystans + " m stąd, bloków=" + LiczBloki(cel));

            var duza = cel as MyCubeGrid;
            if (duza == null)
            {
                Powiedz("STOP: siatka nie jest MyCubeGrid (nie da się zbudować mapy).");
                return;
            }
            bool pathfinding = _api.IsValidForPathfinding(cel);
            Powiedz("IsValidForPathfinding: " + pathfinding);
            if (!pathfinding)
            {
                Powiedz("STOP: AiEnabled uważa tę siatkę za nienadającą się pod boty.");
                return;
            }
            bool mapa = _api.IsGridMapReady(duza);
            Powiedz("IsGridMapReady: " + mapa);
            if (!mapa)
            {
                _api.CreateGridMap(duza);
                Powiedz("Zamówiłem budowę mapy siatki — poczekaj kilkanaście sekund " +
                        "i powtórz `/zf zaloga`.");
                return;
            }

            var wezly = new List<Vector3D>();
            _api.GetAvailableGridNodes(duza, CrewPerStation, wezly, null, false);
            Powiedz("GetAvailableGridNodes: " + wezly.Count + " wolnych węzłów");
            if (wezly.Count == 0)
            {
                Powiedz("STOP: nie ma gdzie postawić bota (brak wnętrza / węzłów).");
                return;
            }

            string ignored;
            long wlasciciel = FactionEconomy.FindTargetIdentity(celTag, out ignored);
            Powiedz("właściciel dla botów: " + wlasciciel);
            if (wlasciciel == 0)
            {
                Powiedz("STOP: brak tożsamości właściciela — bot byłby bezpański.");
                return;
            }

            int index = Array.IndexOf(Tags, celTag);
            Powiedz("próbuję postawić 1 bota: typ=" + BotType[index] + ", rola=" + Role[index]);
            _api.SpawnBotQueued(BotType[index], "ZF Test",
                                new MyPositionAndOrientation(wezly[0], Vector3.Forward, Vector3.Up),
                                duza, Role[index], wlasciciel, null, null);
            Powiedz("Zlecenie wysłane do AiEnabled. Jeśli bot się nie pojawi w ~10 s, powód " +
                    "będzie w Storage/2596208372.sbm_AiEnabled/AiEnabled.log");
        }

        private static int LiczBloki(IMyCubeGrid grid)
        {
            var b = new List<IMySlimBlock>();
            grid.GetBlocks(b);
            return b.Count;
        }

        private static void Powiedz(string tekst)
        {
            MyAPIGateway.Utilities.ShowMessage("ZAŁOGA", tekst);
        }

        public void Dispose()
        {
            if (_api != null)
            {
                _api.Close();
            }
        }

        public void Update(int tick)
        {
            if (tick % CheckEveryTicks != 0)
            {
                return;
            }
            if (_api == null || !_api.Valid)
            {
                return; // brak AiEnabled — cicho, to jest zależność opcjonalna
            }
            if (!_api.CanSpawn)
            {
                // AiEnabled bywa zarejestrowane, ale jeszcze nie gotowe stawiać botów
                // (LocalBotAPI.SpawnBot loguje wtedy „received SpawnBot command before mod
                // was ready" i zwraca null). Poczekamy do następnego przebiegu.
                return;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return;
            }
            Vector3D pozycjaGracza = player.GetPosition();

            for (int i = 0; i < Tags.Length; i++)
            {
                EconomyBlock stacja = FactionEconomy.FindContractBlock(Tags[i]);
                if (stacja == null)
                {
                    continue; // frakcja nie ma jeszcze stacji (patrz StationSpawner)
                }
                if (Vector3D.DistanceSquared(pozycjaGracza, stacja.Position) > PlayerRange * PlayerRange)
                {
                    continue; // za daleko, żeby ktokolwiek to zobaczył
                }
                int next;
                if (_nextTry.TryGetValue(stacja.GridId, out next) && tick < next)
                {
                    continue;
                }
                _nextTry[stacja.GridId] = tick + RetryTicks;
                Uzupelnij(i, stacja);
                return; // jedna stacja na przebieg
            }

            ZalogaNaStatkach(tick, pozycjaGracza);
        }

        /// <summary>
        /// Załoga na statkach rajdowych — PROGRAMOWO, tak samo jak na stacjach (2026-08-05).
        ///
        /// Dlaczego nie przez MES, skoro `ZF_Boty.sbc` ma komplet profili, akcji i triggerów:
        /// bo ta droga nie działa dla NASZYCH kadłubów i wiemy dlaczego. Duże prefaby vanilli
        /// (Vulture, Enforcer, Armed Tender…) NIE MAJĄ bloku zdalnego sterowania, więc w chwili
        /// spawnu MES nie ma gdzie zapisać zachowania ani podpiąć triggerów. Blok dokłada nasz
        /// `TestSpawner.EnsurePilot` DOPIERO w callbacku po spawnie — RivalAI odczytuje z niego
        /// `[BehaviorName:Fighter]` (statek faktycznie leci, mamy to potwierdzone w autoteście),
        /// ale `[Triggers:...]` jest już wtedy po herbacie: lista triggerów zachowania została
        /// zamknięta wcześniej. Objaw: w logu AiEnabled NIE MA ANI JEDNEJ próby spawnu bota —
        /// czyli nikt o nią nie prosi, a nie że AiEnabled odmawia.
        ///
        /// Zamiast walczyć z kolejnością faz w cudzym modzie, prosimy sami. Uchwyt do AiEnabled
        /// już mamy i już działa (patrz <see cref="ApiZarejestrowane"/>), a `Uzupelnij` jest ten
        /// sam co dla stacji — buduje mapę siatki, znajduje wolne węzły i stawia załogę.
        /// Profile w `ZF_Boty.sbc` zostają: są poprawne, kosztują tyle co nic i zadziałają same,
        /// gdyby kiedyś trafił się kadłub z własnym blokiem zdalnego sterowania.
        /// </summary>
        private void ZalogaNaStatkach(int tick, Vector3D pozycjaGracza)
        {
            for (int i = 0; i < Tags.Length; i++)
            {
                List<IMyCubeGrid> statki = TestSpawner.SledzoneSiatki(Tags[i]);
                for (int s = 0; s < statki.Count; s++)
                {
                    IMyCubeGrid grid = statki[s];
                    if (grid == null || grid.MarkedForClose)
                    {
                        continue;
                    }
                    // Boty nie chodzą po małych siatkach — patrole odpadają z definicji.
                    if (grid.GridSizeEnum != MyCubeSize.Large)
                    {
                        continue;
                    }
                    Vector3D pozycja = grid.GetPosition();
                    // Ten sam próg co trigger PlayerNear w ZF_Boty.sbc, żeby zachowanie było
                    // jedno, niezależnie od tego, która droga bota postawi.
                    if (Vector3D.DistanceSquared(pozycjaGracza, pozycja) > StatekRange * StatekRange)
                    {
                        continue;
                    }
                    int next;
                    if (_nextTry.TryGetValue(grid.EntityId, out next) && tick < next)
                    {
                        continue;
                    }
                    _nextTry[grid.EntityId] = tick + RetryTicks;
                    Uzupelnij(i, new EconomyBlock
                    {
                        GridId = grid.EntityId,
                        Position = pozycja,
                        GridName = grid.DisplayName,
                    });
                    return; // jeden statek na przebieg
                }
            }
        }

        private void Uzupelnij(int index, EconomyBlock stacja)
        {
            var grid = MyAPIGateway.Entities.GetEntityById(stacja.GridId) as IMyCubeGrid;
            if (grid == null)
            {
                return;
            }
            // Bez mapy siatki bot nie ma po czym chodzić. AiEnabled buduje ją asynchronicznie,
            // więc przy pierwszym podejściu zwykle jeszcze jej nie ma — zamawiamy i wracamy
            // przy następnym przebiegu.
            var duzaSiatka = grid as MyCubeGrid;
            if (duzaSiatka == null || !_api.IsValidForPathfinding(grid))
            {
                Ostrzez("stacja " + Tags[index] + " nie nadaje się pod boty (pathfinding)");
                return;
            }
            if (!_api.IsGridMapReady(duzaSiatka))
            {
                _api.CreateGridMap(duzaSiatka);
                _nextTry[stacja.GridId] = 0; // mapa w budowie — spróbuj przy najbliższej okazji
                return;
            }

            int brakuje = CrewPerStation - PolicZaloge(stacja);
            if (brakuje <= 0)
            {
                return;
            }

            var wezly = new List<Vector3D>();
            _api.GetAvailableGridNodes(duzaSiatka, brakuje, wezly, null, false);
            if (wezly.Count == 0)
            {
                Ostrzez("na stacji " + Tags[index] + " nie ma wolnych miejsc dla załogi");
                return;
            }

            string ignored;
            long owner = FactionEconomy.FindTargetIdentity(Tags[index], out ignored);
            if (owner == 0)
            {
                return;
            }

            for (int i = 0; i < wezly.Count && i < brakuje; i++)
            {
                var pozycja = new MyPositionAndOrientation(wezly[i], Vector3.Forward, Vector3.Up);
                // Imię jest tu tylko etykietą; docelowo (Etap C) postać ma mieć rekord
                // w SQLite po stronie brainu, a to imię ma z niego pochodzić.
                string imie = Imie(Tags[index], i);
                _api.SpawnBotQueued(BotType[index], imie, pozycja, duzaSiatka,
                                    Role[index], owner, Kolor[index], null);
            }
        }

        /// <summary>Ile postaci frakcji kręci się już przy tej stacji.</summary>
        private static int PolicZaloge(EconomyBlock stacja)
        {
            var kula = new BoundingSphereD(stacja.Position, CrewRadius);
            List<VRage.ModAPI.IMyEntity> encje =
                MyAPIGateway.Entities.GetEntitiesInSphere(ref kula);
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

        private static string Imie(string tag, int nr)
        {
            switch (tag)
            {
                case "KRW": return "Krwawa Reka " + (nr + 1);
                case "WGR": return "Wolni Gornicy " + (nr + 1);
                default: return "Ochrona Helionu " + (nr + 1);
            }
        }

        private void Ostrzez(string tresc)
        {
            if (_ostrzezono)
            {
                return;
            }
            _ostrzezono = true;
            MyAPIGateway.Utilities.ShowMessage("ZF", "UWAGA: " + tresc + ".");
        }
    }
}
