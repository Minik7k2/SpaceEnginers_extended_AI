using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>
    /// HYBRYDA reputacji: nasz silnik relacji (brain, skala -100..+100) zostaje źródłem
    /// prawdy, ale jego wynik jest PRZEPISYWANY na natywną reputację SE (-1500..+1500).
    /// Dzięki temu gracz widzi stan relacji tam, gdzie się go spodziewa — w zwykłym oknie
    /// frakcji — a wieżyczki, ceny w sklepach i strefy stacji reagują na nasze wojny.
    ///
    /// Kierunek jest JEDNOSTRONNY (brain -> gra). Wartości docelowe pilnujemy co ~5 s i
    /// nadpisujemy, gdy gra zmieni je po swojemu (np. nagroda reputacyjna za kontrakt
    /// vanilla) — inaczej nasza liczba i ta z okna frakcji znowu by się rozjechały, a
    /// zmiany byłyby liczone dwa razy (raz przez nas w brainie, raz przez ekonomię gry).
    ///
    /// Wartości docelowe żyją tylko w pamięci: po wczytaniu świata mod pisze session_start,
    /// a brain odsyła komplet reputation_sync (patrz docs/protocol.md).
    /// </summary>
    internal sealed class ReputationSync
    {
        // Co ile tików sprawdzamy, czy gra nie ruszyła reputacji po swojemu (~5 s przy 60 Hz).
        private const int RecheckEveryTicks = 300;

        // Progi ETYKIET GRY (dokumentacja SE): <= -500 wróg, >= +500 sojusznik. To progi
        // vanilli, nie nasze — brain celuje w nie swoim odwzorowaniem ([reputacja] w rules.toml).
        private const int VanillaEnemy = -500;
        private const int VanillaAlly = 500;

        private struct Cel
        {
            public string Faction;
            public string Other;   // pusty = relacja do gracza; inaczej tag drugiej frakcji
            public int Vanilla;
            public double Value;   // nasza skala — tylko do raportu /zf rep
        }

        private readonly Dictionary<string, Cel> _cele = new Dictionary<string, Cel>();
        // Klucze do usunięcia po przejściu pętli — słownika nie wolno modyfikować w foreach.
        private readonly List<string> _doUsuniecia = new List<string>();

        /// <summary>Komenda reputation_sync z brainu: zapamiętaj cel i przepisz go do gry.</summary>
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

            object otherObj;
            data.TryGetValue("other", out otherObj);
            string other = otherObj as string ?? "";

            // JSON liczby parsujemy jako double (Json.ParseNumber) — stąd rzuty.
            object vanillaObj;
            if (!data.TryGetValue("vanilla", out vanillaObj) || !(vanillaObj is double))
            {
                return; // bez wartości docelowej nie ma czego ustawiać
            }
            var cel = new Cel();
            cel.Faction = faction;
            cel.Other = other;
            cel.Vanilla = (int)(double)vanillaObj;

            object valueObj;
            if (data.TryGetValue("value", out valueObj) && valueObj is double)
            {
                cel.Value = (double)valueObj;
            }

            _cele[Key(faction, other)] = cel;
            if (!Apply(cel))
            {
                _cele.Remove(Key(faction, other));
            }
        }

        /// <summary>
        /// Pilnowanie celów: gra (kontrakty, ekonomia) potrafi ruszyć reputację sama, a
        /// frakcja albo gracz mogą jeszcze nie istnieć w chwili, gdy przyszła komenda.
        /// </summary>
        public void Update(int tick)
        {
            if (tick % RecheckEveryTicks != 0 || _cele.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<string, Cel> kv in _cele)
            {
                if (!Apply(kv.Value))
                {
                    _doUsuniecia.Add(kv.Key);
                }
            }
            for (int i = 0; i < _doUsuniecia.Count; i++)
            {
                _cele.Remove(_doUsuniecia[i]);
            }
            _doUsuniecia.Clear();
        }

        /// <summary>
        /// Cel przysłany przez brain — dla `/zf autotest reputacja`. Autotest podmienia cele
        /// na własne (żeby sprawdzić zapis, progi i przywracanie), więc musi najpierw móc
        /// zapamiętać, co tu stało, i po teście to oddać. false = brain nic dla tej pary
        /// nie przysłał (np. nie działa) i po teście trzeba cel ZAPOMNIEĆ, nie odtwarzać.
        /// </summary>
        public bool TryGetCel(string faction, string other, out int vanilla, out double value)
        {
            vanilla = 0;
            value = 0;
            Cel cel;
            if (!_cele.TryGetValue(Key(faction, other ?? ""), out cel))
            {
                return false;
            }
            vanilla = cel.Vanilla;
            value = cel.Value;
            return true;
        }

        /// <summary>
        /// Przestań pilnować celu (autotest sprząta po sobie wpisy, których brain nie zna).
        /// Wartość w grze zostaje taka, jaka jest — kolejny reputation_sync ją poprawi.
        /// </summary>
        public void Zapomnij(string faction, string other)
        {
            _cele.Remove(Key(faction, other ?? ""));
        }

        /// <summary>"/zf rep" — co brain chce mieć w grze i co gra ma naprawdę.</summary>
        public void Report()
        {
            IMyFactionCollection factions = MyAPIGateway.Session.Factions;
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (factions == null || player == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Reputacja: brak sesji gracza.");
                return;
            }
            if (_cele.Count == 0)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Reputacja: brain nie przysłał jeszcze żadnej wartości (czy brain działa?).");
                return;
            }

            foreach (KeyValuePair<string, Cel> kv in _cele)
            {
                Cel cel = kv.Value;
                IMyFaction fac = factions.TryGetFactionByTag(cel.Faction);
                if (fac == null)
                {
                    MyAPIGateway.Utilities.ShowMessage("ZF", cel.Faction + ": brak frakcji w świecie");
                    continue;
                }

                string kto;
                int wGrze;
                if (string.IsNullOrEmpty(cel.Other))
                {
                    kto = cel.Faction + " -> gracz";
                    wGrze = factions.GetReputationBetweenPlayerAndFaction(player.IdentityId, fac.FactionId);
                }
                else
                {
                    IMyFaction other = factions.TryGetFactionByTag(cel.Other);
                    if (other == null)
                    {
                        continue;
                    }
                    kto = cel.Faction + " -> " + cel.Other;
                    wGrze = factions.GetReputationBetweenFactions(fac.FactionId, other.FactionId);
                }

                string zgodne = wGrze == cel.Vanilla ? "" : " (ROZJAZD, cel " + cel.Vanilla + ")";
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    kto + ": " + wGrze + " " + Tier(wGrze) + " | brain " +
                    cel.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + zgodne);
            }
        }

        /// <summary>
        /// Przepisuje jeden cel do gry. false = API zawiodło i celu nie ma sensu ponawiać
        /// (wołający go wtedy zapomina); brak frakcji/gracza to zwykłe „jeszcze nie teraz".
        /// </summary>
        private bool Apply(Cel cel)
        {
            IMyFactionCollection factions = MyAPIGateway.Session.Factions;
            if (factions == null)
            {
                return true;
            }
            IMyFaction fac = factions.TryGetFactionByTag(cel.Faction);
            if (fac == null)
            {
                // Frakcje z Factions.sbc powstają przy GENEROWANIU świata (IsDefault) —
                // na starym zapisie ich nie ma. Nie krzyczymy co 5 s, po prostu czekamy.
                return true;
            }

            try
            {
                if (string.IsNullOrEmpty(cel.Other))
                {
                    IMyPlayer player = MyAPIGateway.Session.Player;
                    if (player == null)
                    {
                        return true;
                    }
                    if (factions.GetReputationBetweenPlayerAndFaction(player.IdentityId, fac.FactionId) !=
                        cel.Vanilla)
                    {
                        factions.SetReputationBetweenPlayerAndFaction(player.IdentityId, fac.FactionId,
                            cel.Vanilla);
                    }
                }
                else
                {
                    IMyFaction other = factions.TryGetFactionByTag(cel.Other);
                    if (other == null)
                    {
                        return true;
                    }
                    if (factions.GetReputationBetweenFactions(fac.FactionId, other.FactionId) != cel.Vanilla)
                    {
                        // W grze reputacja pary jest symetryczna — jedno wywołanie ustawia obie strony.
                        factions.SetReputation(fac.FactionId, other.FactionId, cel.Vanilla);
                    }
                }
            }
            catch (Exception e)
            {
                // Reputacja to ozdoba mechaniki, nie jej rdzeń: gdyby API zawiodło,
                // gra ma działać dalej (reguła: mostek nie wywala sesji).
                MyAPIGateway.Utilities.ShowMessage("ZF", "Reputacja " + cel.Faction + ": błąd zapisu (" +
                    e.Message + ")");
                return false;
            }
            return true;
        }

        private static string Key(string faction, string other)
        {
            return string.IsNullOrEmpty(other) ? faction : faction + "|" + other;
        }

        private static string Tier(int reputation)
        {
            if (reputation <= VanillaEnemy)
            {
                return "(wróg)";
            }
            if (reputation >= VanillaAlly)
            {
                return "(sojusznik)";
            }
            return "(neutralny)";
        }
    }
}
