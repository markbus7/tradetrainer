// =====================================================================
//  Trade Trainer Reader -- cTrader cBot
//
//  The "Reads the chart" Coach from Trade Trainer (markbus7.github.io/
//  tradetrainer), ported line for line: it finds the 11 Al Brooks setups
//  the app teaches (bull and bear) from CLOSED bars only, and trades them
//  the way the app's auto-backtest does:
//    - entry: a stop order just beyond the signal bar (range edges: a limit
//      at the signal bar's close),
//    - stop: beyond the structure the setup rests on, never inside the
//      signal bar; target: the setup's own target,
//    - skipped when the trade does not pay at the setup's taught odds,
//    - risk: a fixed % of equity per trade, one trade at a time,
//    - an unfilled order is cancelled after 6 bars; a setup the market has
//      already run through is not chased; a fill that lands (almost) on its
//      stop, or past its own target, is closed straight away.
//
//  HOW TO USE
//    cTrader > Algo > New cBot > paste this whole file > Build.
//    Add one instance per chart (symbol + timeframe you want it to trade).
//    Backtest it first -- years of data, with your broker's spread and
//    commission -- and read the per-setup report it prints at the end.
//    For BTCUSD on weekends only, set "Days to open trades" to Weekends_only.
//
//  WARNING: the app tuned these rules on generated charts. They have never
//  been tested on real market data. Backtest and demo-trade before risking
//  real money.
// =====================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TradeDays { Every_day, Weekdays_only, Weekends_only }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class TradeTrainerReader : Robot
    {
        [Parameter("Risk per trade (% of equity)", Group = "Risk", DefaultValue = 1.0, MinValue = 0.05, MaxValue = 5.0, Step = 0.05)]
        public double RiskPercent { get; set; }

        [Parameter("Skip if spread > (% of ATR)", Group = "Risk", DefaultValue = 25, MinValue = 1, MaxValue = 200)]
        public double MaxSpreadPctAtr { get; set; }

        [Parameter("Days to open trades", Group = "Schedule", DefaultValue = TradeDays.Every_day)]
        public TradeDays Days { get; set; }

        [Parameter("Cancel unfilled order after (bars)", Group = "Orders", DefaultValue = 6, MinValue = 1, MaxValue = 50)]
        public int ExpiryBars { get; set; }

        [Parameter("Label", Group = "Orders", DefaultValue = "TT Reader")]
        public string OrderLabel { get; set; }

        [Parameter("Draw setups on the chart", Group = "Display", DefaultValue = true)]
        public bool DrawSetups { get; set; }

        [Parameter("Failed breakout", Group = "Setups", DefaultValue = true)] public bool UseFailedBo { get; set; }
        [Parameter("Breakout with follow-through", Group = "Setups", DefaultValue = true)] public bool UseSuccessBo { get; set; }
        [Parameter("Major trend reversal", Group = "Setups", DefaultValue = true)] public bool UseMtr { get; set; }
        [Parameter("Final flag", Group = "Setups", DefaultValue = true)] public bool UseFinalFlag { get; set; }
        [Parameter("Wedge (three pushes)", Group = "Setups", DefaultValue = true)] public bool UseWedge { get; set; }
        [Parameter("Climax", Group = "Setups", DefaultValue = true)] public bool UseClimax { get; set; }
        [Parameter("Spike and channel", Group = "Setups", DefaultValue = true)] public bool UseSpike { get; set; }
        [Parameter("Two-legged pullback", Group = "Setups", DefaultValue = true)] public bool UseTwoLeg { get; set; }
        [Parameter("Tight channel", Group = "Setups", DefaultValue = true)] public bool UseTight { get; set; }
        [Parameter("Broad channel", Group = "Setups", DefaultValue = true)] public bool UseBroad { get; set; }
        [Parameter("Trading range edges", Group = "Setups", DefaultValue = true)] public bool UseRange { get; set; }

        sealed class Plan { public ReaderSignal Sig; public double Risk; }
        sealed class Tally { public int N, Wins; public double R; }

        ReaderCore core;
        Plan working;                                   // the order we have out, if any
        readonly Dictionary<int, Plan> open = new Dictionary<int, Plan>();
        readonly Dictionary<string, Tally> stats = new Dictionary<string, Tally>();

        protected override void OnStart()
        {
            var keys = new List<string>();
            if (UseFailedBo) keys.Add("failedBo");
            if (UseSuccessBo) keys.Add("successBo");
            if (UseMtr) keys.Add("mtr");
            if (UseFinalFlag) keys.Add("finalFlag");
            if (UseWedge) keys.Add("wedgeBottom");
            if (UseClimax) keys.Add("climax");
            if (UseSpike) keys.Add("spikeChannel");
            if (UseTwoLeg) keys.Add("twoLegPullback");
            if (UseTight) keys.Add("tightChannel");
            if (UseBroad) keys.Add("broadChannel");
            if (UseRange) keys.Add("rangeLow");
            core = new ReaderCore(Symbol.TickSize, keys);

            // warm up on the history already on the chart -- read it, but never
            // trade a setup that printed before the bot started
            for (int i = 0; i <= Bars.Count - 2; i++) AddBar(i);
            core.ReadUpTo(core.Count - 1);
            foreach (var s in core.Signals) s.Taken = true;

            Positions.Opened += OnOpened;
            Positions.Closed += OnClosed;
            Print("Trade Trainer Reader on {0} {1}: {2} setups on, risk {3}% per trade, {4}. Read {5} bars of history.",
                SymbolName, TimeFrame, keys.Count, RiskPercent, Days, core.Count);
        }

        void AddBar(int i)
        {
            core.AddBar(Bars.OpenPrices[i], Bars.HighPrices[i], Bars.LowPrices[i], Bars.ClosePrices[i]);
        }

        protected override void OnBar()
        {
            int v = Bars.Count - 2;                          // the bar that just closed
            if (v < 0) return;
            while (core.Count <= v) AddBar(core.Count);
            foreach (var g in core.ReadUpTo(v))
            {
                Print("Setup read: {0} ({1}) entry {2} stop {3} target {4}, {5:0.00}R at {6:0}% taught odds",
                    g.Name, g.Dir > 0 ? "buy" : "sell", Fmt(g.Entry), Fmt(g.Stop), Fmt(g.Target), g.RR, g.PWin * 100);
                if (DrawSetups) Draw(g);
            }

            // an order the setup has outlived is cancelled
            if (working != null && v - working.Sig.Index > ExpiryBars)
            {
                foreach (var po in PendingOrders.Where(o => o.Label == OrderLabel && o.SymbolName == SymbolName).ToList())
                    CancelPendingOrder(po);
                working = null;
            }

            if (Positions.FindAll(OrderLabel, SymbolName).Length > 0) return;
            if (PendingOrders.Any(o => o.Label == OrderLabel && o.SymbolName == SymbolName)) return;
            if (!DayOk()) return;

            // the most recent setup still live (it stays live for 6 bars)
            var live = core.Signals.Where(s => v - s.Index <= 6).OrderByDescending(s => s.Index).FirstOrDefault();
            if (live == null || live.Taken) return;
            live.Taken = true;
            Enter(live, v);
        }

        void Enter(ReaderSignal g, int v)
        {
            double atr = core.Atr(v), dir = g.Dir;
            if (Symbol.Spread > MaxSpreadPctAtr / 100.0 * atr) { Print("Skipped {0}: spread too wide ({1})", g.Name, Fmt(Symbol.Spread)); return; }
            var type = g.Dir > 0 ? TradeType.Buy : TradeType.Sell;
            double px = g.Dir > 0 ? Symbol.Ask : Symbol.Bid;
            bool market = false;
            if (!g.Limit)
            {
                if ((px - g.Entry) * dir >= 0) { Print("Skipped {0}: price already ran through the entry", g.Name); return; }
            }
            else
            {
                if ((px - g.Entry) * dir < -0.1 * atr) { Print("Skipped {0}: price already fell through the entry", g.Name); return; }
                if ((px - g.Entry) * dir <= 0) market = true;     // at or better than the planned limit
            }
            double reference = market ? px : g.Entry;
            double dist = Math.Abs(reference - g.Stop);
            if ((reference - g.Stop) * dir <= 0 || dist < 0.2 * atr) { Print("Skipped {0}: stop too close to the entry", g.Name); return; }
            if ((g.Target - reference) * dir <= 0) { Print("Skipped {0}: target already behind the entry", g.Name); return; }

            double risk = Account.Equity * RiskPercent / 100.0;
            double slPips = dist / Symbol.PipSize, tpPips = Math.Abs(g.Target - reference) / Symbol.PipSize;
            double volume = Symbol.NormalizeVolumeInUnits(risk / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (volume < Symbol.VolumeInUnitsMin) { Print("Skipped {0}: 1% risk is below the minimum size", g.Name); return; }
            volume = Math.Min(volume, Symbol.VolumeInUnitsMax);

            var plan = new Plan { Sig = g, Risk = risk };
            working = plan;
            TradeResult res = market
                ? ExecuteMarketOrder(type, SymbolName, volume, OrderLabel, slPips, tpPips)
                : g.Limit ? PlaceLimitOrder(type, SymbolName, volume, g.Entry, OrderLabel, g.Stop, g.Target, ProtectionType.Absolute)
                          : PlaceStopOrder(type, SymbolName, volume, g.Entry, OrderLabel, g.Stop, g.Target, ProtectionType.Absolute);
            if (!res.IsSuccessful) { working = null; Print("Order for {0} rejected: {1}", g.Name, res.Error); return; }
            Print("{0} {1} {2} at {3}, stop {4} ({5}), target {6}, risking {7:0.00}",
                market ? "Market" : g.Limit ? "Limit" : "Stop", type, volume, Fmt(reference), Fmt(g.Stop), g.StopName, Fmt(g.Target), risk);
        }

        void OnOpened(PositionOpenedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != OrderLabel || p.SymbolName != SymbolName || working == null) return;
            var plan = working; working = null;
            double dir = plan.Sig.Dir, atr = core.Atr(core.Count - 1);
            // a fill past its own stop or target, or right on the stop, is not
            // the trade that was planned -- close it rather than pretend it is
            if ((p.EntryPrice - plan.Sig.Stop) * dir <= 0 || (plan.Sig.Target - p.EntryPrice) * dir <= 0 ||
                Math.Abs(p.EntryPrice - plan.Sig.Stop) < 0.2 * atr)
            {
                Print("Invalid fill for {0} at {1}: closing it", plan.Sig.Name, Fmt(p.EntryPrice));
                open[p.Id] = plan;
                ClosePosition(p);
                return;
            }
            // stop and target exactly on the structure, not pips from the fill
            ModifyPosition(p, plan.Sig.Stop, plan.Sig.Target, ProtectionType.Absolute);
            open[p.Id] = plan;
        }

        void OnClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            Plan plan;
            if (!open.TryGetValue(p.Id, out plan)) return;
            open.Remove(p.Id);
            double r = plan.Risk > 0 ? p.NetProfit / plan.Risk : 0;
            Tally t;
            if (!stats.TryGetValue(plan.Sig.Key, out t)) stats[plan.Sig.Key] = t = new Tally();
            t.N++; if (r > 0) t.Wins++; t.R += r;
            Print("Closed {0}: {1:+0.00;-0.00}R ({2}, net {3:0.00})", plan.Sig.Name, r, args.Reason, p.NetProfit);
        }

        protected override void OnStop()
        {
            int n = stats.Values.Sum(x => x.N);
            if (n == 0) { Print("No trades closed."); return; }
            Print("---- Trade Trainer Reader: {0} trades, net {1:+0.00;-0.00}R, avg {2:+0.000;-0.000}R, win rate {3:0}% ----",
                n, stats.Values.Sum(x => x.R), stats.Values.Sum(x => x.R) / n, 100.0 * stats.Values.Sum(x => x.Wins) / n);
            foreach (var kv in stats.OrderByDescending(x => x.Value.N))
                Print("  {0,-36} {1,4} trades  win {2,3:0}%  avg {3:+0.00;-0.00}R", kv.Key, kv.Value.N, 100.0 * kv.Value.Wins / kv.Value.N, kv.Value.R / kv.Value.N);
        }

        bool DayOk()
        {
            var d = Server.Time.DayOfWeek;
            bool weekend = d == DayOfWeek.Saturday || d == DayOfWeek.Sunday;
            return Days == TradeDays.Every_day || (Days == TradeDays.Weekends_only ? weekend : !weekend);
        }

        void Draw(ReaderSignal g)
        {
            try
            {
                var t = Bars.OpenTimes[g.Index];
                double y = g.Dir > 0 ? Bars.LowPrices[g.Index] : Bars.HighPrices[g.Index];
                Chart.DrawIcon("tt_i" + g.Index, g.Dir > 0 ? ChartIconType.UpArrow : ChartIconType.DownArrow, t, y, Color.Orange);
                Chart.DrawText("tt_t" + g.Index, g.Name, t, y, Color.Orange);
            }
            catch (Exception) { /* no chart in some run modes */ }
        }

        string Fmt(double x) { return x.ToString("F" + Symbol.Digits); }
    }

    // =====================================================================
    // READER CORE -- a line-for-line port of Trade Trainer's reading Coach.
    // It finds the 11 taught setups (bull and bear) from closed bars only.
    // At bar v it reads bars 0..v and nothing after. No cTrader types here,
    // so the same code can be checked against the app outside cTrader.
    // =====================================================================
    public sealed class ReaderSignal
    {
        public string Key;          // e.g. "wedgeBottom" or "wedgeBottom_D" (bear twin)
        public string Name;         // readable name
        public int Dir;             // +1 buy, -1 sell
        public int Index;           // signal bar
        public double Entry, Stop, Target, PWin, RR;
        public bool Limit;          // limit entry at the signal close (range edges); otherwise a stop order
        public string StopName;
        public int Anchor;
        public bool Taken;
    }

    public sealed class ReaderCore
    {
        sealed class Pt { public int I; public double P; public Pt(int i, double p) { I = i; P = p; } }
        sealed class Piv { public int I; public double P; public int D; }
        sealed class Hit
        {
            public double Target, StopAt; public string StopName; public bool Limit; public int Anchor;
        }
        sealed class Frame
        {
            public int M;
            public readonly List<double> H = new List<double>(), L = new List<double>(), O = new List<double>(),
                C = new List<double>(), E = new List<double>(), TR = new List<double>(), A = new List<double>();
            public readonly List<Piv> Piv = new List<Piv>();
            public int PivTo = 2;
        }

        static readonly string[] Keys = { "failedBo", "successBo", "mtr", "finalFlag", "wedgeBottom", "climax",
            "spikeChannel", "twoLegPullback", "tightChannel", "broadChannel", "rangeLow" };
        // taught win rates -- the same numbers the app's Coach shows
        static readonly Dictionary<string, double> PWin = new Dictionary<string, double> {
            { "spikeChannel", 0.54 }, { "tightChannel", 0.62 }, { "broadChannel", 0.58 }, { "twoLegPullback", 0.54 },
            { "rangeLow", 0.61 }, { "failedBo", 0.65 }, { "successBo", 0.52 }, { "wedgeBottom", 0.40 },
            { "mtr", 0.42 }, { "finalFlag", 0.44 }, { "climax", 0.62 } };
        static readonly Dictionary<string, string[]> Names = new Dictionary<string, string[]> {
            { "spikeChannel", new[] { "Spike and channel", "Spike and channel (bear)" } },
            { "tightChannel", new[] { "Tight bull channel", "Tight bear channel" } },
            { "broadChannel", new[] { "Broad bull channel", "Broad bear channel" } },
            { "twoLegPullback", new[] { "Two-legged pullback", "Two-legged pullback (bear)" } },
            { "rangeLow", new[] { "Trading range - buy at the low", "Trading range - sell at the high" } },
            { "failedBo", new[] { "Failed breakout below the range", "Failed breakout above the range" } },
            { "successBo", new[] { "Breakout with follow-through", "Breakout with follow-through (down)" } },
            { "wedgeBottom", new[] { "Wedge bottom (three pushes down)", "Wedge top (three pushes up)" } },
            { "mtr", new[] { "Major trend reversal (bull)", "Major trend reversal (bear)" } },
            { "finalFlag", new[] { "Final flag reversal (bull)", "Final flag reversal (bear)" } },
            { "climax", new[] { "Sell climax", "Buy climax" } } };

        readonly Frame up = new Frame { M = 1 }, dn = new Frame { M = -1 };
        readonly double tick;
        readonly HashSet<string> enabled;
        public readonly List<ReaderSignal> Signals = new List<ReaderSignal>();
        int readTo = 59;

        public ReaderCore(double tickSize, IEnumerable<string> enabledKeys)
        {
            tick = tickSize;
            enabled = new HashSet<string>(enabledKeys ?? Keys);
        }
        public static IEnumerable<string> AllKeys { get { return Keys; } }
        public int Count { get { return up.H.Count; } }
        public double Atr(int v) { return up.A[v]; }

        /// Append one CLOSED bar (in order, oldest first).
        public void AddBar(double o, double h, double l, double c)
        {
            Push(up, o, h, l, c); Push(dn, o, h, l, c);
        }
        static void Push(Frame f, double o, double h, double l, double c)
        {
            int m = f.M, i = f.H.Count;
            f.H.Add(m > 0 ? h : -l); f.L.Add(m > 0 ? l : -h); f.O.Add(m * o); f.C.Add(m * c);
            f.E.Add(i > 0 ? f.C[i] * (2.0 / 21) + f.E[i - 1] * (19.0 / 21) : f.C[i]);
            f.TR.Add(i > 0 ? Math.Max(f.H[i] - f.L[i], Math.Max(Math.Abs(f.H[i] - f.C[i - 1]), Math.Abs(f.L[i] - f.C[i - 1]))) : f.H[i] - f.L[i]);
            double s = 0; int n = 0;
            for (int j = Math.Max(0, i - 13); j <= i; j++) { s += f.TR[j]; n++; }
            f.A.Add(n > 0 && s / n > 0 ? s / n : 1e-9);
        }

        /// Read every bar added so far; returns the setups that just completed.
        public List<ReaderSignal> ReadUpTo(int upto)
        {
            var fresh = new List<ReaderSignal>();
            upto = Math.Min(upto, Count - 1);
            for (int v = readTo + 1; v <= upto; v++)
            {
                var g = DetectAt(v);
                if (g != null && !Signals.Any(x => x.Key == g.Key && (v - x.Index <= 5 || x.Anchor == g.Anchor)))
                { Signals.Add(g); fresh.Add(g); }
                readTo = v;
            }
            return fresh;
        }

        public ReaderSignal DetectAt(int v)
        {
            if (v < 60 || v >= Count) return null;
            foreach (var key in Keys)
            {
                if (!enabled.Contains(key)) continue;
                for (int mi = 0; mi < 2; mi++)
                {
                    var f = mi == 0 ? up : dn; int m = f.M;
                    var r = Run(key, f, v);
                    if (r == null) continue;
                    double A = f.A[v], pWin = PWin[key];
                    double entry = r.Limit ? f.C[v] : f.H[v] + 0.08 * A;
                    double stop = Math.Min(r.StopAt, f.L[v]) - 4 * tick, risk = entry - stop;
                    double rr = risk > 0 ? (r.Target - entry) / risk : 0;
                    if (!(rr > 0) || pWin * rr - (1 - pWin) <= 0) continue;
                    return new ReaderSignal {
                        Key = m > 0 ? key : key + "_D", Name = Names[key][m > 0 ? 0 : 1], Dir = m, Index = v,
                        Entry = m * entry, Stop = m * stop, Target = m * r.Target, PWin = pWin, RR = rr, Limit = r.Limit,
                        StopName = r.StopAt <= f.L[v] ? r.StopName : "signal bar", Anchor = r.Anchor };
                }
            }
            return null;
        }

        // ---------------- primitives (bull frame) ----------------
        static void Pivots(Frame f, int v)
        {
            for (int i = f.PivTo + 1; i <= v - 3; i++)
            {
                bool isH = true, isL = true;
                for (int j = i - 3; j <= i + 3; j++)
                {
                    if (j == i) continue;
                    if (f.H[j] >= f.H[i]) isH = false;
                    if (f.L[j] <= f.L[i]) isL = false;
                }
                if (isH) f.Piv.Add(new Piv { I = i, P = f.H[i], D = 1 });
                if (isL) f.Piv.Add(new Piv { I = i, P = f.L[i], D = -1 });
                f.PivTo = i;
            }
        }
        static List<Piv> Pv(Frame f, int v, int a, int d)
        {
            Pivots(f, v);
            var P = f.Piv; int lo = 0, hi = P.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (P[mid].I < a) lo = mid + 1; else hi = mid; }
            var o = new List<Piv>();
            for (int k = lo; k < P.Count && P[k].I <= v - 3; k++) if (P[k].D == d) o.Add(P[k]);
            return o;
        }
        static Pt HiF(Frame f, int a, int z)
        {
            a = Math.Max(0, a); int bi = a; double bv = double.NegativeInfinity;
            for (int i = a; i <= z; i++) if (f.H[i] > bv) { bv = f.H[i]; bi = i; }
            return new Pt(bi, bv);
        }
        static Pt LoF(Frame f, int a, int z)
        {
            a = Math.Max(0, a); int bi = a; double bv = double.PositiveInfinity;
            for (int i = a; i <= z; i++) if (f.L[i] < bv) { bv = f.L[i]; bi = i; }
            return new Pt(bi, bv);
        }
        static Pt P(Piv p) { return new Pt(p.I, p.P); }
        static bool SigBar(Frame f, int v, bool strong)
        {
            double r = f.H[v] - f.L[v];
            if (!(r > 0)) return false;
            double pos = (f.C[v] - f.L[v]) / r, body = f.C[v] - f.O[v];
            return strong ? (body >= 0.35 * r && pos >= 0.75 && r >= 0.6 * f.A[v]) : (body >= 0 && pos >= 0.5);
        }
        static bool FirstSig(Frame f, int from, int v, bool strong)
        {
            for (int j = from + 1; j < v; j++) if (SigBar(f, j, strong)) return false;
            return true;
        }
        static bool Turned(Frame f, Pt ext, int v)
        {
            return v - ext.I >= 2 && LoF(f, ext.I + 1, v).P > ext.P && f.C[v] > f.H[ext.I];
        }
        static bool FirstTurn(Frame f, Pt ext, int v, bool strong)
        {
            for (int j = ext.I + 1; j < v; j++) if (SigBar(f, j, strong) && Turned(f, ext, j)) return false;
            return true;
        }
        static int HTries(Frame f, int from, int v)
        {
            int tries = 0; bool armed = true;
            for (int j = from + 1; j <= v; j++)
            {
                if (f.H[j] > f.H[j - 1]) { if (armed) { tries++; armed = false; } }
                else armed = true;
            }
            return tries;
        }
        sealed class Rng { public Pt Top, Bot; public double H; }
        static Rng RangeAt(Frame f, int a, int z, int v)
        {
            a = Math.Max(1, a);
            if (z - a < 40) return null;
            Pt top = HiF(f, a, z), bot = LoF(f, a, z); double h = top.P - bot.P, A = f.A[v];
            if (h < 3 * A || h > 10 * A) return null;
            if (Math.Abs(f.E[z] - f.E[a]) > 0.25 * h) return null;
            int cross = 0;
            for (int i = a + 1; i <= z; i++) if ((f.C[i] - f.E[i]) * (f.C[i - 1] - f.E[i - 1]) < 0) cross++;
            if (cross < 6) return null;
            var tt = Pv(f, v, a, 1).Where(p => p.I <= z && p.P >= top.P - 0.2 * h).ToList();
            var tb = Pv(f, v, a, -1).Where(p => p.I <= z && p.P <= bot.P + 0.2 * h).ToList();
            if (tt.Count < 2 || tb.Count < 2) return null;
            if (tt[tt.Count - 1].I - tt[0].I < 10 || tb[tb.Count - 1].I - tb[0].I < 10) return null;
            return new Rng { Top = top, Bot = bot, H = h };
        }

        static Hit Run(string key, Frame f, int v)
        {
            switch (key)
            {
                case "failedBo": return FailedBo(f, v);
                case "successBo": return SuccessBo(f, v);
                case "mtr": return Mtr(f, v);
                case "finalFlag": return FinalFlag(f, v);
                case "wedgeBottom": return Wedge(f, v);
                case "climax": return Climax(f, v);
                case "spikeChannel": return Spike(f, v);
                case "twoLegPullback": return TwoLeg(f, v);
                case "tightChannel": return Tight(f, v);
                case "broadChannel": return Broad(f, v);
                case "rangeLow": return RangeLow(f, v);
            }
            return null;
        }

        // ---------------- the detectors (bull frame) ----------------
        static Hit FailedBo(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            Pt fail = LoF(f, v - 7, v); double A = f.A[v];
            var r = RangeAt(f, v - 90, fail.I - 2, v); if (r == null) return null;
            if (!(fail.P < r.Bot.P - 0.15 * A) || fail.P < r.Bot.P - 0.35 * r.H) return null;
            if (!(f.C[v] > r.Bot.P)) return null;
            if (!Turned(f, fail, v)) return null;
            if (!FirstTurn(f, fail, v, true)) return null;
            return new Hit { Target = r.Top.P, Anchor = fail.I, StopAt = fail.P, StopName = "failed breakout extreme" };
        }
        static Hit SuccessBo(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            int bo = -1; Rng r = null;
            for (int i = v - 3; i >= v - 14 && bo < 0; i--)
            {
                var rr = RangeAt(f, i - 90, i - 1, v);
                if (rr != null && f.C[i] > rr.Top.P && f.C[i - 1] <= rr.Top.P) { bo = i; r = rr; }
            }
            if (bo < 0) return null;
            Pt peak = HiF(f, bo, v - 1);
            if (peak.P < r.Top.P + 0.15 * r.H || peak.I >= v - 1) return null;
            Pt pb = LoF(f, peak.I, v);
            if (pb.P < r.Top.P - 0.1 * r.H) return null;
            if (!FirstSig(f, peak.I, v, true)) return null;
            return new Hit { Target = r.Top.P + r.H, Anchor = bo, StopAt = pb.P, StopName = "breakout pullback" };
        }
        static Hit Mtr(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt test = LoF(f, v - 6, v);
            Pt legHi = HiF(f, test.I - 25, test.I - 2);
            Pt low = LoF(f, legHi.I - 35, legHi.I);
            if (low.I >= legHi.I - 2) return null;
            Pt st = HiF(f, low.I - 70, low.I); double trend = st.P - low.P, leg = legHi.P - low.P;
            if (trend < 5 * A || leg < 2 * A || leg > 0.8 * trend) return null;
            var phs = Pv(f, v, st.I, 1).Where(p => p.I < low.I).ToList();
            if (phs.Count < 2) return null;
            Piv a = phs[phs.Count - 2], b = phs[phs.Count - 1];
            if (!(b.P < a.P) || b.I - a.I < 4) return null;
            double slope = (b.P - a.P) / (b.I - a.I); bool brk = false;
            for (int i = low.I + 1; i <= legHi.I; i++) { if (f.H[i] > b.P + slope * (i - b.I) + 0.2 * A) { brk = true; break; } }
            if (!brk) return null;
            if (legHi.P - test.P < 0.4 * leg) return null;
            if (test.P < low.P - 0.8 * A || test.P > low.P + 0.6 * leg) return null;
            if (HiF(f, legHi.I + 1, v).P > legHi.P) return null;
            if (!Turned(f, test, v)) return null;
            if (!FirstTurn(f, test, v, true)) return null;
            return new Hit { Target = test.P + leg, Anchor = low.I, StopAt = test.P, StopName = test.P > low.P ? "higher low" : "lower low test" };
        }
        static Hit FinalFlag(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt fail = LoF(f, v - 6, v);
            Pt legLo = LoF(f, fail.I - 18, fail.I - 3);
            Pt flagTop = HiF(f, legLo.I, fail.I); double flagH = flagTop.P - legLo.P;
            if (flagH < 0.8 * A) return null;
            if (!(fail.P < legLo.P - 0.1 * A) || fail.P < legLo.P - 2 * A) return null;
            if (!(f.C[v] > legLo.P) || !Turned(f, fail, v)) return null;
            Pt st = HiF(f, legLo.I - 45, legLo.I); double trend = st.P - legLo.P;
            if (trend < 2.5 * flagH || trend < 4 * A) return null;
            if (flagTop.P > legLo.P + 0.5 * trend) return null;
            var lhs = Pv(f, v, st.I, 1).Where(p => p.I < legLo.I - 2).ToList();
            if (lhs.Count < 2 || !(lhs[lhs.Count - 1].P < lhs[lhs.Count - 2].P)) return null;
            if (fail.I - legLo.I < 4 || fail.I - legLo.I > 16) return null;
            if (!FirstTurn(f, fail, v, true)) return null;
            return new Hit { Target = flagTop.P + (flagTop.P - fail.P), Anchor = fail.I, StopAt = fail.P, StopName = "failed breakout extreme" };
        }
        static Hit Wedge(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt p3 = LoF(f, v - 6, v);
            var pls = Pv(f, v, p3.I - 45, -1).Where(p => p.I < p3.I - 3).ToList();
            if (pls.Count < 2) return null;
            Pt p2 = P(pls[pls.Count - 1]), p1 = P(pls[pls.Count - 2]);
            if (!(p2.P < p1.P - 0.3 * A && p3.P < p2.P - 0.2 * A)) return null;
            if (p2.I - p1.I < 5 || p3.I - p2.I < 5) return null;
            Pt b1 = HiF(f, p1.I, p2.I), b2 = HiF(f, p2.I, p3.I);
            if (!(b2.P < b1.P) || b1.P - p1.P < 0.8 * A || b2.P - p2.P < 0.8 * A) return null;
            Pt org = HiF(f, p1.I - 25, p1.I);
            if (org.P - p3.P < 4 * A || org.P <= b1.P) return null;
            if (b2.P - p3.P > 1.3 * (b1.P - p2.P)) return null;
            if (!(p2.P - p3.P < p1.P - p2.P) || org.P - p1.P < 1.2 * (b1.P - p1.P)) return null;
            if (!((p1.P - p3.P) / Math.Max(1, p3.I - p1.I) < (b1.P - b2.P) / Math.Max(1, b2.I - b1.I))) return null;
            if (!Turned(f, p3, v)) return null;
            if (!FirstTurn(f, p3, v, true)) return null;
            return new Hit { Target = org.P, Anchor = p3.I, StopAt = p3.P, StopName = "third push" };
        }
        static Hit Climax(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt low = LoF(f, v - 6, v), top = HiF(f, low.I - 22, low.I - 6);
            double h = top.P - low.P;
            if (h < 4 * A) return null;
            int mid = (int)Math.Floor((top.I + low.I) / 2.0 + 0.5);   // JS Math.round
            double d1 = f.C[top.I] - f.C[mid], d2 = f.C[mid] - f.C[low.I], r1 = 0, r2 = 0;
            for (int i = top.I; i < mid; i++) r1 += f.H[i] - f.L[i];
            for (int j = mid; j <= low.I; j++) r2 += f.H[j] - f.L[j];
            r1 /= Math.Max(1, mid - top.I); r2 /= Math.Max(1, low.I - mid + 1);
            if (!(d2 > 1.15 * d1) || !(r2 > 1.1 * r1)) return null;
            if (!Turned(f, low, v)) return null;
            if (!FirstTurn(f, low, v, true)) return null;
            return new Hit { Target = low.P + 0.62 * h, Anchor = low.I, StopAt = low.P, StopName = "climax extreme" };
        }
        static Hit Spike(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; int ss = -1, se = -1;
            for (int s = v - 10; s >= v - 45 && ss < 0; s--)
            {
                for (int len = 3; len <= 6 && ss < 0; len++)
                {
                    int e = s + len - 1; if (e > v - 8) break;
                    int bulls = 0; bool upOk = true;
                    for (int j = s; j <= e; j++) { if (f.C[j] > f.O[j]) bulls++; if (j > s && f.C[j] < f.C[j - 1]) upOk = false; }
                    if (upOk && bulls >= len - 1 && f.C[e] - f.O[s] >= 2.5 * A && f.C[e] - f.O[s] >= 1.2 * len * A * 0.5) { ss = s; se = e; }
                }
            }
            if (ss < 0) return null;
            Pt lo = LoF(f, ss - 3, ss), hi = HiF(f, ss, se + 1);
            double baseH = HiF(f, ss - 15, ss - 1).P - LoF(f, ss - 15, ss - 1).P;
            if (baseH > 0.8 * (hi.P - lo.P)) return null;
            Pt chanHi = HiF(f, se + 2, v - 1);
            if (!(chanHi.P > hi.P)) return null;
            Pt pb1 = LoF(f, se + 1, chanHi.I);
            if (pb1.P <= lo.P || v - chanHi.I < 2 || v - chanHi.I > 10) return null;
            Pt pb = LoF(f, chanHi.I, v);
            if (pb.P <= pb1.P || !Turned(f, pb, v)) return null;
            if (!FirstTurn(f, pb, v, true)) return null;
            return new Hit { Target = hi.P + (hi.P - lo.P), Anchor = chanHi.I, StopAt = pb.P, StopName = "last pullback" };
        }
        static Hit TwoLeg(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt th = HiF(f, v - 35, v - 8);
            var pls = Pv(f, v, th.I - 60, -1).Where(p => p.I < th.I).ToList();
            if (pls.Count < 2) return null;
            Pt hl = P(pls[pls.Count - 1]), hl0 = P(pls[pls.Count - 2]);
            if (!(hl.P > hl0.P) || th.P - hl.P < 2 * A) return null;
            var l1s = Pv(f, v, th.I + 1, -1);
            if (l1s.Count == 0 || l1s[0].I > v - 4) return null;
            Pt l1 = P(l1s[0]);
            Pt bp = HiF(f, l1.I + 1, v - 2);
            if (bp.P >= th.P || bp.P - l1.P < 0.3 * (th.P - l1.P)) return null;
            Pt l2 = LoF(f, bp.I + 1, v);
            if (l2.P > l1.P + 0.4 * (bp.P - l1.P) || l2.P <= hl.P) return null;
            if (th.P - l2.P > 0.95 * (th.P - hl.P) || !Turned(f, l2, v)) return null;
            if (!FirstTurn(f, l2, v, true)) return null;
            return new Hit { Target = l2.P + (th.P - hl.P), Anchor = th.I, StopAt = l2.P, StopName = "second leg" };
        }
        static Hit Tight(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; Pt cs = LoF(f, v - 45, v - 20), top = HiF(f, cs.I, v - 1); double h = top.P - cs.P;
            if (v - top.I < 1 || v - top.I > 8 || h < 5 * A) return null;
            int above = 0, n = 0; double dd = 0, run = double.NegativeInfinity;
            for (int j = cs.I + 3; j <= v; j++)
            {
                n++; if (f.C[j] > f.E[j]) above++;
                run = Math.Max(run, f.H[j]); dd = Math.Max(dd, run - f.L[j]);
            }
            if (above < 0.85 * n || dd > 0.35 * h || dd > 4 * A) return null;
            if (h / Math.Max(1, top.I - cs.I) < 0.2 * A) return null;
            Pt pl = LoF(f, top.I, v);
            if (!Turned(f, pl, v) || !FirstTurn(f, pl, v, true)) return null;
            var fhs = Pv(f, v, cs.I + 1, 1);
            Pt legH = fhs.Count > 0 && fhs[0].I < top.I ? P(fhs[0]) : HiF(f, cs.I, cs.I + 10);
            return new Hit { Target = pl.P + (legH.P - cs.P), Anchor = top.I, StopAt = pl.P, StopName = "pullback" };
        }
        static Hit Broad(Frame f, int v)
        {
            if (!SigBar(f, v, true)) return null;
            double A = f.A[v]; var phs = Pv(f, v, v - 90, 1);
            if (phs.Count < 2) return null;
            Piv c2 = phs[phs.Count - 1], c1 = null;
            for (int k = phs.Count - 2; k >= 0; k--) if (c2.I - phs[k].I >= 8) { c1 = phs[k]; break; }
            if (c1 == null) return null;
            Pt l2 = LoF(f, c1.I, c2.I), l1 = LoF(f, c1.I - 25, c1.I);
            if (!(c2.P > c1.P + 0.3 * A && l2.P > l1.P + 0.3 * A)) return null;
            double leg = c2.P - l2.P;
            if (leg < 2.5 * A || v - c2.I < 4) return null;
            Pt pb = LoF(f, c2.I, v);
            if (pb.P <= l2.P || c2.P - pb.P < 0.4 * leg || HiF(f, c2.I + 1, v).P > c2.P) return null;
            if (HTries(f, c2.I, v - 1) < 1 || !Turned(f, pb, v)) return null;
            if (!FirstTurn(f, pb, v, true)) return null;
            return new Hit { Target = c2.P, Anchor = c2.I, StopAt = pb.P, StopName = "channel pullback" };
        }
        static Hit RangeLow(Frame f, int v)
        {
            if (!SigBar(f, v, false)) return null;
            var r = RangeAt(f, v - 90, v - 1, v); if (r == null) return null;
            if (!(f.L[v] <= r.Bot.P + 0.2 * r.H && f.C[v] > r.Bot.P) || f.C[v] > r.Bot.P + 0.4 * r.H) return null;
            int vs = v;
            while (vs > v - 40 && f.H[vs - 1] < r.Bot.P + 0.5 * r.H) vs--;
            Pt lowNow = LoF(f, vs, v);
            if (HTries(f, lowNow.I - 1, v - 1) < 1 && HTries(f, vs, v - 1) < 1) return null;
            if (!Turned(f, lowNow, v) || !FirstTurn(f, lowNow, v, false)) return null;
            return new Hit { Limit = true, Target = r.Top.P, Anchor = lowNow.I, StopAt = r.Bot.P, StopName = "range edge" };
        }
    }
}
