using System;
using System.Collections.Generic;
using Sandbox.ModAPI;

namespace ZyweFrakcje
{
    /// <summary>
    /// Kolejka radiowa (Etap 5a). Brain dostarcza radio_message paczkami przez commands.jsonl;
    /// tu ustawiamy je w kolejkę PRIORYTETOWĄ i wyświetlamy z odstępem, żeby seria wiadomości
    /// nie mignęła jednym kłębem. Bojowe (priority>=1) wyprzedzają gadkę.
    ///
    /// TTL: wiadomości starsze niż 2 min porzucamy (liczone z brainowego "ts"). Chroni przed
    /// zaległymi echami z poprzedniej sesji — kolejka commands.jsonl przeżywa restart świata,
    /// a nie chcemy witać gracza minutami starego radia.
    ///
    /// Limit 1/min/frakcję poza walką narzuca już brain (engine.emit) — tu tylko prezentacja.
    ///
    /// Kolor frakcji: brain niesie pole "color" (blue/red/yellow), ale kolorowanego CZATU nie da
    /// się zrobić na whiteliście ModAPI (MyVisualScriptLogicProvider poza whitelistą). Wyświetlamy
    /// więc klienckim MyAPIGateway.Utilities.ShowMessage (biało, jak Etap 1); color trzymamy w
    /// kolejce pod ewentualny kolorowy HUD (ShowNotification) w przyszłości. Patrz docs/testy-reczne G.
    /// </summary>
    internal sealed class RadioDisplay
    {
        private const long TtlMs = 120000;         // 2 min — spec Etapu 5
        private const int ShowIntervalTicks = 20;  // ~0.3 s odstępu między wyświetleniami serii
        private const int MaxQueue = 64;           // zawór bezpieczeństwa; TTL i tak dławi wzrost

        // DateTimeOffset jest poza whitelistą ModAPI — epokę liczymy z DateTime (jak EventWriter).
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private struct Item
        {
            public string Faction;
            public string Text;
            public string Color;
            public int Priority;
            public long Ts;   // znacznik z brainu (unix ms) — do TTL i kolejności w obrębie priorytetu
        }

        private readonly List<Item> _queue = new List<Item>();
        private int _lastShownTick = int.MinValue;

        public void Enqueue(string faction, string text, string color, int priority, long tsMs)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            _queue.Add(new Item
            {
                Faction = string.IsNullOrEmpty(faction) ? "???" : faction,
                Text = text,
                Color = color,
                Priority = priority,
                Ts = tsMs > 0 ? tsMs : NowMs(),
            });
            // Zawór: gdyby coś zalało kolejkę, usuń najstarszą (po ts) — TTL zwykle wystarcza.
            while (_queue.Count > MaxQueue)
            {
                _queue.RemoveAt(IndexOfOldest());
            }
        }

        /// <summary>Woła sesja co tik. Porządkuje, dławi tempo i wypuszcza jedną wiadomość.</summary>
        public void Update(int tick)
        {
            if (_queue.Count == 0)
            {
                return;
            }

            long now = NowMs();
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (now - _queue[i].Ts > TtlMs)
                {
                    _queue.RemoveAt(i); // przeterminowane (zaległe echa itp.)
                }
            }
            if (_queue.Count == 0)
            {
                return;
            }

            // Odstęp między wyświetleniami, żeby paczka z pollu nie zalała czatu na raz.
            if (_lastShownTick != int.MinValue && tick - _lastShownTick < ShowIntervalTicks)
            {
                return;
            }

            int best = IndexOfBest();
            Item item = _queue[best];
            _queue.RemoveAt(best);
            Show(item);
            _lastShownTick = tick;
        }

        // Najwyższy priorytet; przy remisie najstarsza (najniższy ts) idzie pierwsza.
        private int IndexOfBest()
        {
            int best = 0;
            for (int i = 1; i < _queue.Count; i++)
            {
                bool wyzszy = _queue[i].Priority > _queue[best].Priority;
                bool remisStarszy = _queue[i].Priority == _queue[best].Priority && _queue[i].Ts < _queue[best].Ts;
                if (wyzszy || remisStarszy)
                {
                    best = i;
                }
            }
            return best;
        }

        private int IndexOfOldest()
        {
            int oldest = 0;
            for (int i = 1; i < _queue.Count; i++)
            {
                if (_queue[i].Ts < _queue[oldest].Ts)
                {
                    oldest = i;
                }
            }
            return oldest;
        }

        private static void Show(Item item)
        {
            // Kliencki, whitelistowany czat (jak Etap 1). Nazwa nadawcy niesie [RADIO | TAG],
            // więc format z testów (A1/B1/B2) zostaje; czat SE rysuje białym.
            MyAPIGateway.Utilities.ShowMessage("RADIO | " + item.Faction, item.Text);
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;
        }
    }
}
