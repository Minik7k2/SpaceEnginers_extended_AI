using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>
    /// Narzędzie testowe: <c>/zf daj &lt;surowiec&gt; [ilość]</c> — wrzuca przedmiot do
    /// inwentarza gracza. Po co: bez trybu eksperymentalnego survival nie daje jak zrobić
    /// 700 sztabek niklu na test trybutu (B+), a przeklikiwanie rafinerii zabija tempo
    /// testów. Nic w grze tego nie woła — tylko komenda czatu.
    ///
    /// Składnia: sam klucz to SZTABKA (<c>/zf daj nikiel 700</c>), z prefiksem rodziny
    /// dowolny inny przedmiot (<c>/zf daj Ore/Ice 100</c>, <c>/zf daj Component/SteelPlate 50</c>).
    /// Przyjmuje polskie nazwy i SubtypeId z gry.
    /// </summary>
    internal static class DebugGive
    {
        private const int DefaultAmount = 100;
        private const int MaxAmount = 1000000;

        // Luźna nazwa (małe litery) -> SubtypeId z gry.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "zelazo", "Iron" }, { "żelazo", "Iron" }, { "iron", "Iron" },
            { "nikiel", "Nickel" }, { "niklu", "Nickel" }, { "nickel", "Nickel" },
            { "krzem", "Silicon" }, { "silicon", "Silicon" },
            { "kobalt", "Cobalt" }, { "cobalt", "Cobalt" },
            { "srebro", "Silver" }, { "silver", "Silver" },
            { "zloto", "Gold" }, { "złoto", "Gold" }, { "gold", "Gold" },
            { "platyna", "Platinum" }, { "platinum", "Platinum" },
            { "magnez", "Magnesium" }, { "magnesium", "Magnesium" },
            { "uran", "Uranium" }, { "uranium", "Uranium" },
            { "kamien", "Stone" }, { "kamień", "Stone" }, { "stone", "Stone" },
            { "lod", "Ice" }, { "lód", "Ice" }, { "ice", "Ice" },
        };

        /// <summary>Argumenty po prefiksie "/zf daj".</summary>
        public static void Handle(string args)
        {
            string[] parts = (args ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                Show("Użycie: /zf daj <surowiec> [ilość] — np. /zf daj nikiel 700 (domyślnie " +
                     DefaultAmount + "). Inna rodzina: /zf daj Ore/Ice 100, /zf daj Component/SteelPlate 50");
                return;
            }

            int amount = DefaultAmount;
            if (parts.Length >= 2 && !int.TryParse(parts[1], out amount))
            {
                Show("\"" + parts[1] + "\" to nie jest liczba");
                return;
            }
            if (amount < 1)
            {
                amount = 1;
            }
            if (amount > MaxAmount)
            {
                amount = MaxAmount;
            }

            // "Ore/Nickel" -> rodzina + podtyp; sam "nikiel" -> sztabka.
            string family = "Ingot";
            string name = parts[0];
            int slash = name.IndexOf('/');
            if (slash > 0)
            {
                family = Family(name.Substring(0, slash));
                name = name.Substring(slash + 1);
                if (family == null)
                {
                    Show("nieznana rodzina przedmiotu — użyj Ingot/, Ore/ albo Component/");
                    return;
                }
            }
            string subtype = Subtype(name);

            MyObjectBuilder_PhysicalObject content = NewContent(family);
            if (content == null)
            {
                Show("nieznana rodzina przedmiotu — użyj Ingot/, Ore/ albo Component/");
                return;
            }
            content.SubtypeName = subtype;

            MyDefinitionId def;
            string fullId = "MyObjectBuilder_" + family + "/" + subtype;
            try
            {
                def = MyDefinitionId.Parse(fullId);
            }
            catch (Exception)
            {
                Show("nie umiem rozpoznać przedmiotu \"" + fullId + "\"");
                return;
            }

            IMyInventory inv = PlayerInventory();
            if (inv == null)
            {
                Show("brak inwentarza gracza (nie ma postaci?)");
                return;
            }

            // Bez odpytywania definicji (MyDefinitionManager poza naszym zakresem ModAPI):
            // liczymy stan przed i po. Zły podtyp albo pełny plecak widać po różnicy.
            float before = (float)inv.GetItemAmount(def);
            try
            {
                inv.AddItems((MyFixedPoint)amount, content);
            }
            catch (Exception e)
            {
                Show("nie udało się dodać " + fullId + ": " + e.Message);
                return;
            }
            float after = (float)inv.GetItemAmount(def);
            int added = (int)(after - before);

            if (added <= 0)
            {
                Show("nic nie weszło (" + fullId + ") — zły podtyp albo pełny inwentarz");
                return;
            }
            if (added < amount)
            {
                Show("dodano " + added + " z " + amount + " (" + fullId + ") — reszta się nie zmieściła");
                return;
            }
            Show("dodano " + added + "x " + fullId + " (w inwentarzu: " + (int)after + ")");
        }

        private static IMyInventory PlayerInventory()
        {
            if (MyAPIGateway.Session == null)
            {
                return null;
            }
            IMyPlayer player = MyAPIGateway.Session.Player;
            if (player == null || player.Character == null)
            {
                return null;
            }
            IMyEntity character = player.Character;
            if (!character.HasInventory)
            {
                return null;
            }
            return character.GetInventory(0);
        }

        private static string Family(string raw)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ingot":
                case "sztabka":
                case "sztabki": return "Ingot";
                case "ore":
                case "ruda": return "Ore";
                case "component":
                case "komponent": return "Component";
                default: return null;
            }
        }

        private static MyObjectBuilder_PhysicalObject NewContent(string family)
        {
            switch (family)
            {
                case "Ingot": return new MyObjectBuilder_Ingot();
                case "Ore": return new MyObjectBuilder_Ore();
                case "Component": return new MyObjectBuilder_Component();
                default: return null;
            }
        }

        // Polska nazwa -> SubtypeId; nieznane zostawiamy z dużej litery ("steelplate" i tak
        // nie przejdzie, ale "SteelPlate" wpisane wprost — owszem).
        private static string Subtype(string raw)
        {
            string alias;
            if (Aliases.TryGetValue(raw.ToLowerInvariant(), out alias))
            {
                return alias;
            }
            if (raw.Length > 0)
            {
                return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
            }
            return raw;
        }

        private static void Show(string text)
        {
            MyAPIGateway.Utilities.ShowMessage("ZF", text);
        }
    }
}
