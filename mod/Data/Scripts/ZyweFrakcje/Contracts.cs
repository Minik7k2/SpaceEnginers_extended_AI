using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Contracts;
using VRage.Game;
using VRage.Game.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>
    /// Etap 6 — kontrakty. Brain decyduje KIEDY i ZA ILE (commands.jsonl: contract_create),
    /// mod tworzy kontrakt w grze przez MyAPIGateway.ContractSystem i odsyła prawdziwe ID
    /// (events.jsonl: contract_created). Rozliczenie wraca jako contract_done.
    ///
    /// Kontrakt powstaje na bloku kontraktów (albo sklepie) NALEŻĄCYM DO FRAKCJI — patrz
    /// <see cref="FactionEconomy.TryFindContractBlock"/>. Bez takiego bloku nie ma gdzie go
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
        /// Tworzy kontrakt frakcji w grze. reward w kredytach, duration w minutach.
        /// Kaucja (collateral) to 1/10 nagrody — świat mściwy: zawalone zlecenie ma boleć
        /// nie tylko relacją.
        /// </summary>
        public void Create(string faction, string kind, long reward, int durationMin)
        {
            if (MyAPIGateway.ContractSystem == null)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Kontrakty niedostępne w tej wersji gry (brak ContractSystem)");
                return;
            }

            long blockId;
            string gridName;
            if (!FactionEconomy.TryFindContractBlock(faction, out blockId, out gridName))
            {
                MyAPIGateway.Utilities.ShowMessage("ZF",
                    "Kontrakt " + faction + " pominięty: frakcja nie ma bloku kontraktów ani sklepu (postaw stację frakcji)");
                return;
            }

            MyDefinitionId itemId;
            int amount;
            string opis;
            ItemForFaction(faction, out itemId, out amount, out opis);

            int money = reward > int.MaxValue ? int.MaxValue : (int)reward;
            int collateral = money / 10;
            int durationSeconds = durationMin * 60; // API bierze sekundy

            // ID kontraktu znamy dopiero PO AddContract, a callbacki trzeba ustawić WCZEŚNIEJ —
            // stąd jednoelementowa tablica jako uchwyt domknięcia.
            long[] idBox = new long[1];
            var contract = new MyContractAcquisition(blockId, money, collateral, durationSeconds,
                                                     blockId, itemId, amount);
            contract.OnContractSucceeded = () => Finish(idBox[0], true);
            contract.OnContractFailed = () => Finish(idBox[0], false);

            MyAddContractResultWrapper result = MyAPIGateway.ContractSystem.AddContract(contract);
            if (!result.Success)
            {
                MyAPIGateway.Utilities.ShowMessage("ZF", "Gra odrzuciła kontrakt frakcji " + faction);
                return;
            }
            idBox[0] = result.ContractId;

            var tracked = new Tracked { Id = result.ContractId, Faction = faction, Kind = kind };
            _tracked.Add(tracked);
            SaveState();

            _events.WriteContractCreated(result.ContractId.ToString(), faction, kind, reward, opis);
            MyAPIGateway.Utilities.ShowMessage("ZF",
                "Nowe zlecenie " + faction + ": " + opis + " za " + reward + " kr (" + (gridName ?? "stacja") + ")");
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
