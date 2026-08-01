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
        private const int CheckEveryTicks = 3600;  // ~60 s — boty są drogie, nie ma pośpiechu
        private const int RetryTicks = 36000;      // ~10 min karencji na stację
        private const int CrewPerStation = 3;
        private const double CrewRadius = 150;     // w tym promieniu liczymy „załogę tej stacji"

        // Załogę stawiamy dopiero, gdy gracz jest w okolicy: postacie kosztują, a nikt ich
        // nie zobaczy z drugiego końca układu. Ten sam pomysł co trigger PlayerNear na statkach.
        private const double PlayerRange = 3000;

        private static readonly string[] Tags = { "HEL", "KRW", "WGR" };

        // Rola AiEnabled per frakcja. „Soldier" to rola wroga — bot jest członkiem frakcji,
        // więc do gracza strzela dopiero wtedy, gdy reputacja jest wroga (patrz ZF_Boty.sbc).
        private static readonly string[] Role = { "Soldier", "Soldier", "Soldier" };
        private static readonly string[] BotType = { "Police_Bot", "Police_Bot", "Police_Bot" };

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
