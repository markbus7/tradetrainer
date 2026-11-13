// =====================================================================
//  Trade Watch -- cTrader cBot (read-only)
//
//  Watches every pending order and open position on the account, on all
//  symbols, and groups them into setups: one setup = one symbol + one
//  direction. Buys and sells, and different symbols, are never mixed.
//
//  For each setup it shows lot size, entry, SL and TP of every order, the
//  risk if everything fills and is stopped (in money and % of balance), the
//  reward at TP, what is filled now and its P/L in R, spread, swap and
//  commission -- and checks the math against your plan:
//    missing or wrong-side SL/TP, risk above your target, stops that differ,
//    the range scale-in rules (entry 2 fills -> all TPs to entry 1), a stop
//    that is closer than the plan, spread too wide for the stop, orders left
//    pending after a trade closed, rollover/swap, and your daily loss stop.
//
//  It also keeps a journal and a report in Documents/TradeWatch:
//    journal.csv    one line per finished trade (a scale-in is one trade)
//    cancelled.csv  orders you cancelled or that expired, and what they
//                   would have done
//    report.md      your performance, what you missed out on, and the three
//                   things that cost you most. Send this file to Claude.
//
//  It never places, moves or closes anything.
//
//  HOW TO USE (cTrader for Mac or Windows)
//    Algo > cBots > New > delete the template > paste this whole file >
//    Build. Add an instance on any one chart and press Start. Allow file
//    access when cTrader asks (needed only for the journal).
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using cAlgo.API;
using cAlgo.API.Internals;
using TradeWatchCore;

namespace TradeWatchCore
{
    public enum Lvl { Ok = 0, Info = 1, Warn = 2, Bad = 3 }

    public sealed class Note
    {
        public Lvl Level;
        public string Code;   // rule name, used in the journal ("risk_high")
        public string Key;    // unique per setup + rule, for alerting once
        public string Text;
        public Note(Lvl level, string code, string key, string text) { Level = level; Code = code; Key = key; Text = text; }
    }

    // one pending order or open position, already converted to plain numbers
    public sealed class WItem
    {
        public string Symbol;
        public bool Buy;
        public bool Filled;
        public bool IsLimit = true;     // pending limit order (positions: true)
        public string OrderType = "limit";
        public long Id;
        public double Units, Lots, Entry;
        public double? Sl, Tp;
        public double PipSize = 0.0001, PipValue;   // PipValue: account money per pip per 1 unit
        public double StepLots = 0.01;
        public int Digits = 5;
        public double CommRT;            // round-trip commission in account money (positive = cost)
        public bool CommKnown;
        public double NetProfit;         // filled only
        public double SwapPerNight;      // filled only, account money, negative = cost
        public int SwapMultTonight = 1;  // 3 on the symbol's triple-swap day (the weekend's swap)
        public string TripleDay = "";    // the symbol's triple-swap weekday, e.g. "Wed"
        public double SpreadPips;
        public double Current = double.NaN;   // price the position would close at now

        public double Sign => Buy ? 1 : -1;
        public double? SlPips => Sl.HasValue ? (Entry - Sl.Value) * Sign / PipSize : (double?)null;   // > 0 = risk
        public double? TpPips => Tp.HasValue ? (Tp.Value - Entry) * Sign / PipSize : (double?)null;   // > 0 = reward
        public double Money(double pips) => pips * PipValue * Units;
        public double? LossAtSl => SlPips.HasValue ? Money(SlPips.Value) + CommRT : (double?)null;  // < 0 = stop locks profit
        public double? GainAtTp => TpPips.HasValue ? Money(TpPips.Value) - CommRT : (double?)null;
    }

    public sealed class Setup
    {
        public string Symbol;
        public bool Buy;
        public string Key => Symbol + "|" + (Buy ? "BUY" : "SELL");
        public List<WItem> Items = new List<WItem>();
        public List<List<WItem>> Levels = new List<List<WItem>>();
        public bool ScaleIn;
        public string Stage = "";
        public double? FullRisk, FullReward, FilledRisk, FilledReward;
        public double Floating, RiskPct, SpreadPips, SwapNight, SwapTonight, CommRT;
        public bool TripleTonight; public string TripleDay = "";
        public bool CommAllKnown = true;
        public double? FloatR;
        public List<Note> Notes = new List<Note>();
        public int Digits => Items.Count > 0 ? Items[0].Digits : 5;
        public double PipSize => Items.Count > 0 ? Items[0].PipSize : 0.0001;
        public Lvl Worst => Notes.Count == 0 ? Lvl.Ok : Notes.Max(n => n.Level);
        public string LevelName(WItem it)
        {
            for (int i = 0; i < Levels.Count; i++) if (Levels[i].Contains(it)) return (ScaleIn ? "E" : "#") + (i + 1);
            return "#";
        }
    }

    public static class Fmt
    {
        public static readonly CultureInfo C = CultureInfo.InvariantCulture;
        public static string Ccy = "€";
        public static string M(double v) => (v < 0 ? "-" : "") + Ccy + Math.Abs(v).ToString("0.00", C);
        public static string SM(double v) => (v < 0 ? "-" : "+") + Ccy + Math.Abs(v).ToString("0.00", C);
        public static string P(double v, int digits) => v.ToString("F" + digits, C);
        public static string N(double v, string f = "0.0") => v.ToString(f, C);
        public static string Lots(double v) => v.ToString("0.00##", C);
        public static string R(double v) => (v < 0 ? "" : "+") + v.ToString("0.00", C) + "R";
    }

    public static class Analyzer
    {
        public const double LevelTolPips = 1.5;

        /// Builds one setup per symbol + direction and runs every check on it.
        public static List<Setup> Build(IEnumerable<WItem> items, double balance, double riskTargetPct,
                                        ICollection<string> justClosedKeys = null)
        {
            var setups = items
                .GroupBy(i => i.Symbol + "|" + (i.Buy ? "BUY" : "SELL"))
                .Select(g => new Setup { Symbol = g.First().Symbol, Buy = g.First().Buy, Items = g.ToList() })
                .OrderBy(s => s.Symbol, StringComparer.Ordinal).ThenBy(s => s.Buy ? 0 : 1)
                .ToList();
            foreach (var s in setups) Analyze(s, balance, riskTargetPct, justClosedKeys);

            // both directions open on one symbol: note it on both, never merge them
            foreach (var g in setups.GroupBy(s => s.Symbol).Where(g => g.Count() > 1))
                foreach (var s in g)
                    s.Notes.Add(new Note(Lvl.Warn, "both_dirs", s.Key + "|both", "Buys and sells open on " + s.Symbol + " at the same time (shown separately)."));
            foreach (var s in setups) s.Notes = s.Notes.OrderByDescending(n => n.Level).ToList();
            return setups;
        }

        static void Analyze(Setup s, double balance, double target, ICollection<string> justClosed)
        {
            double pip = s.PipSize; int dg = s.Digits; string k = s.Key + "|";
            double sign = s.Buy ? 1 : -1;

            // levels: entry 1 is the one price reaches first (buy: highest, sell: lowest)
            var sorted = s.Items.OrderByDescending(i => i.Entry * sign).ToList();
            foreach (var it in sorted)
            {
                var last = s.Levels.LastOrDefault();
                if (last != null && Math.Abs(last[0].Entry - it.Entry) / pip <= LevelTolPips) last.Add(it);
                else s.Levels.Add(new List<WItem> { it });
            }
            s.ScaleIn = s.Levels.Count == 2 && s.Items.Where(i => !i.Filled).All(i => i.IsLimit);

            s.SpreadPips = s.Items.Max(i => i.SpreadPips);
            s.SwapNight = s.Items.Where(i => i.Filled).Sum(i => i.SwapPerNight);
            s.SwapTonight = s.Items.Where(i => i.Filled).Sum(i => i.SwapPerNight * i.SwapMultTonight);
            s.TripleTonight = s.Items.Any(i => i.Filled && i.SwapMultTonight == 3);
            s.TripleDay = s.Items.Select(i => i.TripleDay).FirstOrDefault(d => !string.IsNullOrEmpty(d)) ?? "";
            s.CommRT = s.Items.Sum(i => i.CommRT);
            s.CommAllKnown = s.Items.All(i => i.CommKnown);
            s.Floating = s.Items.Where(i => i.Filled).Sum(i => i.NetProfit);

            bool allSl = s.Items.All(i => i.Sl.HasValue), allTp = s.Items.All(i => i.Tp.HasValue);
            var filled = s.Items.Where(i => i.Filled).ToList();
            s.FullRisk = allSl ? s.Items.Sum(i => i.LossAtSl.Value) : (double?)null;
            s.FullReward = allTp ? s.Items.Sum(i => i.GainAtTp.Value) : (double?)null;
            if (filled.Count > 0)
            {
                s.FilledRisk = filled.All(i => i.Sl.HasValue) ? filled.Sum(i => i.LossAtSl.Value) : (double?)null;
                s.FilledReward = filled.All(i => i.Tp.HasValue) ? filled.Sum(i => i.GainAtTp.Value) : (double?)null;
            }
            if (s.FullRisk.HasValue && s.FullRisk.Value > 0 && filled.Count > 0) s.FloatR = s.Floating / s.FullRisk.Value;

            // stage
            if (filled.Count == 0) s.Stage = "waiting to fill";
            else if (s.ScaleIn)
                s.Stage = s.Levels[1].All(i => i.Filled) ? "both entries filled" : "entry 1 filled";
            else s.Stage = filled.Count == s.Items.Count ? "open" : "partly filled";

            // 1. missing / wrong-side SL and TP
            foreach (var it in s.Items)
            {
                string nm = s.LevelName(it) + " " + Fmt.Lots(it.Lots) + " lot";
                if (!it.Sl.HasValue)
                    s.Notes.Add(new Note(Lvl.Bad, "no_sl", k + "nosl|" + it.Id, "No stop on " + nm + ". Risk is unlimited: set the SL."));
                else if (it.SlPips.Value <= 0 && !it.Filled)
                    s.Notes.Add(new Note(Lvl.Bad, "sl_side", k + "slside|" + it.Id, "Stop of " + nm + " is on the wrong side of its entry."));
                else if (it.SlPips.Value < 0 && it.Filled)
                    s.Notes.Add(new Note(Lvl.Ok, "sl_profit", k + "slprofit|" + it.Id, "Stop of " + nm + " is in profit: locks " + Fmt.SM(-it.LossAtSl.Value) + "."));

                if (!it.Tp.HasValue)
                    s.Notes.Add(new Note(Lvl.Warn, "no_tp", k + "notp|" + it.Id, "No TP on " + nm + "."));
                else if (it.TpPips.Value < -LevelTolPips)
                    s.Notes.Add(new Note(Lvl.Bad, "tp_side", k + "tpside|" + it.Id, "TP of " + nm + " is on the loss side of its entry."));
            }

            // 2. one shared stop
            var sls = s.Items.Where(i => i.Sl.HasValue).Select(i => i.Sl.Value).ToList();
            if (sls.Count > 1 && (sls.Max() - sls.Min()) / pip > 1.0)
                s.Notes.Add(new Note(Lvl.Warn, "stops_differ", k + "stopsdiffer",
                    "Stops differ (" + string.Join(", ", sls.Distinct().Select(v => Fmt.P(v, dg))) + "). Your plan uses one shared stop."));

            // 3. risk against target
            if (s.FullRisk.HasValue && balance > 0)
            {
                s.RiskPct = s.FullRisk.Value / balance * 100;
                if (s.RiskPct > target * 1.10)
                {
                    double f = target / s.RiskPct;
                    var fits = s.Items.Where(i => !i.Filled)
                        .Select(i => Fmt.Lots(i.Lots) + " -> " + Fmt.Lots(Math.Floor(i.Lots * f / i.StepLots + 1e-9) * i.StepLots)).ToList();
                    string fix = fits.Count > 0 ? " Pending sizes that fit: " + string.Join(", ", fits) + "." : " Tighten the plan or reduce size.";
                    s.Notes.Add(new Note(Lvl.Bad, "risk_high", k + "riskhigh",
                        "Risk " + Fmt.N(s.RiskPct) + "% if all fill and stop, above your " + Fmt.N(target) + "%." + fix));
                }
                else if (s.FullRisk.Value <= 0)
                    s.Notes.Add(new Note(Lvl.Ok, "risk_free", k + "riskfree", "No risk left: the stop locks " + Fmt.SM(-s.FullRisk.Value) + "."));
                else if (s.RiskPct < target * 0.5)
                    s.Notes.Add(new Note(Lvl.Info, "risk_low", k + "risklow", "Risk " + Fmt.N(s.RiskPct) + "%, well under your " + Fmt.N(target) + "%."));
                else
                    s.Notes.Add(new Note(Lvl.Ok, "risk_ok", k + "riskok", "Risk " + Fmt.N(s.RiskPct) + "% fits your " + Fmt.N(target) + "%."));
            }

            // 4. range scale-in rules
            if (s.ScaleIn)
            {
                var l1 = s.Levels[0]; var l2 = s.Levels[1];
                double e1 = l1.Average(i => i.Entry), e2 = l2.Average(i => i.Entry);
                bool l1Filled = l1.Any(i => i.Filled), l2Filled = l2.All(i => i.Filled);
                string e1s = Fmt.P(e1, dg);
                if (l1Filled && l2Filled)
                {
                    var wrong = s.Items.Where(i => i.Tp.HasValue && Math.Abs(i.Tp.Value - e1) / pip > LevelTolPips).ToList();
                    if (wrong.Count > 0)
                        s.Notes.Add(new Note(Lvl.Bad, "tp2", k + "tp2",
                            "Entry 2 filled: move the TP of all " + s.Items.Count + " positions to " + e1s + " (entry 1). You're out at entry 1 with entry 2 in profit."));
                    else
                        s.Notes.Add(new Note(Lvl.Ok, "tp2_ok", k + "tp2ok", "Both filled and TP is at entry 1 (" + e1s + ")."));
                }
                else
                {
                    var wrong2 = l2.Where(i => !i.Filled && i.Tp.HasValue && Math.Abs(i.Tp.Value - e1) / pip > LevelTolPips).ToList();
                    if (wrong2.Count > 0)
                        s.Notes.Add(new Note(Lvl.Warn, "tp_e2", k + "tpe2",
                            "Entry 2 TP is " + Fmt.P(wrong2[0].Tp.Value, dg) + ". Set it to " + e1s + " (entry 1), so both close at entry 1 if it fills."));
                    if (l1Filled)
                        s.Notes.Add(new Note(Lvl.Info, "e1_only", k + "e1only",
                            "Entry 1 filled: TP at the middle (against the trend) or the other side (with the trend). If entry 2 fills, all TPs go to " + e1s + "."));
                }

                // geometry: stop ~ a full measured move beyond the edge -> about as far beyond entry 2 as the gap
                double gap = (e1 - e2) * sign / pip;
                var sl = s.Items.Where(i => i.Sl.HasValue).Select(i => i.Sl.Value).ToList();
                if (sl.Count > 0 && gap > 0)
                {
                    double beyond = (e2 - sl.Average()) * sign / pip;
                    if (beyond < 0.5 * gap)
                        s.Notes.Add(new Note(Lvl.Warn, "stop_tight", k + "stoptight",
                            "Stop only " + Fmt.N(beyond) + " pips beyond entry 2 (entries are " + Fmt.N(gap) + " pips apart). Plan: a full measured move beyond the edge, about " + Fmt.N(gap) + " pips beyond entry 2."));
                    else if (beyond > 1.6 * gap)
                        s.Notes.Add(new Note(Lvl.Info, "stop_wide", k + "stopwide",
                            "Stop is " + Fmt.N(beyond) + " pips beyond entry 2, wider than the plan (about " + Fmt.N(gap) + ")."));
                }
                double u1 = l1.Sum(i => i.Units), u2 = l2.Sum(i => i.Units);
                if (Math.Abs(u1 - u2) > 1e-6)
                    s.Notes.Add(new Note(Lvl.Info, "sizes", k + "sizes", "Entry sizes differ: " + Fmt.Lots(l1.Sum(i => i.Lots)) + " and " + Fmt.Lots(l2.Sum(i => i.Lots)) + " lot."));
            }

            // 5. spread against the stop
            var stops = s.Items.Where(i => i.SlPips.HasValue && i.SlPips.Value > 0).Select(i => i.SlPips.Value).ToList();
            if (stops.Count > 0 && s.SpreadPips > 0.10 * stops.Min())
                s.Notes.Add(new Note(Lvl.Warn, "spread", k + "spread",
                    "Spread " + Fmt.N(s.SpreadPips) + " pips is " + Fmt.N(s.SpreadPips / stops.Min() * 100, "0") + "% of your " + Fmt.N(stops.Min()) + "-pip stop."));

            // 6. weekend swap: tonight the symbol charges three nights
            if (s.TripleTonight && s.SwapTonight < 0)
                s.Notes.Add(new Note(Lvl.Info, "swap_triple", k + "swap3",
                    "Tonight is triple swap (the weekend's swap): holding costs about " + Fmt.SM(s.SwapTonight) + " instead of " + Fmt.SM(s.SwapNight) + "."));

            // 7. orders left after the trade closed
            if (filled.Count == 0 && justClosed != null && justClosed.Contains(s.Key))
                s.Notes.Add(new Note(Lvl.Bad, "orphan", k + "orphan", "This trade just closed but these orders are still pending. Cancel them?"));
        }
    }

    // ---------------------------------------------------------------- day
    public sealed class ClosedPos
    {
        public long PositionId; public string Symbol; public bool Buy;
        public DateTime Open, Close; public double Net, Gross, Comm, Swap, Units, Lots, EntryPrice, ClosePrice;
    }

    public sealed class TradeGroup
    {
        public string Symbol; public bool Buy; public DateTime Open, Close; public double Net;
        public List<ClosedPos> Parts = new List<ClosedPos>();
    }

    public static class Day
    {
        /// Positions on the same symbol + direction that close within a few minutes of each other are one trade
        /// (a scale-in stopped out together, or closed at entry 1 together).
        public static List<TradeGroup> Group(IEnumerable<ClosedPos> closed, double minutes = 5)
        {
            var res = new List<TradeGroup>();
            foreach (var c in closed.OrderBy(c => c.Close))
            {
                var g = res.LastOrDefault(x => x.Symbol == c.Symbol && x.Buy == c.Buy && (c.Close - x.Close).TotalMinutes <= minutes);
                if (g == null) { g = new TradeGroup { Symbol = c.Symbol, Buy = c.Buy, Open = c.Open, Close = c.Close }; res.Add(g); }
                g.Parts.Add(c); g.Net += c.Net;
                if (c.Close > g.Close) g.Close = c.Close;
                if (c.Open < g.Open) g.Open = c.Open;
            }
            return res;
        }

        /// A scratch (a small loss, under a share of one risk) is not a loss for the daily stop.
        public static double ScratchMoney(double balance, double riskTargetPct, double scratchPctOfRisk) => balance * riskTargetPct / 100 * scratchPctOfRisk / 100;
        public static bool IsLoss(double net, double scratchMoney) => net < -Math.Max(0, scratchMoney);
        public static bool IsScratch(double net, double scratchMoney) => !IsLoss(net, scratchMoney) && net <= Math.Max(0, scratchMoney);

        public static int MaxLosses(double riskTargetPct, int overrideMax) => overrideMax > 0 ? overrideMax : (riskTargetPct > 1.0 ? 2 : 3);
    }

    public enum DayRule { Day_down_in_R, Count_losses }

    /// Where the day stands against the daily stop.
    public sealed class DayState
    {
        public bool Stopped, OneLeft; public int Trades, Losses, Scratches, MaxLosses;
        public double Net, OneR, LimitMoney, Used;   // Used: 0..1 of the limit
        public string Line, Chip, StopText, LastText;
        public List<DayItem> Items = new List<DayItem>();   // today's trades, each marked loss / scratch / win
        public Lvl Level => Stopped ? Lvl.Bad : OneLeft ? Lvl.Warn : Lvl.Ok;
    }

    public sealed class DayItem { public string Text; public Lvl Level; }

    public static class DayRules
    {
        /// oneR: one risk in money at the start of the day. Scratches never count as losses.
        public static DayState Evaluate(List<TradeGroup> today, DayRule rule, double oneR, double limitR, int maxLosses, double scratchMoney)
        {
            var d = new DayState { Trades = today.Count, Net = today.Sum(t => t.Net), OneR = oneR, MaxLosses = maxLosses,
                                   Losses = today.Count(t => Day.IsLoss(t.Net, scratchMoney)),
                                   Scratches = today.Count(t => t.Net < 0 && !Day.IsLoss(t.Net, scratchMoney)) };
            foreach (var t in today.OrderBy(x => x.Close))
            {
                bool loss = Day.IsLoss(t.Net, scratchMoney), scratch = t.Net < 0 && !loss;
                d.Items.Add(new DayItem { Level = loss ? Lvl.Bad : scratch ? Lvl.Info : Lvl.Ok,
                    Text = t.Close.ToString("HH:mm", Fmt.C) + " " + (t.Symbol ?? "") + " " + (t.Buy ? "buy" : "sell") + " " + Fmt.SM(t.Net) +
                           (t.Parts.Count > 1 ? " (" + t.Parts.Count + " positions)" : "") + (loss ? " · loss" : scratch ? " · scratch" : t.Net > 0 ? " · win" : "") });
            }
            string trades = d.Trades + " trade" + (d.Trades == 1 ? "" : "s") + (d.Scratches > 0 ? " (" + d.Scratches + " scratch)" : "");
            if (rule == DayRule.Day_down_in_R)
            {
                d.LimitMoney = limitR * oneR;
                double downR = oneR > 0 ? -d.Net / oneR : 0;
                d.Used = d.LimitMoney > 0 ? Math.Max(0, Math.Min(1, -d.Net / d.LimitMoney)) : 0;
                d.Stopped = d.LimitMoney > 0 && d.Net <= -d.LimitMoney + 1e-9;
                d.OneLeft = !d.Stopped && d.LimitMoney > 0 && d.Net - oneR <= -d.LimitMoney + 1e-9;   // one more full loss would end it
                d.Line = "Day " + Fmt.SM(d.Net) + " (" + Fmt.R(oneR > 0 ? d.Net / oneR : 0) + ")  ·  stop at -" + Fmt.M(d.LimitMoney).TrimStart('-') + " (-" + Fmt.N(limitR, "0.#") + "R)  ·  " + trades;
                d.Chip = (d.Net >= 0 ? "day " + Fmt.R(oneR > 0 ? d.Net / oneR : 0) : "day -" + Fmt.N(downR, "0.0") + "R") + " / -" + Fmt.N(limitR, "0.#") + "R";
                d.StopText = "Daily stop: the day is down " + Fmt.M(-d.Net) + " (" + Fmt.N(downR, "0.0") + "R of your " + Fmt.N(limitR, "0.#") + "R). No new trades today.";
                d.LastText = "One more full loss ends your day: " + Fmt.M(d.LimitMoney + d.Net) + " left before the daily stop.";
            }
            else
            {
                d.Used = maxLosses > 0 ? Math.Min(1, (double)d.Losses / maxLosses) : 0;
                d.Stopped = d.Losses >= maxLosses;
                d.OneLeft = !d.Stopped && d.Losses == maxLosses - 1;
                d.Line = d.Losses + " of " + maxLosses + " losses  ·  " + trades + "  ·  " + Fmt.SM(d.Net);
                d.StopText = "Daily stop: " + d.Losses + " losses today (scratches under " + Fmt.M(scratchMoney) + " not counted). No new trades today.";
                d.LastText = "One more loss ends your day (" + d.Losses + " of " + maxLosses + ").";
            }
            return d;
        }
    }

    // ---------------------------------------------------------------- journal
    public sealed class Campaign
    {
        public string Symbol { get; set; }
        public bool Buy { get; set; }
        public DateTime Start { get; set; }
        public string Kind { get; set; }
        public int PlannedOrders { get; set; }
        public double PlanRisk { get; set; }
        public double PlanRiskPct { get; set; }
        public double PlanReward { get; set; }
        public double PlanSl { get; set; } = double.NaN;
        public double PlanTp { get; set; } = double.NaN;
        public double PipSize { get; set; }
        public double PipValue { get; set; }
        public int Digits { get; set; }
        public double Best { get; set; } = double.NaN;
        public double Worst { get; set; } = double.NaN;
        public bool AfterDailyStop { get; set; }
        public List<long> PosIds { get; set; } = new List<long>();
        public Dictionary<string, double> LastSl { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> LastTp { get; set; } = new Dictionary<string, double>();
        public HashSet<string> Breaks { get; set; } = new HashSet<string>();
        public string Key => Symbol + "|" + (Buy ? "BUY" : "SELL");
    }

    public sealed class TradeRow
    {
        public string Id, Symbol, Dir, Kind, Entries, ExitType, Breaks, Follow = "", FollowText = "";
        public DateTime Open, Close;
        public double Sl = double.NaN, Tp = double.NaN, PlanRisk, PlanRiskPct, PlanReward, Net, R = double.NaN, Comm, Swap,
                      BestR = double.NaN, WorstR = double.NaN, ExitPrice, Units, PipSize, PipValue, Missed;
        public int Digits;
        public bool AfterStop;

        public static readonly string[] Cols = {
            "id","symbol","dir","kind","open","close","entries","sl","tp","plan_risk","plan_risk_pct","plan_reward",
            "net","r","commission","swap","best_r","worst_r","exit","exit_price","units","pip_size","pip_value","digits",
            "breaks","after_daily_stop","follow","missed","follow_text" };

        public string ToCsv() => Csv.Line(new[] {
            Id, Symbol, Dir, Kind, Csv.T(Open), Csv.T(Close), Entries, Csv.D(Sl), Csv.D(Tp), Csv.D(PlanRisk), Csv.D(PlanRiskPct), Csv.D(PlanReward),
            Csv.D(Net), Csv.D(R), Csv.D(Comm), Csv.D(Swap), Csv.D(BestR), Csv.D(WorstR), ExitType, Csv.D(ExitPrice), Csv.D(Units),
            Csv.D(PipSize), Csv.D(PipValue), Digits.ToString(Fmt.C), Breaks, AfterStop ? "1" : "0", Follow, Csv.D(Missed), FollowText });

        public static TradeRow FromCsv(string[] f)
        {
            return new TradeRow {
                Id = f[0], Symbol = f[1], Dir = f[2], Kind = f[3], Open = Csv.PT(f[4]), Close = Csv.PT(f[5]), Entries = f[6],
                Sl = Csv.PD(f[7]), Tp = Csv.PD(f[8]), PlanRisk = Csv.PD(f[9]), PlanRiskPct = Csv.PD(f[10]), PlanReward = Csv.PD(f[11]),
                Net = Csv.PD(f[12]), R = Csv.PD(f[13]), Comm = Csv.PD(f[14]), Swap = Csv.PD(f[15]), BestR = Csv.PD(f[16]), WorstR = Csv.PD(f[17]),
                ExitType = f[18], ExitPrice = Csv.PD(f[19]), Units = Csv.PD(f[20]), PipSize = Csv.PD(f[21]), PipValue = Csv.PD(f[22]),
                Digits = int.Parse(f[23], Fmt.C), Breaks = f[24], AfterStop = f[25] == "1", Follow = f[26], Missed = Csv.PD(f[27]), FollowText = f[28] };
        }
        public bool Buy => Dir == "BUY";
        public bool Win => Net > 0;
    }

    public sealed class CancelRow
    {
        public string Id, Symbol, Dir, Reason, Follow = "", FollowText = "";
        public DateTime Time;
        public double Entry, Sl = double.NaN, Tp = double.NaN, Units, PipSize, PipValue, Result;
        public int Digits;
        public bool Leftover;    // left over from a trade that had just closed: no "what if"
        public static readonly string[] Cols = { "id","symbol","dir","time","reason","entry","sl","tp","units","pip_size","pip_value","digits","leftover","follow","result","follow_text" };
        public string ToCsv() => Csv.Line(new[] { Id, Symbol, Dir, Csv.T(Time), Reason, Csv.D(Entry), Csv.D(Sl), Csv.D(Tp), Csv.D(Units),
            Csv.D(PipSize), Csv.D(PipValue), Digits.ToString(Fmt.C), Leftover ? "1" : "0", Follow, Csv.D(Result), FollowText });
        public static CancelRow FromCsv(string[] f) => new CancelRow {
            Id = f[0], Symbol = f[1], Dir = f[2], Time = Csv.PT(f[3]), Reason = f[4], Entry = Csv.PD(f[5]), Sl = Csv.PD(f[6]), Tp = Csv.PD(f[7]),
            Units = Csv.PD(f[8]), PipSize = Csv.PD(f[9]), PipValue = Csv.PD(f[10]), Digits = int.Parse(f[11], Fmt.C), Leftover = f[12] == "1",
            Follow = f[13], Result = Csv.PD(f[14]), FollowText = f[15] };
        public bool Buy => Dir == "BUY";
    }

    public static class Csv
    {
        public static string D(double v) => double.IsNaN(v) ? "" : Math.Round(v, 6).ToString("0.######", Fmt.C);
        public static double PD(string s) => string.IsNullOrWhiteSpace(s) ? double.NaN : double.Parse(s, Fmt.C);
        public static string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Fmt.C);
        public static DateTime PT(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", Fmt.C);
        public static string Line(IEnumerable<string> f) => string.Join(",", f.Select(Esc));
        static string Esc(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        public static List<string[]> Parse(string text)
        {
            var rows = new List<string[]>(); var cur = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                    else sb.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { cur.Add(sb.ToString()); sb.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    cur.Add(sb.ToString()); sb.Clear(); if (cur.Count > 1 || cur[0] != "") rows.Add(cur.ToArray()); cur = new List<string>();
                }
                else sb.Append(c);
            }
            if (sb.Length > 0 || cur.Count > 0) { cur.Add(sb.ToString()); rows.Add(cur.ToArray()); }
            return rows;
        }
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    }

    public struct PBar { public DateTime T; public double H, L; public PBar(DateTime t, double h, double l) { T = t; H = h; L = l; } }

    public static class Follow
    {
        /// After an exit at exitPrice: does price reach tp before sl? ("tp", "sl", or "" if neither yet).
        /// A bar that touches both counts as "sl" (assume the worst).
        public static string FirstHit(bool buy, double tp, double sl, IEnumerable<PBar> bars)
        {
            foreach (var b in bars)
            {
                bool hitSl = !double.IsNaN(sl) && (buy ? b.L <= sl : b.H >= sl);
                bool hitTp = !double.IsNaN(tp) && (buy ? b.H >= tp : b.L <= tp);
                if (hitSl) return "sl";
                if (hitTp) return "tp";
            }
            return "";
        }

        /// A cancelled order: would it have filled, and then hit tp or sl first?  ("never", "tp", "sl", or "" if open).
        public static string WhatIf(bool buy, double entry, double tp, double sl, IEnumerable<PBar> bars, bool windowOver)
        {
            bool filled = false;
            foreach (var b in bars)
            {
                if (!filled)
                {
                    if (buy ? b.L <= entry : b.H >= entry)
                    {
                        filled = true;
                        if (!double.IsNaN(sl) && (buy ? b.L <= sl : b.H >= sl)) return "sl";
                    }
                    continue;
                }
                bool hitSl = !double.IsNaN(sl) && (buy ? b.L <= sl : b.H >= sl);
                bool hitTp = !double.IsNaN(tp) && (buy ? b.H >= tp : b.L <= tp);
                if (hitSl) return "sl";
                if (hitTp) return "tp";
            }
            return windowOver ? (filled ? "open" : "never") : "";
        }

        public static void Apply(TradeRow t, IEnumerable<PBar> barsAfterExit, bool windowOver)
        {
            if (t.Follow != "") return;
            if (t.ExitType == "TP" || double.IsNaN(t.Tp)) { t.Follow = "n/a"; return; }
            double sl = t.ExitType == "SL" ? double.NaN : t.Sl;   // after a stop-out only ask: did the TP come anyway?
            string hit = FirstHit(t.Buy, t.Tp, sl, barsAfterExit);
            double sign = t.Buy ? 1 : -1;
            if (hit == "tp")
            {
                t.Follow = "tp_after";
                t.Missed = (t.Tp - t.ExitPrice) * sign / t.PipSize * t.PipValue * t.Units;
                t.FollowText = t.ExitType == "SL"
                    ? "Stopped out, then price reached your TP " + Fmt.P(t.Tp, t.Digits) + "."
                    : "You closed early; price then reached your TP " + Fmt.P(t.Tp, t.Digits) + ".";
            }
            else if (hit == "sl")
            {
                t.Follow = "sl_after";
                t.Missed = (t.Sl - t.ExitPrice) * sign / t.PipSize * t.PipValue * t.Units;   // negative = the loss you avoided
                t.FollowText = "Good exit: price went on to your stop " + Fmt.P(t.Sl, t.Digits) + ".";
            }
            else if (windowOver) { t.Follow = "none"; t.FollowText = "Price reached neither TP nor stop within a day."; }
        }

        public static void Apply(CancelRow c, IEnumerable<PBar> barsAfter, bool windowOver)
        {
            if (c.Follow != "") return;
            if (c.Leftover) { c.Follow = "n/a"; return; }
            string r = WhatIf(c.Buy, c.Entry, c.Tp, c.Sl, barsAfter, windowOver);
            if (r == "") return;
            double sign = c.Buy ? 1 : -1;
            c.Follow = r;
            if (r == "tp") { c.Result = (c.Tp - c.Entry) * sign / c.PipSize * c.PipValue * c.Units; c.FollowText = "Would have filled and hit TP."; }
            else if (r == "sl") { c.Result = (c.Sl - c.Entry) * sign / c.PipSize * c.PipValue * c.Units; c.FollowText = "Would have filled and hit the stop: good cancel."; }
            else if (r == "never") c.FollowText = "Would not have filled within a day.";
            else c.FollowText = "Would have filled; no TP or stop within a day.";
        }
    }

    public static class Report
    {
        static string M(double v) => Fmt.M(v);
        static string Pct(int a, int b) => b == 0 ? "-" : (100.0 * a / b).ToString("0", Fmt.C) + "%";

        static readonly Dictionary<string, string> RuleNames = new Dictionary<string, string> {
            { "no_sl", "No stop loss" }, { "sl_side", "Stop on the wrong side" }, { "no_tp", "No TP" }, { "tp_side", "TP on the wrong side" },
            { "stops_differ", "Stops not shared" }, { "risk_high", "Risk above target" }, { "tp2", "TP not moved to entry 1 after entry 2 filled" },
            { "tp_e2", "Entry 2 TP not at entry 1" }, { "stop_tight", "Stop closer than the plan" }, { "spread", "Spread wide for the stop" },
            { "orphan", "Order left pending after the trade closed" }, { "both_dirs", "Buys and sells at the same time" }, { "after_stop", "Traded after the daily stop" } };
        public static string RuleName(string code) => RuleNames.TryGetValue(code, out var n) ? n : code;

        sealed class Lesson { public double Impact; public string Text; }

        /// A scratch: a result within scratchR of zero (R known), or exactly zero.
        public static bool IsScratch(TradeRow x, double scratchR) => double.IsNaN(x.R) ? x.Net == 0 : Math.Abs(x.R) <= scratchR;

        public static string Build(List<TradeRow> trades, List<CancelRow> cancels, double riskTarget, DateTime now, double scratchR = 0.2)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Trade Watch report");
            sb.AppendLine();
            sb.AppendLine("Made " + now.ToString("yyyy-MM-dd HH:mm", Fmt.C) + " (Amsterdam time). Risk target " + Fmt.N(riskTarget) + "% per trade. Money in account currency; R = result / planned risk.");
            sb.AppendLine();
            if (trades.Count == 0)
            {
                sb.AppendLine("No finished trades logged yet.");
                AppendCancels(sb, cancels);
                return sb.ToString();
            }

            var t = trades.OrderBy(x => x.Close).ToList();
            int scratches = t.Count(x => IsScratch(x, scratchR)), wins = t.Count(x => x.Net > 0 && !IsScratch(x, scratchR)), losses = t.Count(x => x.Net < 0 && !IsScratch(x, scratchR));
            double net = t.Sum(x => x.Net), comm = t.Sum(x => x.Comm), swap = t.Sum(x => x.Swap);
            var rs = t.Where(x => !double.IsNaN(x.R)).Select(x => x.R).ToList();
            double grossWin = t.Where(x => x.Net > 0).Sum(x => x.Net), grossLoss = -t.Where(x => x.Net < 0).Sum(x => x.Net);

            sb.AppendLine("## Overview");
            sb.AppendLine();
            sb.AppendLine("| | |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| Period | " + t.First().Open.ToString("yyyy-MM-dd", Fmt.C) + " to " + t.Last().Close.ToString("yyyy-MM-dd", Fmt.C) + " |");
            sb.AppendLine("| Trades | " + t.Count + " (" + wins + " won, " + losses + " lost, " + scratches + " scratch) |");
            sb.AppendLine("| Win rate | " + Pct(wins, wins + losses) + " (scratches left out) |");
            sb.AppendLine("| Net result | " + Fmt.SM(net) + " |");
            sb.AppendLine("| Average R | " + (rs.Count > 0 ? Fmt.R(rs.Average()) : "-") + " |");
            sb.AppendLine("| Average win / loss | " + (wins > 0 ? M(grossWin / wins) : "-") + " / " + (losses > 0 ? M(-grossLoss / losses) : "-") + " |");
            sb.AppendLine("| Profit factor | " + (grossLoss > 0 ? (grossWin / grossLoss).ToString("0.00", Fmt.C) : "-") + " |");
            sb.AppendLine("| Best / worst trade | " + M(t.Max(x => x.Net)) + " / " + M(t.Min(x => x.Net)) + " |");
            sb.AppendLine("| Costs paid | commission " + M(comm) + ", swap " + M(swap) + " |");
            var pr = t.Where(x => !double.IsNaN(x.PlanRiskPct)).Select(x => x.PlanRiskPct).ToList();
            sb.AppendLine("| Average planned risk | " + (pr.Count > 0 ? Fmt.N(pr.Average()) + "%" : "-") + " |");
            sb.AppendLine("| Max losing streak | " + Streak(t, scratchR) + " (scratches don't break or add to it) |");
            sb.AppendLine();

            // lessons
            var lessons = new List<Lesson>();
            var early = t.Where(x => x.Follow == "tp_after" && x.ExitType != "SL").ToList();
            if (early.Count > 0) lessons.Add(new Lesson { Impact = early.Sum(x => x.Missed),
                Text = "You closed " + early.Count + " trade(s) early that went on to your TP. That left " + M(early.Sum(x => x.Missed)) + " on the table. Let the plan run unless the chart changed." });
            var stopThenTp = t.Where(x => x.Follow == "tp_after" && x.ExitType == "SL").ToList();
            if (stopThenTp.Count > 0) lessons.Add(new Lesson { Impact = stopThenTp.Sum(x => -x.Net),
                Text = stopThenTp.Count + " stop(s) were hit and then price reached the TP anyway (you lost " + M(-stopThenTp.Sum(x => x.Net)) + " on them). Check the stop: a full measured move beyond the edge, not just past the last wick." });
            var high = t.Where(x => x.Breaks.Contains("risk_high") && x.Net < 0 && x.PlanRiskPct > 0).ToList();
            if (high.Count > 0)
            {
                double extra = high.Sum(x => -x.Net * (1 - riskTarget / x.PlanRiskPct));
                lessons.Add(new Lesson { Impact = Math.Max(0, extra), Text = high.Count + " losing trade(s) risked more than " + Fmt.N(riskTarget) + "%. The extra size cost " + M(extra) + ". Size from the stop before placing." });
            }
            var tp2 = t.Where(x => x.Breaks.Contains("tp2")).ToList();
            if (tp2.Count > 0) lessons.Add(new Lesson { Impact = Math.Max(0, -tp2.Where(x => x.Net < 0).Sum(x => x.Net)),
                Text = tp2.Count + " scale-in(s) had entry 2 filled while TP was not yet at entry 1 (net " + M(tp2.Sum(x => x.Net)) + "). Move all TPs to entry 1 the moment entry 2 fills." });
            var after = t.Where(x => x.AfterStop).ToList();
            if (after.Count > 0) lessons.Add(new Lesson { Impact = Math.Max(0, -after.Sum(x => x.Net)),
                Text = after.Count + " trade(s) were taken after the daily stop (net " + M(after.Sum(x => x.Net)) + "). After the last allowed loss, close the platform." });
            var noSl = t.Where(x => x.Breaks.Contains("no_sl")).ToList();
            if (noSl.Count > 0) lessons.Add(new Lesson { Impact = Math.Max(1, -noSl.Where(x => x.Net < 0).Sum(x => x.Net)),
                Text = noSl.Count + " trade(s) were open without a stop at some point. Put the SL on the order itself." });
            var tight = t.Where(x => x.Breaks.Contains("stop_tight")).ToList();
            if (tight.Count > 0 && tight.Count(x => x.Net < 0) > 0) lessons.Add(new Lesson { Impact = -tight.Where(x => x.Net < 0).Sum(x => x.Net),
                Text = tight.Count + " scale-in(s) had a stop closer than the plan; " + tight.Count(x => x.Net < 0) + " of them lost (" + M(tight.Sum(x => x.Net)) + ")." });
            var cWon = cancels.Where(c => c.Follow == "tp").ToList();
            if (cWon.Count > 0) lessons.Add(new Lesson { Impact = cWon.Sum(c => c.Result),
                Text = cWon.Count + " order(s) you cancelled would have won " + M(cWon.Sum(c => c.Result)) + ". If the setup was still valid, let the order sit." });
            if (comm + swap < 0 && grossWin > 0 && -(comm + swap) > 0.15 * grossWin) lessons.Add(new Lesson { Impact = -(comm + swap),
                Text = "Costs (" + M(comm + swap) + ") ate " + (-(comm + swap) / grossWin * 100).ToString("0", Fmt.C) + "% of your winnings. Wider stops with smaller size, or fewer trades, keep more of it." });
            foreach (var g in t.GroupBy(x => x.Open.Hour).Where(g => g.Count() >= 3 && g.Sum(x => x.Net) < 0))
                lessons.Add(new Lesson { Impact = -g.Sum(x => x.Net) * 0.5,
                    Text = "Trades opened " + g.Key.ToString("00", Fmt.C) + ":00-" + g.Key.ToString("00", Fmt.C) + ":59 lost " + M(-g.Sum(x => x.Net)) + " over " + g.Count() + " trades." });
            foreach (var g in t.GroupBy(x => x.Symbol).Where(g => g.Count() >= 3 && g.Sum(x => x.Net) < 0))
                lessons.Add(new Lesson { Impact = -g.Sum(x => x.Net) * 0.5,
                    Text = g.Key + " lost " + M(-g.Sum(x => x.Net)) + " over " + g.Count() + " trades." });

            sb.AppendLine("## What to work on");
            sb.AppendLine();
            var top = lessons.OrderByDescending(l => l.Impact).ToList();
            if (top.Count == 0) sb.AppendLine("Nothing stands out yet: rules were followed and nothing was left on the table.");
            for (int i = 0; i < Math.Min(3, top.Count); i++) sb.AppendLine((i + 1) + ". " + top[i].Text);
            sb.AppendLine();
            if (top.Count > 3)
            {
                sb.AppendLine("Also worth a look:");
                sb.AppendLine();
                foreach (var l in top.Skip(3)) sb.AppendLine("- " + l.Text);
                sb.AppendLine();
            }

            // what went well
            var good = new List<string>();
            var clean = t.Where(x => string.IsNullOrEmpty(x.Breaks)).ToList();
            var broken = t.Where(x => !string.IsNullOrEmpty(x.Breaks)).ToList();
            if (clean.Count > 0) good.Add("Trades with no rule broken: " + clean.Count + ", net " + M(clean.Sum(x => x.Net)) + (broken.Count > 0 ? " (with a rule broken: " + broken.Count + ", net " + M(broken.Sum(x => x.Net)) + ")" : "") + ".");
            var savedExits = t.Where(x => x.Follow == "sl_after" && x.ExitType != "SL").ToList();
            if (savedExits.Count > 0) good.Add(savedExits.Count + " early exit(s) were right: price went on to the stop, saving " + M(-savedExits.Sum(x => x.Missed)) + ".");
            var cGood = cancels.Where(c => c.Follow == "sl").ToList();
            if (cGood.Count > 0) good.Add(cGood.Count + " cancelled order(s) would have lost " + M(cGood.Sum(c => c.Result)) + ": good cancels.");
            var bestKind = t.GroupBy(x => x.Kind).Where(g => g.Count() >= 3).OrderByDescending(g => g.Sum(x => x.Net)).FirstOrDefault();
            if (bestKind != null && bestKind.Sum(x => x.Net) > 0) good.Add("Best setup type: " + bestKind.Key + " (" + M(bestKind.Sum(x => x.Net)) + " over " + bestKind.Count() + ").");
            if (good.Count > 0)
            {
                sb.AppendLine("## What went well");
                sb.AppendLine();
                foreach (var g in good) sb.AppendLine("- " + g);
                sb.AppendLine();
            }

            // missed out
            sb.AppendLine("## What you missed out on");
            sb.AppendLine();
            sb.AppendLine("| | Count | Money (from your exit or entry to the TP) |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine("| Closed early, then price hit TP | " + early.Count + " | " + M(early.Sum(x => x.Missed)) + " |");
            sb.AppendLine("| Stopped out, then price hit TP | " + stopThenTp.Count + " | " + M(stopThenTp.Sum(x => x.Missed)) + " |");
            sb.AppendLine("| Cancelled orders that would have won | " + cWon.Count + " | " + M(cWon.Sum(c => c.Result)) + " |");
            sb.AppendLine("| Cancelled orders that would have lost | " + cGood.Count + " | " + M(cGood.Sum(c => c.Result)) + " |");
            var mfe = t.Where(x => !double.IsNaN(x.BestR) && !double.IsNaN(x.R) && x.BestR > 0).ToList();
            if (mfe.Count > 0) sb.AppendLine("| Average best point reached vs result | " + mfe.Count + " | " + Fmt.R(mfe.Average(x => x.BestR)) + " vs " + Fmt.R(mfe.Average(x => x.R)) + " |");
            var losersUp = t.Where(x => x.Net < 0 && !double.IsNaN(x.BestR) && x.BestR >= 1).ToList();
            if (losersUp.Count > 0) sb.AppendLine("| Losers that were +1R or more first | " + losersUp.Count + " | " + M(losersUp.Sum(x => x.Net)) + " |");
            sb.AppendLine();

            // rules
            var codes = t.SelectMany(x => (x.Breaks ?? "").Split(';')).Where(c => c != "").Distinct().ToList();
            if (codes.Count > 0)
            {
                sb.AppendLine("## Rules broken");
                sb.AppendLine();
                sb.AppendLine("| Rule | Trades | Net on those |");
                sb.AppendLine("|---|---|---|");
                foreach (var c in codes.OrderByDescending(c => t.Count(x => x.Breaks.Split(';').Contains(c))))
                {
                    var tt = t.Where(x => x.Breaks.Split(';').Contains(c)).ToList();
                    sb.AppendLine("| " + RuleName(c) + " | " + tt.Count + " | " + M(tt.Sum(x => x.Net)) + " |");
                }
                sb.AppendLine();
            }

            Table(sb, "By setup type", t.GroupBy(x => x.Kind));
            Table(sb, "By symbol", t.GroupBy(x => x.Symbol + " " + x.Dir.ToLowerInvariant()));
            Table(sb, "By hour opened", t.GroupBy(x => x.Open.Hour.ToString("00", Fmt.C) + ":00"));
            Table(sb, "By weekday", t.GroupBy(x => x.Open.DayOfWeek.ToString()));
            Table(sb, "By exit", t.GroupBy(x => x.ExitType));

            sb.AppendLine("## Last 20 trades");
            sb.AppendLine();
            sb.AppendLine("| Closed | Trade | Entries | Plan risk | Result | R | Best | Exit | Notes |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var x in t.AsEnumerable().Reverse().Take(20))
            {
                string notes = string.Join("; ", (x.Breaks ?? "").Split(';').Where(c => c != "").Select(RuleName));
                if (x.AfterStop) notes = (notes == "" ? "" : notes + "; ") + "after daily stop";
                if (!string.IsNullOrEmpty(x.FollowText)) notes = (notes == "" ? "" : notes + "; ") + x.FollowText;
                sb.AppendLine("| " + x.Close.ToString("MM-dd HH:mm", Fmt.C) + " | " + x.Symbol + " " + x.Dir.ToLowerInvariant() + " " + x.Kind +
                    " | " + x.Entries + " | " + (double.IsNaN(x.PlanRisk) ? "no stop" : M(x.PlanRisk) + " (" + Fmt.N(x.PlanRiskPct) + "%)") + " | " + Fmt.SM(x.Net) +
                    " | " + (double.IsNaN(x.R) ? "-" : Fmt.R(x.R)) + " | " + (double.IsNaN(x.BestR) ? "-" : Fmt.R(x.BestR)) +
                    " | " + x.ExitType + " | " + notes.Replace("|", "/") + " |");
            }
            sb.AppendLine();
            AppendCancels(sb, cancels);
            return sb.ToString();
        }

        static int Streak(List<TradeRow> t, double scratchR)
        {
            int best = 0, cur = 0;
            foreach (var x in t)
            {
                if (IsScratch(x, scratchR)) continue;
                if (x.Net < 0) { cur++; best = Math.Max(best, cur); } else cur = 0;
            }
            return best;
        }

        static void Table(StringBuilder sb, string title, IEnumerable<IGrouping<string, TradeRow>> groups)
        {
            var gs = groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            if (gs.Count < 2) return;
            sb.AppendLine("## " + title);
            sb.AppendLine();
            sb.AppendLine("| | Trades | Win rate | Net | Avg R |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var g in gs)
            {
                var r = g.Where(x => !double.IsNaN(x.R)).Select(x => x.R).ToList();
                sb.AppendLine("| " + g.Key + " | " + g.Count() + " | " + Pct(g.Count(x => x.Net > 0), g.Count()) + " | " + Fmt.SM(g.Sum(x => x.Net)) + " | " + (r.Count > 0 ? Fmt.R(r.Average()) : "-") + " |");
            }
            sb.AppendLine();
        }

        static void AppendCancels(StringBuilder sb, List<CancelRow> cancels)
        {
            var c = cancels.Where(x => !x.Leftover).OrderByDescending(x => x.Time).Take(15).ToList();
            if (c.Count == 0) return;
            sb.AppendLine("## Cancelled and expired orders");
            sb.AppendLine();
            sb.AppendLine("| When | Order | Entry | Why | What if |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var x in c)
                sb.AppendLine("| " + x.Time.ToString("MM-dd HH:mm", Fmt.C) + " | " + x.Symbol + " " + x.Dir.ToLowerInvariant() + " | " + Fmt.P(x.Entry, x.Digits) +
                    " | " + x.Reason + " | " + (x.FollowText == "" ? "checking..." : x.FollowText + (x.Follow == "tp" || x.Follow == "sl" ? " (" + Fmt.SM(x.Result) + ")" : "")) + " |");
            sb.AppendLine();
        }
    }

    public static class TradeBuilder
    {
        /// Turns a finished campaign plus its closed positions into one journal row.
        public static TradeRow Finish(Campaign c, List<ClosedPos> parts, double exitTolPips = 1.0)
        {
            double units = parts.Sum(p => p.Units);
            double sign = c.Buy ? 1 : -1;
            double avgEntry = units > 0 ? parts.Sum(p => p.EntryPrice * p.Units) / units : 0;
            double avgExit = units > 0 ? parts.Sum(p => p.ClosePrice * p.Units) / units : 0;
            var types = new List<string>();
            foreach (var p in parts)
            {
                string id = p.PositionId.ToString(Fmt.C);
                bool sl = c.LastSl.TryGetValue(id, out var s) && Math.Abs(p.ClosePrice - s) / c.PipSize <= exitTolPips;
                bool tp = c.LastTp.TryGetValue(id, out var tv) && Math.Abs(p.ClosePrice - tv) / c.PipSize <= exitTolPips;
                types.Add(tp ? "TP" : sl ? "SL" : "manual");
            }
            string exit = types.Distinct().Count() == 1 ? types[0] : "mixed";
            double net = parts.Sum(p => p.Net);
            var row = new TradeRow
            {
                Id = string.Join("-", parts.Select(p => p.PositionId.ToString(Fmt.C))),
                Symbol = c.Symbol, Dir = c.Buy ? "BUY" : "SELL", Kind = c.Kind,
                Open = parts.Min(p => p.Open), Close = parts.Max(p => p.Close),
                Entries = string.Join(" + ", parts.OrderBy(p => p.Open).Select(p => Fmt.P(p.EntryPrice, c.Digits) + "x" + Fmt.Lots(p.Lots > 0 ? p.Lots : p.Units / 100000.0))),
                Sl = c.PlanSl, Tp = c.PlanTp, PlanRisk = c.PlanRisk, PlanRiskPct = c.PlanRiskPct, PlanReward = c.PlanReward,
                Net = net, R = c.PlanRisk > 0 ? net / c.PlanRisk : double.NaN,
                Comm = parts.Sum(p => p.Comm), Swap = parts.Sum(p => p.Swap),
                ExitType = exit, ExitPrice = avgExit, Units = units, PipSize = c.PipSize, PipValue = c.PipValue, Digits = c.Digits,
                Breaks = string.Join(";", c.Breaks.OrderBy(b => b, StringComparer.Ordinal)), AfterStop = c.AfterDailyStop
            };
            if (!double.IsNaN(c.Best) && c.PlanRisk > 0) row.BestR = (c.Best - avgEntry) * sign / c.PipSize * c.PipValue * units / c.PlanRisk;
            if (!double.IsNaN(c.Worst) && c.PlanRisk > 0) row.WorstR = (c.Worst - avgEntry) * sign / c.PipSize * c.PipValue * units / c.PlanRisk;
            return row;
        }

        public static string Kind(Setup s)
        {
            if (s.ScaleIn) return "scale-in";
            return s.Items.Count == 1 ? "single" : "multi";
        }
    }

    // ---------------------------------------------------------------- panel model
    // What the chart panel shows, as plain data. The bot draws it with cTrader controls;
    // the tests draw the same model as HTML for a preview.
    public sealed class PMark { public double Pos; public string Label; public string Kind; }   // sl, tp, fill, pend, now
    public sealed class PRow { public string Name, Lots, Entry, Status, Sl, Tp, Pnl; public bool Filled; public double PnlVal; }
    public sealed class PCard
    {
        public string Symbol, Kind, Stage; public bool Buy; public Lvl Status;
        public string Risk, RiskSub, Reward, RewardSub, Now, NowSub;
        public Lvl RiskLvl; public double NowVal; public bool HasNow, HasReward;
        public double MeterFill, MeterTarget;            // 0..1 of the bar width
        public double ZoneSl = double.NaN, ZoneEntry = double.NaN, ZoneTp = double.NaN;   // 0..1 on the price ladder (stop always left)
        public List<PMark> Marks = new List<PMark>();
        public List<PRow> Rows = new List<PRow>();
        public string Costs;
        public List<Note> Notes = new List<Note>();
    }
    public sealed class PanelModel
    {
        public string Title, Balance, Target, AllRisk, Footer;
        public string DayLine, DayChip; public List<DayItem> DayItems = new List<DayItem>(); public int Losses, MaxLosses; public double DayUsed; public Lvl DayLvl, FooterLvl;
        public List<Note> Global = new List<Note>();
        public List<PCard> Cards = new List<PCard>();
        static readonly JsonSerializerOptions SigOpt = new JsonSerializerOptions { IncludeFields = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        public string Signature() => JsonSerializer.Serialize(this, SigOpt);
    }

    public static class PanelBuilder
    {
        public static PanelModel Build(List<Setup> setups, List<Note> global, double balance, double equity, double target,
                                       DayState day, string footer, Lvl footerLvl, string version)
        {
            var m = new PanelModel
            {
                Title = "TRADE WATCH " + version,
                Balance = "Balance " + Fmt.M(balance) + "  ·  equity " + Fmt.M(equity),
                Target = "Risk per trade " + Fmt.N(target) + "% = " + Fmt.M(balance * target / 100),
                DayLine = day.Line, DayChip = day.Chip, DayItems = day.Items, Losses = day.Losses, MaxLosses = day.Chip == null ? day.MaxLosses : 0, DayUsed = day.Used, DayLvl = day.Level,
                Global = global.OrderByDescending(n => n.Level).ToList(), Footer = footer, FooterLvl = footerLvl
            };
            if (setups.Count > 1)
            {
                double all = setups.Where(x => x.FullRisk.HasValue).Sum(x => x.FullRisk.Value);
                m.AllRisk = "All setups, if every order fills and stops: " + Fmt.M(all) + " (" + Fmt.N(balance > 0 ? all / balance * 100 : 0) + "%)" +
                            (setups.Any(x => !x.FullRisk.HasValue) ? " + unlimited (no SL)" : "");
            }
            foreach (var s in setups) m.Cards.Add(Card(s, target));
            return m;
        }

        static PCard Card(Setup s, double target)
        {
            int dg = s.Digits;
            var c = new PCard { Symbol = s.Symbol, Buy = s.Buy, Kind = TradeBuilder.Kind(s), Stage = s.Stage,
                                Status = s.Worst == Lvl.Info ? Lvl.Ok : s.Worst, Notes = s.Notes };
            if (s.FullRisk.HasValue)
            {
                c.Risk = s.FullRisk.Value <= 0 ? "none" : Fmt.M(s.FullRisk.Value);
                c.RiskSub = s.FullRisk.Value <= 0 ? "stop locks " + Fmt.SM(-s.FullRisk.Value) : Fmt.N(s.RiskPct) + "% of balance";
                c.RiskLvl = s.RiskPct > target * 1.10 ? Lvl.Bad : Lvl.Ok;
                c.MeterFill = Math.Max(0, Math.Min(1, s.RiskPct / (target * 1.5)));
            }
            else { c.Risk = "NO STOP"; c.RiskSub = "unlimited"; c.RiskLvl = Lvl.Bad; c.MeterFill = 1; }
            c.MeterTarget = 1 / 1.5;
            c.HasReward = s.FullReward.HasValue;
            c.Reward = s.FullReward.HasValue ? Fmt.SM(s.FullReward.Value) : "no TP";
            c.RewardSub = s.FullReward.HasValue && s.FullRisk.HasValue && s.FullRisk.Value > 0 ? Fmt.N(s.FullReward.Value / s.FullRisk.Value, "0.00") + "R" : "";
            c.HasNow = s.Items.Any(i => i.Filled);
            c.NowVal = s.Floating;
            c.Now = c.HasNow ? Fmt.SM(s.Floating) : "not filled";
            c.NowSub = c.HasNow && s.FloatR.HasValue ? Fmt.R(s.FloatR.Value) : "";

            foreach (var it in s.Levels.SelectMany(l => l))
                c.Rows.Add(new PRow
                {
                    Name = s.LevelName(it), Lots = Fmt.Lots(it.Lots), Entry = Fmt.P(it.Entry, dg), Status = it.Filled ? "filled" : it.OrderType,
                    Sl = it.Sl.HasValue ? Fmt.P(it.Sl.Value, dg) : "none", Tp = it.Tp.HasValue ? Fmt.P(it.Tp.Value, dg) : "none",
                    Pnl = it.Filled ? Fmt.SM(it.NetProfit) : "", PnlVal = it.NetProfit, Filled = it.Filled
                });
            c.Costs = "Spread " + Fmt.N(s.SpreadPips) + "p  ·  commission " + Fmt.M(s.CommRT) + (s.CommAllKnown ? "" : " (estimate)") +
                      (c.HasNow ? "  ·  swap " + Fmt.SM(s.SwapNight) + "/night" + (s.TripleTonight ? ", tonight ×3 = " + Fmt.SM(s.SwapTonight) : s.TripleDay != "" ? " (×3 on " + s.TripleDay + ")" : "") : "");

            // price ladder: stop on the left, target on the right, whatever the direction
            var prices = new List<double>();
            foreach (var it in s.Items) { prices.Add(it.Entry); if (it.Sl.HasValue) prices.Add(it.Sl.Value); if (it.Tp.HasValue) prices.Add(it.Tp.Value); }
            double now = s.Items.Select(i => i.Current).FirstOrDefault(v => !double.IsNaN(v) && v > 0);
            if (now > 0) prices.Add(now); else now = double.NaN;
            double lo = prices.Min(), hi = prices.Max();
            if (hi - lo < s.PipSize) { lo -= s.PipSize; hi += s.PipSize; }
            double pad = (hi - lo) * 0.04; lo -= pad; hi += pad;
            Func<double, double> X = v => s.Buy ? (v - lo) / (hi - lo) : (hi - v) / (hi - lo);
            var sls = s.Items.Where(i => i.Sl.HasValue).Select(i => i.Sl.Value).Distinct().ToList();
            var tps = s.Items.Where(i => i.Tp.HasValue).Select(i => i.Tp.Value).Distinct().OrderBy(v => X(v)).ToList();
            foreach (var v in sls) c.Marks.Add(new PMark { Pos = X(v), Label = "SL", Kind = "sl" });
            for (int i = 0; i < tps.Count; i++) c.Marks.Add(new PMark { Pos = X(tps[i]), Label = tps.Count > 1 ? "TP" + (i + 1) : "TP", Kind = "tp" });
            for (int i = 0; i < s.Levels.Count; i++)
            {
                var l = s.Levels[i];
                c.Marks.Add(new PMark { Pos = X(l.Average(x => x.Entry)), Label = (s.ScaleIn ? "E" : "#") + (i + 1), Kind = l.Any(x => x.Filled) ? "fill" : "pend" });
            }
            if (!double.IsNaN(now)) c.Marks.Add(new PMark { Pos = X(now), Label = "now", Kind = "now" });
            if (sls.Count > 0) c.ZoneSl = sls.Min(X);
            c.ZoneEntry = X(s.Levels[0].Average(x => x.Entry));
            if (tps.Count > 0) c.ZoneTp = tps.Max(X);
            return c;
        }
    }

    // ---------------------------------------------------------------- report as a web page
    public static class ReportHtml
    {
        static string H(string t) => System.Net.WebUtility.HtmlEncode(t ?? "");

        static string Inline(string t)
        {
            var e = H(t);
            e = System.Text.RegularExpressions.Regex.Replace(e, @"(?<![\w.])(-€[\d.]+|-[\d.]+R)(?!\w)", "<span class=neg>$1</span>");
            e = System.Text.RegularExpressions.Regex.Replace(e, @"(?<![\w.])(\+€[\d.]+|\+[\d.]+R)(?!\w)", "<span class=pos>$1</span>");
            return e;
        }

        /// The markdown report as a coloured page, with tiles and charts on top. Opens in any browser.
        public static string Build(List<TradeRow> trades, List<CancelRow> cancels, double target, DateTime now, double scratchR = 0.2)
        {
            string md = Report.Build(trades, cancels, target, now, scratchR);
            var t = trades.OrderBy(x => x.Close).ToList();
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>Trade Watch report</title><style>");
            sb.Append(@":root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--dim:#6b7383;--line:#e3e6eb;--pos:#16a34a;--neg:#dc2626;--warn:#d97706;--acc:#2563eb}
@media (prefers-color-scheme:dark){:root{--bg:#12151b;--card:#1b1f27;--ink:#e6e9ef;--dim:#9aa4b2;--line:#2c323d;--pos:#4cc38a;--neg:#ff6b6b;--warn:#f2b84b;--acc:#7fb3ff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}
main{max-width:980px;margin:0 auto;padding:20px 16px 60px}h1{font-size:26px;margin:4px 0}h2{font-size:18px;margin:28px 0 10px}
.sub{color:var(--dim);font-size:13px}.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin:16px 0}
.tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.tile b{display:block;font-size:24px;font-variant-numeric:tabular-nums}
.tile span{color:var(--dim);font-size:12px;text-transform:uppercase;letter-spacing:.04em}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:14px;margin:10px 0;overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:14px;font-variant-numeric:tabular-nums}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{color:var(--dim);font-weight:600;font-size:12px;text-transform:uppercase}.pos{color:var(--pos);font-weight:600}.neg{color:var(--neg);font-weight:600}
ol.work{padding:0;list-style:none;counter-reset:w}ol.work li{counter-increment:w;background:var(--card);border:1px solid var(--line);border-left:5px solid var(--warn);border-radius:10px;padding:12px 14px 12px 48px;margin:8px 0;position:relative}
ol.work li:before{content:counter(w);position:absolute;left:14px;top:11px;width:24px;height:24px;border-radius:50%;background:var(--warn);color:#fff;font-weight:700;text-align:center;line-height:24px}
ul.good{border:1px solid var(--line);border-left:5px solid var(--pos);background:var(--card);border-radius:10px;padding:10px 14px 10px 34px}ul.good li{margin:4px 0}
svg text{fill:var(--dim);font-size:11px}</style></head><body><main>");

            sb.Append("<h1>Trade Watch report</h1><div class=sub>Made " + H(now.ToString("yyyy-MM-dd HH:mm", Fmt.C)) + " (Amsterdam time) · risk target " + H(Fmt.N(target)) + "% per trade · R = result ÷ planned risk</div>");
            if (t.Count > 0)
            {
                int wins = t.Count(x => x.Net > 0 && !Report.IsScratch(x, scratchR)), lost = t.Count(x => x.Net < 0 && !Report.IsScratch(x, scratchR)); double net = t.Sum(x => x.Net);
                var rs = t.Where(x => !double.IsNaN(x.R)).Select(x => x.R).ToList();
                double gw = t.Where(x => x.Net > 0).Sum(x => x.Net), gl = -t.Where(x => x.Net < 0).Sum(x => x.Net);
                string Tile(string label, string val, string cls) => "<div class=tile><span>" + H(label) + "</span><b class=\"" + cls + "\">" + H(val) + "</b></div>";
                sb.Append("<div class=tiles>");
                sb.Append(Tile("Net result", Fmt.SM(net), net >= 0 ? "pos" : "neg"));
                sb.Append(Tile("Trades", t.Count.ToString(Fmt.C), ""));
                sb.Append(Tile("Win rate", wins + lost > 0 ? (100.0 * wins / (wins + lost)).ToString("0", Fmt.C) + "%" : "-", ""));
                sb.Append(Tile("Average R", rs.Count > 0 ? Fmt.R(rs.Average()) : "-", rs.Count > 0 && rs.Average() >= 0 ? "pos" : "neg"));
                sb.Append(Tile("Profit factor", gl > 0 ? (gw / gl).ToString("0.00", Fmt.C) : "-", gl > 0 && gw / gl >= 1 ? "pos" : "neg"));
                double missed = t.Where(x => x.Follow == "tp_after").Sum(x => x.Missed) + cancels.Where(c => c.Follow == "tp").Sum(c => c.Result);
                sb.Append(Tile("Missed out", Fmt.M(missed), missed > 0 ? "neg" : ""));
                sb.Append("</div>");
                sb.Append("<div class=card><div class=sub>Balance curve: your results added up, trade by trade</div>" + Curve(t) + "</div>");
                sb.Append("<div class=card><div class=sub>Result per trade in R (green won, red lost)</div>" + RBars(t) + "</div>");
            }
            sb.Append(Md(md));
            sb.Append("</main></body></html>");
            return sb.ToString();
        }

        static string F(double v) => v.ToString("0.0", Fmt.C);

        static string Curve(List<TradeRow> t)
        {
            const double W = 920, Hh = 170, P = 60;
            var pts = new List<double> { 0 }; double acc = 0;
            foreach (var x in t) { acc += x.Net; pts.Add(acc); }
            double lo = Math.Min(0, pts.Min()), hi = Math.Max(0, pts.Max()); if (hi - lo < 1e-9) hi = lo + 1;
            Func<int, double> X = i => P + (W - P - 10) * i / Math.Max(1, pts.Count - 1);
            Func<double, double> Y = v => 10 + (Hh - 30) * (hi - v) / (hi - lo);
            var sb = new StringBuilder("<svg viewBox=\"0 0 920 170\" width=\"100%\" role=img aria-label=\"balance curve\">");
            sb.Append("<line x1=\"" + P + "\" x2=\"" + (W - 10) + "\" y1=\"" + F(Y(0)) + "\" y2=\"" + F(Y(0)) + "\" stroke=\"currentColor\" stroke-opacity=\".25\" stroke-dasharray=\"4\"/>");
            sb.Append("<polyline fill=\"none\" stroke-width=\"2.5\" stroke=\"" + (acc >= 0 ? "var(--pos)" : "var(--neg)") + "\" points=\"" +
                      string.Join(" ", pts.Select((v, i) => F(X(i)) + "," + F(Y(v)))) + "\"/>");
            sb.Append("<text x=\"4\" y=\"" + F(Y(hi) + 4) + "\">" + H(Fmt.SM(hi)) + "</text><text x=\"4\" y=\"" + F(Y(lo) + 4) + "\">" + H(Fmt.SM(lo)) + "</text>");
            if (lo < 0 && hi > 0) sb.Append("<text x=\"4\" y=\"" + F(Y(0) + 4) + "\">" + H(Fmt.M(0)) + "</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        static string RBars(List<TradeRow> t)
        {
            var r = t.Select(x => double.IsNaN(x.R) ? 0 : Math.Max(-3, Math.Min(3, x.R))).ToList();
            const double W = 920, Hh = 140, P = 40;
            double max = Math.Max(1, r.Select(Math.Abs).DefaultIfEmpty(1).Max());
            double bw = (W - P - 10) / Math.Max(1, r.Count), mid = Hh / 2;
            var sb = new StringBuilder("<svg viewBox=\"0 0 920 140\" width=\"100%\" role=img aria-label=\"R per trade\">");
            sb.Append("<line x1=\"" + P + "\" x2=\"" + (W - 10) + "\" y1=\"" + mid + "\" y2=\"" + mid + "\" stroke=\"currentColor\" stroke-opacity=\".25\"/>");
            for (int i = 0; i < r.Count; i++)
            {
                double h = Math.Abs(r[i]) / max * (mid - 12), x = P + i * bw + bw * 0.15;
                sb.Append("<rect x=\"" + F(x) + "\" width=\"" + F(Math.Min(40, bw * 0.7)) + "\" y=\"" + F(r[i] >= 0 ? mid - h : mid) +
                          "\" height=\"" + F(Math.Max(1, h)) + "\" rx=\"2\" fill=\"" + (r[i] >= 0 ? "var(--pos)" : "var(--neg)") + "\"><title>" + H(t[i].Symbol + " " + Fmt.R(r[i])) + "</title></rect>");
            }
            sb.Append("<text x=\"4\" y=\"16\">+" + max.ToString("0.#", Fmt.C) + "R</text><text x=\"4\" y=\"" + (mid + 4) + "\">0</text><text x=\"4\" y=\"134\">-" + max.ToString("0.#", Fmt.C) + "R</text></svg>");
            return sb.ToString();
        }

        // the small markdown subset Report.Build writes: headings, tables, numbered and bullet lists, paragraphs
        static string Md(string md)
        {
            var sb = new StringBuilder();
            var lines = md.Replace("\r", "").Split('\n');
            string section = ""; bool skip = false;
            var num = new System.Text.RegularExpressions.Regex(@"^\d+\. ");
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i];
                if (l.StartsWith("# ")) { skip = true; continue; }
                if (l.StartsWith("## ")) { section = l.Substring(3); skip = section == "Overview"; if (!skip) sb.Append("<h2>" + H(section) + "</h2>"); continue; }
                if (skip) continue;
                if (l.StartsWith("|"))
                {
                    var rows = new List<string>();
                    while (i < lines.Length && lines[i].StartsWith("|")) rows.Add(lines[i++]);
                    i--;
                    sb.Append("<div class=card><table>");
                    for (int r = 0; r < rows.Count; r++)
                    {
                        if (r == 1) continue;   // the |---| line
                        var cells = rows[r].Trim().Trim('|').Split('|');
                        string tag = r == 0 ? "th" : "td";
                        sb.Append("<tr>" + string.Concat(cells.Select(c => "<" + tag + ">" + Inline(c.Trim()) + "</" + tag + ">")) + "</tr>");
                    }
                    sb.Append("</table></div>");
                    continue;
                }
                if (num.IsMatch(l))
                {
                    sb.Append("<ol class=work>");
                    while (i < lines.Length && num.IsMatch(lines[i])) { sb.Append("<li>" + Inline(num.Replace(lines[i], "")) + "</li>"); i++; }
                    i--; sb.Append("</ol>"); continue;
                }
                if (l.StartsWith("- "))
                {
                    sb.Append("<ul class=\"" + (section == "What went well" ? "good" : "") + "\">");
                    while (i < lines.Length && lines[i].StartsWith("- ")) { sb.Append("<li>" + Inline(lines[i].Substring(2)) + "</li>"); i++; }
                    i--; sb.Append("</ul>"); continue;
                }
                if (l.Trim() != "") sb.Append("<p>" + Inline(l) + "</p>");
            }
            return sb.ToString();
        }
    }
}

namespace cAlgo.Robots
{
    public enum PanelCorner { Top_left, Top_right, Bottom_left, Bottom_right }

    [Robot(TimeZone = TimeZones.WEuropeStandardTime, AccessRights = AccessRights.FullAccess)]
    public class TradeWatch : Robot
    {
        public const string Version = "1.3.0";

        [Parameter("Risk target per trade (% of balance)", Group = "Plan", DefaultValue = 3.0, MinValue = 0.1, MaxValue = 10, Step = 0.1)]
        public double RiskTarget { get; set; }

        [Parameter("Daily stop rule", Group = "Daily stop", DefaultValue = DayRule.Day_down_in_R)]
        public DayRule StopRule { get; set; }

        [Parameter("Day down in R: stop when the day is down this many risks", Group = "Daily stop", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10, Step = 0.5)]
        public double DayLimitR { get; set; }

        [Parameter("Count losses: stop after (0 = auto: 2 above 1% risk, else 3)", Group = "Daily stop", DefaultValue = 0, MinValue = 0, MaxValue = 20)]
        public int DailyStopOverride { get; set; }

        [Parameter("Trading day starts at (hour, Amsterdam time)", Group = "Daily stop", DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int DayStartHour { get; set; }

        [Parameter("Scratch: a loss smaller than this % of your risk per trade doesn't count", Group = "Daily stop", DefaultValue = 20, MinValue = 0, MaxValue = 100)]
        public double ScratchPct { get; set; }

        [Parameter("Sound on a new warning", Group = "Alerts", DefaultValue = true)]
        public bool Sound { get; set; }

        [Parameter("Popup on a new warning", Group = "Alerts", DefaultValue = true)]
        public bool Popup { get; set; }

        [Parameter("Warn before rollover (minutes)", Group = "Alerts", DefaultValue = 60, MinValue = 0, MaxValue = 600)]
        public int RolloverWarnMin { get; set; }

        [Parameter("Keep journal and report", Group = "Journal", DefaultValue = true)]
        public bool Journal { get; set; }

        [Parameter("Journal folder (empty = Documents/TradeWatch)", Group = "Journal", DefaultValue = "")]
        public string JournalFolder { get; set; }

        [Parameter("Panel corner", Group = "Panel", DefaultValue = PanelCorner.Top_left)]
        public PanelCorner Corner { get; set; }

        [Parameter("Font size", Group = "Panel", DefaultValue = 12, MinValue = 8, MaxValue = 24)]
        public int FontSize { get; set; }

        // panel
        Border box; StackPanel body, head, headStatus; Button toggle; bool collapsed; string lastSig = "";
        static readonly Color CText = Color.FromHex("#FFE6E9EF"), CDim = Color.FromHex("#FF9AA4B2"), COk = Color.FromHex("#FF4CC38A"),
                              CWarn = Color.FromHex("#FFF2B84B"), CBad = Color.FromHex("#FFFF6B6B"), CInfo = Color.FromHex("#FF7FB3FF");

        // state
        readonly HashSet<string> alerted = new HashSet<string>();
        readonly Dictionary<string, DateTime> firstSeen = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> justClosed = new Dictionary<string, DateTime>();
        Dictionary<string, Campaign> campaigns = new Dictionary<string, Campaign>();
        readonly Dictionary<string, DateTime> finishWait = new Dictionary<string, DateTime>();
        List<TradeRow> journal = new List<TradeRow>();
        List<CancelRow> cancelled = new List<CancelRow>();
        string dir; bool fileOk; string fileErr = "";
        List<TradeGroup> today = new List<TradeGroup>(); DateTime todayFor = DateTime.MinValue; bool historyDirty = true;
        DateTime lastFollow = DateTime.MinValue; string lastDayList = null;
        List<Setup> setups = new List<Setup>();

        protected override void OnStart()
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            string ccy = Account.Asset != null ? Account.Asset.Name : "EUR";
            Fmt.Ccy = ccy == "EUR" ? "€" : ccy == "USD" ? "$" : ccy == "GBP" ? "£" : ccy + " ";

            BuildPanel();
            if (Journal) LoadFiles();

            Positions.Opened += _ => Refresh();
            Positions.Closed += a => { historyDirty = true; justClosed[a.Position.SymbolName + "|" + (a.Position.TradeType == TradeType.Buy ? "BUY" : "SELL")] = Server.Time; Refresh(); };
            Positions.Modified += _ => Refresh();
            PendingOrders.Created += _ => Refresh();
            PendingOrders.Modified += _ => Refresh();
            PendingOrders.Filled += _ => Refresh();
            PendingOrders.Cancelled += OnCancelled;
            Timer.Start(1);
            Print("Trade Watch " + Version + " started. Read-only: it never changes your orders." + (Journal ? (fileOk ? " Journal: " + dir : " Journal off: " + fileErr) : ""));
            Refresh();
        }

        protected override void OnTimer() => Refresh();

        protected override void OnStop() { if (Journal && fileOk) SaveState(); }

        // ------------------------------------------------------------ data
        double CommPerUnitRT(string symbol)
        {
            for (int i = History.Count - 1, n = 0; i >= 0 && n < 500; i--, n++)
            {
                var h = History[i];
                if (h.SymbolName == symbol && h.VolumeInUnits > 0 && h.Commissions != 0) return Math.Abs(h.Commissions) / h.VolumeInUnits;
            }
            return double.NaN;
        }

        static double SwapNight(Symbol s, bool buy, double units, double price)
        {
            double rate = buy ? s.SwapLong : s.SwapShort;
            switch (s.SwapCalculationType)
            {
                case SymbolSwapCalculationType.Percentage:
                    double notional = units * price * s.PipValue / s.PipSize;
                    return notional * rate / 100.0 / 360.0;
                case SymbolSwapCalculationType.Points:
                    return rate * Math.Pow(10, -s.Digits) / s.PipSize * s.PipValue * units;
                default:
                    return rate * s.PipValue * units;
            }
        }

        List<WItem> Collect()
        {
            var list = new List<WItem>();
            var commCache = new Dictionary<string, double>();
            double Comm(string sym) { if (!commCache.TryGetValue(sym, out var v)) { v = CommPerUnitRT(sym); commCache[sym] = v; } return v; }

            foreach (var p in Positions)
            {
                var s = p.Symbol; bool buy = p.TradeType == TradeType.Buy;
                list.Add(new WItem
                {
                    Symbol = p.SymbolName, Buy = buy, Filled = true, Id = p.Id, Units = p.VolumeInUnits, Lots = s.VolumeInUnitsToQuantity(p.VolumeInUnits),
                    Entry = p.EntryPrice, Sl = p.StopLoss, Tp = p.TakeProfit, PipSize = s.PipSize, PipValue = s.PipValue, Digits = s.Digits,
                    StepLots = s.VolumeInUnitsToQuantity(s.VolumeInUnitsStep), CommRT = 2 * Math.Abs(p.Commissions), CommKnown = true,
                    NetProfit = p.NetProfit, SwapPerNight = SwapNight(s, buy, p.VolumeInUnits, p.EntryPrice), SwapMultTonight = TripleTonight(s) ? 3 : 1, TripleDay = TripleDayName(s), SpreadPips = s.Spread / s.PipSize, OrderType = "filled", Current = buy ? s.Bid : s.Ask
                });
            }
            foreach (var o in PendingOrders)
            {
                var s = o.Symbol; bool buy = o.TradeType == TradeType.Buy; double c = Comm(o.SymbolName);
                list.Add(new WItem
                {
                    Symbol = o.SymbolName, Buy = buy, Filled = false, Id = o.Id, Units = o.VolumeInUnits, Lots = s.VolumeInUnitsToQuantity(o.VolumeInUnits),
                    Entry = o.TargetPrice, Sl = o.StopLoss, Tp = o.TakeProfit, PipSize = s.PipSize, PipValue = s.PipValue, Digits = s.Digits,
                    StepLots = s.VolumeInUnitsToQuantity(s.VolumeInUnitsStep), CommRT = double.IsNaN(c) ? 0 : c * o.VolumeInUnits, CommKnown = !double.IsNaN(c),
                    SpreadPips = s.Spread / s.PipSize, IsLimit = o.OrderType == PendingOrderType.Limit, Current = buy ? s.Bid : s.Ask,
                    OrderType = o.OrderType == PendingOrderType.Limit ? "limit" : o.OrderType == PendingOrderType.Stop ? "stop" : "stop-limit"
                });
            }
            return list;
        }

        void UpdateToday()
        {
            if (!historyDirty && todayFor == TradingDay(Server.Time)) return;
            todayFor = TradingDay(Server.Time); historyDirty = false;
            var closed = new List<ClosedPos>();
            for (int i = History.Count - 1, n = 0; i >= 0 && n < 1000; i--, n++)
            {
                var h = History[i];
                if (TradingDay(h.ClosingTime) != todayFor) continue;
                closed.Add(new ClosedPos { PositionId = h.PositionId, Symbol = h.SymbolName, Buy = h.TradeType == TradeType.Buy, Open = h.EntryTime, Close = h.ClosingTime, Net = h.NetProfit });
            }
            // a position closed in parts is one position
            var merged = closed.GroupBy(c => c.PositionId).Select(g => new ClosedPos
            { PositionId = g.Key, Symbol = g.First().Symbol, Buy = g.First().Buy, Open = g.Min(x => x.Open), Close = g.Max(x => x.Close), Net = g.Sum(x => x.Net) });
            today = Day.Group(merged);
            string list = string.Join(" | ", DayNow().Items.Select(i => i.Text));
            if (list != lastDayList) { lastDayList = list; Print("Today's trades: " + (list == "" ? "none" : list)); }
        }

        // ------------------------------------------------------------ main loop
        void Refresh()
        {
            try
            {
                UpdateToday();
                foreach (var k in justClosed.Where(kv => (Server.Time - kv.Value).TotalMinutes > 30).Select(kv => kv.Key).ToList()) justClosed.Remove(k);

                var items = Collect();
                setups = Analyzer.Build(items, Account.Balance, RiskTarget, justClosed.Keys.ToList());
                var global = GlobalNotes(items);

                Alert(setups.SelectMany(s => s.Notes).Concat(global).ToList());
                if (Journal) TrackCampaigns(global);
                Draw(global);
                if (Journal && fileOk && (Server.Time - lastFollow).TotalMinutes >= 5) { lastFollow = Server.Time; RunFollowUps(); }
            }
            catch (Exception e) { Print("Trade Watch error: " + e.Message); }
        }

        double ScratchMoney => Day.ScratchMoney(Account.Balance, RiskTarget, ScratchPct);
        DateTime TradingDay(DateTime t) => t.AddHours(-DayStartHour).Date;
        DayState DayNow()
        {
            double dayStartBalance = Account.Balance - today.Sum(t => t.Net);   // one risk measured on the balance the day started with
            return DayRules.Evaluate(today, StopRule, dayStartBalance * RiskTarget / 100, DayLimitR,
                                     Day.MaxLosses(RiskTarget, DailyStopOverride), ScratchMoney);
        }

        List<Note> GlobalNotes(List<WItem> items)
        {
            var g = new List<Note>();
            var day = DayNow();
            string d = todayFor.ToString("yyyyMMdd", Fmt.C);
            if (day.Stopped)
            {
                g.Add(new Note(Lvl.Bad, "daily_stop", "day|stop|" + d, day.StopText));
                if (items.Any(i => !i.Filled))
                    g.Add(new Note(Lvl.Bad, "daily_stop_orders", "day|orders|" + d + "|" + items.Count(i => !i.Filled), "Daily stop reached but orders are still pending: cancel them."));
            }
            else if (day.OneLeft)
                g.Add(new Note(Lvl.Warn, "daily_last", "day|last|" + d, day.LastText));

            // rollover
            var filled = items.Where(i => i.Filled).ToList();
            if (filled.Count > 0 && RolloverWarnMin > 0)
            {
                var (mins, _) = NextRollover();
                if (mins >= 0 && mins <= RolloverWarnMin)
                {
                    double swap = filled.Sum(i => i.SwapPerNight * i.SwapMultTonight);
                    bool triple = filled.Any(i => i.SwapMultTonight == 3);
                    g.Add(new Note(swap < 0 ? Lvl.Warn : Lvl.Info, "rollover", "day|roll|" + d,
                        "Rollover in " + mins.ToString("0", Fmt.C) + " min: swap about " + Fmt.SM(swap) + (triple ? " (triple swap tonight: the weekend)" : "") + " on what is open."));
                }
            }
            return g;
        }

        /// Minutes to the next rollover (17:00 New York time, weekdays) and the New York weekday it falls on.
        (double, DayOfWeek) NextRollover()
        {
            TimeZoneInfo ny = null;
            foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
                try { ny = TimeZoneInfo.FindSystemTimeZoneById(id); break; } catch { }
            DateTime utc = Server.TimeInUtc;
            DateTime local = ny == null ? utc.AddHours(-5) : TimeZoneInfo.ConvertTimeFromUtc(utc, ny);
            var roll = local.Date.AddHours(17); if (roll <= local) roll = roll.AddDays(1);
            while (roll.DayOfWeek == DayOfWeek.Saturday || roll.DayOfWeek == DayOfWeek.Sunday) roll = roll.AddDays(1);
            return ((roll - local).TotalMinutes, roll.DayOfWeek);
        }

        bool TripleTonight(Symbol s)
        {
            try { var d3 = s.Swap3DaysRollover; return d3.HasValue && d3.Value == NextRollover().Item2; } catch { return false; }
        }

        static string TripleDayName(Symbol s)
        {
            try { var d3 = s.Swap3DaysRollover; return d3.HasValue ? d3.Value.ToString().Substring(0, 3) : ""; } catch { return ""; }
        }

        void Alert(List<Note> notes)
        {
            var live = new HashSet<string>(notes.Where(n => n.Level >= Lvl.Warn).Select(n => n.Key));
            foreach (var k in live) if (!firstSeen.ContainsKey(k)) firstSeen[k] = Server.Time;
            foreach (var k in firstSeen.Keys.Where(k => !live.Contains(k)).ToList()) firstSeen.Remove(k);
            foreach (var n in notes.Where(n => n.Level >= Lvl.Warn && !alerted.Contains(n.Key)))
            {
                Print((n.Level == Lvl.Bad ? "[ACTION] " : "[CHECK] ") + n.Text);
                if (Popup) try { Notifications.ShowPopup("Trade Watch", n.Text, n.Level == Lvl.Bad ? PopupNotificationState.Error : PopupNotificationState.Information); } catch { }
                if (Sound) try { Notifications.PlaySound(n.Level == Lvl.Bad ? SoundType.NegativeNotification : SoundType.Announcement); } catch { }
            }
            alerted.RemoveWhere(k => !live.Contains(k));   // a warning that clears and comes back alerts again
            alerted.UnionWith(live);
        }

        // ------------------------------------------------------------ journal
        void TrackCampaigns(List<Note> global)
        {
            bool dayStopped = DayNow().Stopped;
            var withFills = setups.Where(s => s.Items.Any(i => i.Filled)).ToDictionary(s => s.Key);

            foreach (var s in withFills.Values)
            {
                if (!campaigns.TryGetValue(s.Key, out var c))
                {
                    var first = s.Items[0];
                    var l1 = s.Levels[0];
                    c = new Campaign
                    {
                        Symbol = s.Symbol, Buy = s.Buy, Start = Server.Time, Kind = TradeBuilder.Kind(s), PlannedOrders = s.Items.Count,
                        PlanRisk = s.FullRisk ?? double.NaN, PlanRiskPct = s.FullRisk.HasValue ? s.RiskPct : double.NaN, PlanReward = s.FullReward ?? double.NaN,
                        PlanSl = l1.FirstOrDefault(i => i.Sl.HasValue)?.Sl ?? double.NaN, PlanTp = l1.FirstOrDefault(i => i.Tp.HasValue)?.Tp ?? double.NaN,
                        PipSize = first.PipSize, PipValue = first.PipValue, Digits = first.Digits, AfterDailyStop = dayStopped
                    };
                    if (dayStopped) c.Breaks.Add("after_stop");
                    campaigns[s.Key] = c;
                    finishWait.Remove(s.Key);
                    SaveState();
                }
                if (double.IsNaN(c.PlanRisk) && s.FullRisk.HasValue) { c.PlanRisk = s.FullRisk.Value; c.PlanRiskPct = s.RiskPct; }
                if (double.IsNaN(c.PlanSl) && s.Levels[0].Any(i => i.Sl.HasValue)) c.PlanSl = s.Levels[0].First(i => i.Sl.HasValue).Sl.Value;
                if (double.IsNaN(c.PlanTp) && s.Levels[0].Any(i => i.Tp.HasValue)) c.PlanTp = s.Levels[0].First(i => i.Tp.HasValue).Tp.Value;
                if (s.ScaleIn && c.Kind != "scale-in") c.Kind = "scale-in";
                foreach (var it in s.Items.Where(i => i.Filled))
                {
                    if (!c.PosIds.Contains(it.Id)) c.PosIds.Add(it.Id);
                    string id = it.Id.ToString(Fmt.C);
                    if (it.Sl.HasValue) c.LastSl[id] = it.Sl.Value; else c.LastSl.Remove(id);
                    if (it.Tp.HasValue) c.LastTp[id] = it.Tp.Value; else c.LastTp.Remove(id);
                }
                // a rule counts as broken only when it stays broken for a minute (time to move the TP after entry 2 fills)
                foreach (var n in s.Notes.Where(n => n.Level >= Lvl.Warn && n.Code != "both_dirs" && n.Code != "orphan"))
                    if (firstSeen.TryGetValue(n.Key, out var since) && (Server.Time - since).TotalSeconds >= 60) c.Breaks.Add(n.Code);
                if (s.Notes.Any(n => n.Code == "both_dirs")) c.Breaks.Add("both_dirs");

                // best and worst price while open
                var sym = Symbols.GetSymbol(s.Symbol);
                double px = s.Buy ? sym.Bid : sym.Ask;
                if (double.IsNaN(c.Best) || (px - c.Best) * (s.Buy ? 1 : -1) > 0) c.Best = px;
                if (double.IsNaN(c.Worst) || (px - c.Worst) * (s.Buy ? 1 : -1) < 0) c.Worst = px;
            }

            // finished: no filled positions left for that symbol + direction
            foreach (var key in campaigns.Keys.Where(k => !withFills.ContainsKey(k)).ToList())
            {
                var c = campaigns[key];
                var parts = ClosedParts(c.PosIds);
                bool complete = parts.Count == c.PosIds.Count;
                if (!finishWait.ContainsKey(key)) finishWait[key] = Server.Time;
                if (!complete && (Server.Time - finishWait[key]).TotalSeconds < 30) continue;   // history can arrive a moment later
                finishWait.Remove(key);
                campaigns.Remove(key);
                if (parts.Count == 0) { SaveState(); continue; }
                var row = TradeBuilder.Finish(c, parts);
                journal.Add(row);
                Print("Journal: " + row.Symbol + " " + row.Dir.ToLowerInvariant() + " " + row.Kind + " closed " + Fmt.SM(row.Net) +
                      (double.IsNaN(row.R) ? "" : " (" + Fmt.R(row.R) + ")") + ", exit " + row.ExitType + ".");
                SaveAll();
            }
        }

        List<ClosedPos> ClosedParts(List<long> ids)
        {
            var want = new HashSet<long>(ids);
            var found = new List<HistoricalTrade>();
            for (int i = History.Count - 1, n = 0; i >= 0 && n < 2000; i--, n++)
                if (want.Contains(History[i].PositionId)) found.Add(History[i]);
            return found.GroupBy(h => (long)h.PositionId).Select(g =>
            {
                double units = g.Sum(h => h.VolumeInUnits);
                return new ClosedPos
                {
                    PositionId = g.Key, Symbol = g.First().SymbolName, Buy = g.First().TradeType == TradeType.Buy,
                    Open = g.Min(h => h.EntryTime), Close = g.Max(h => h.ClosingTime), Net = g.Sum(h => h.NetProfit), Gross = g.Sum(h => h.GrossProfit),
                    Comm = g.Sum(h => h.Commissions), Swap = g.Sum(h => h.Swap), Units = units, Lots = g.First().Symbol.VolumeInUnitsToQuantity(units),
                    EntryPrice = g.First().EntryPrice, ClosePrice = units > 0 ? g.Sum(h => h.ClosingPrice * h.VolumeInUnits) / units : g.First().ClosingPrice
                };
            }).ToList();
        }

        void OnCancelled(PendingOrderCancelledEventArgs a)
        {
            Refresh();
            if (!Journal || a.Reason == PendingOrderCancellationReason.Rejected) return;
            var o = a.PendingOrder; var s = o.Symbol;
            string key = o.SymbolName + "|" + (o.TradeType == TradeType.Buy ? "BUY" : "SELL");
            cancelled.Add(new CancelRow
            {
                Id = o.Id.ToString(Fmt.C), Symbol = o.SymbolName, Dir = o.TradeType == TradeType.Buy ? "BUY" : "SELL", Time = Server.Time,
                Reason = a.Reason == PendingOrderCancellationReason.Expired ? "expired" : "cancelled",
                Entry = o.TargetPrice, Sl = o.StopLoss ?? double.NaN, Tp = o.TakeProfit ?? double.NaN, Units = o.VolumeInUnits,
                PipSize = s.PipSize, PipValue = s.PipValue, Digits = s.Digits,
                Leftover = justClosed.ContainsKey(key) || campaigns.ContainsKey(key)
            });
            SaveAll();
        }

        IEnumerable<PBar> BarsAfter(string symbol, DateTime from, DateTime to)
        {
            var bars = MarketData.GetBars(TimeFrame.Minute, symbol);
            int guard = 0;
            while (bars.Count > 0 && bars.OpenTimes[0] > from && guard++ < 5) { if (bars.LoadMoreHistory() == 0) break; }
            var list = new List<PBar>();
            for (int i = 0; i < bars.Count; i++)
            {
                var t = bars.OpenTimes[i];
                if (t < from.AddMinutes(-1) || t > to) continue;
                list.Add(new PBar(t, bars.HighPrices[i], bars.LowPrices[i]));
            }
            return list.Where(b => b.T >= from.AddMinutes(1)).ToList();   // from the first full bar after the event
        }

        void RunFollowUps()
        {
            bool changed = false;
            foreach (var t in journal.Where(x => x.Follow == "" && (Server.Time - x.Close).TotalDays <= 3))
            {
                bool over = (Server.Time - t.Close).TotalHours >= 24;
                try { Follow.Apply(t, BarsAfter(t.Symbol, t.Close, t.Close.AddHours(24)), over); } catch { continue; }
                if (t.Follow != "") changed = true;
            }
            foreach (var t in journal.Where(x => x.Follow == "" && (Server.Time - x.Close).TotalDays > 3)) { t.Follow = "none"; t.FollowText = "Not checked (bot was off)."; changed = true; }
            foreach (var c in cancelled.Where(x => x.Follow == "" && (Server.Time - x.Time).TotalDays <= 3))
            {
                bool over = (Server.Time - c.Time).TotalHours >= 24;
                try { Follow.Apply(c, BarsAfter(c.Symbol, c.Time, c.Time.AddHours(24)), over); } catch { continue; }
                if (c.Follow != "") changed = true;
            }
            foreach (var c in cancelled.Where(x => x.Follow == "" && (Server.Time - x.Time).TotalDays > 3)) { c.Follow = "n/a"; c.FollowText = "Not checked (bot was off)."; changed = true; }
            if (changed) SaveAll();
        }

        void LoadFiles()
        {
            try
            {
                // on a Mac, .NET's "MyDocuments" can be the home folder, so look for ~/Documents first
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string docs = Directory.Exists(Path.Combine(home, "Documents")) ? Path.Combine(home, "Documents")
                            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                dir = string.IsNullOrWhiteSpace(JournalFolder) ? Path.Combine(docs, "TradeWatch") : JournalFolder;
                Directory.CreateDirectory(dir);
                string j = Path.Combine(dir, "journal.csv"), c = Path.Combine(dir, "cancelled.csv"), st = Path.Combine(dir, "state.json");
                if (System.IO.File.Exists(j)) journal = Csv.Parse(System.IO.File.ReadAllText(j)).Skip(1).Where(r => r.Length >= TradeRow.Cols.Length).Select(TradeRow.FromCsv).ToList();
                if (System.IO.File.Exists(c)) cancelled = Csv.Parse(System.IO.File.ReadAllText(c)).Skip(1).Where(r => r.Length >= CancelRow.Cols.Length).Select(CancelRow.FromCsv).ToList();
                if (System.IO.File.Exists(st))
                    campaigns = JsonSerializer.Deserialize<Dictionary<string, Campaign>>(System.IO.File.ReadAllText(st), Json.Opt) ?? new Dictionary<string, Campaign>();
                fileOk = true;
                SaveAll();
            }
            catch (Exception e) { fileOk = false; fileErr = e.Message; Print("Trade Watch: journal is off, files can't be written (" + e.Message + "). Allow file access, or set a Journal folder."); }
        }

        void SaveState()
        {
            if (!fileOk) return;
            try { System.IO.File.WriteAllText(Path.Combine(dir, "state.json"), JsonSerializer.Serialize(campaigns, Json.Opt)); } catch (Exception e) { Print("Trade Watch: can't save state: " + e.Message); }
        }

        void SaveAll()
        {
            if (!fileOk) return;
            try
            {
                System.IO.File.WriteAllText(Path.Combine(dir, "journal.csv"), string.Join(",", TradeRow.Cols) + "\n" + string.Concat(journal.Select(r => r.ToCsv() + "\n")));
                System.IO.File.WriteAllText(Path.Combine(dir, "cancelled.csv"), string.Join(",", CancelRow.Cols) + "\n" + string.Concat(cancelled.Select(r => r.ToCsv() + "\n")));
                System.IO.File.WriteAllText(Path.Combine(dir, "report.md"), Report.Build(journal, cancelled, RiskTarget, Server.Time, ScratchPct / 100));
                System.IO.File.WriteAllText(Path.Combine(dir, "report.html"), ReportHtml.Build(journal, cancelled, RiskTarget, Server.Time, ScratchPct / 100));
                SaveState();
            }
            catch (Exception e) { Print("Trade Watch: can't write the journal: " + e.Message); }
        }

        // ------------------------------------------------------------ panel
        const double CardW = 430;   // panel content width in pixels

        void BuildPanel()
        {
            body = new StackPanel { Orientation = Orientation.Vertical };
            toggle = new Button { Text = "–", Width = 26, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 0),
                                  BackgroundColor = Color.FromHex("#33FFFFFF"), ForegroundColor = CText, CornerRadius = new CornerRadius(4) };
            toggle.Click += _ => { collapsed = !collapsed; toggle.Text = collapsed ? "+" : "–"; lastSig = ""; Refresh(); };
            head = new StackPanel { Orientation = Orientation.Horizontal };
            head.AddChild(toggle);
            head.AddChild(new TextBlock { Text = "TRADE WATCH", ForegroundColor = CText, FontSize = FontSize + 2, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
            head.AddChild(new TextBlock { Text = "  v" + Version, ForegroundColor = CDim, FontSize = FontSize - 1, VerticalAlignment = VerticalAlignment.Center });
            headStatus = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            head.AddChild(headStatus);
            var outer = new StackPanel { Orientation = Orientation.Vertical };
            outer.AddChild(head); outer.AddChild(body);
            var scroll = new ScrollViewer { Content = outer, MaxHeight = 900, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            box = new Border
            {
                Child = scroll, BackgroundColor = Color.FromHex("#F0121620"), BorderColor = Color.FromHex("#FF2E3542"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 10), Margin = new Thickness(8),
                HorizontalAlignment = Corner == PanelCorner.Top_left || Corner == PanelCorner.Bottom_left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = Corner == PanelCorner.Top_left || Corner == PanelCorner.Top_right ? VerticalAlignment.Top : VerticalAlignment.Bottom
            };
            Chart.AddControl(box);
        }

        static Color LvlColor(Lvl l) => l == Lvl.Bad ? CBad : l == Lvl.Warn ? CWarn : l == Lvl.Info ? CInfo : COk;
        static Color LvlTint(Lvl l) => Color.FromHex(l == Lvl.Bad ? "#FF3B1F25" : l == Lvl.Warn ? "#FF3A3120" : l == Lvl.Info ? "#FF1C2A3D" : "#FF17322A");
        static string LvlMark(Lvl l) => l == Lvl.Bad ? "✗" : l == Lvl.Warn ? "!" : l == Lvl.Info ? "i" : "✓";

        TextBlock Txt(string t, Color c, double size, bool bold = false, double top = 0) =>
            new TextBlock { Text = t, ForegroundColor = c, FontSize = size, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                            Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };

        Border Chip(string t, Color bg, Color fg, double size) =>
            new Border { BackgroundColor = bg, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 6, 0),
                         VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = t, ForegroundColor = fg, FontSize = size, FontWeight = FontWeight.Bold } };

        Border Pill(Note n)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BackgroundColor = LvlColor(n.Level), Margin = new Thickness(0, 0, 8, 0),
                                   VerticalAlignment = VerticalAlignment.Top,
                                   Child = new TextBlock { Text = LvlMark(n.Level), ForegroundColor = Color.FromHex("#FF101318"), FontSize = FontSize - 1, FontWeight = FontWeight.Bold,
                                                           HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            row.AddChild(dot);
            row.AddChild(new TextBlock { Text = n.Text, ForegroundColor = n.Level == Lvl.Ok ? CText : LvlColor(n.Level), FontSize = FontSize, TextWrapping = TextWrapping.Wrap,
                                         Width = CardW - 50, FontWeight = n.Level == Lvl.Bad ? FontWeight.Bold : FontWeight.Normal });
            return new Border { BackgroundColor = LvlTint(n.Level), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0), Child = row };
        }

        StackPanel Stat(string caption, string value, string sub, Color c)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 8, 0) };
            p.AddChild(Txt(caption, CDim, FontSize - 2, true));
            p.AddChild(Txt(value, c, FontSize + 5, true));
            if (!string.IsNullOrEmpty(sub)) p.AddChild(Txt(sub, CDim, FontSize - 1));
            return p;
        }

        Canvas Meter(double fill, double target, Color c)
        {
            double w = CardW - 24;
            var cv = new Canvas { Width = w, Height = 14, Margin = new Thickness(0, 6, 0, 0) };
            cv.AddChild(new Rectangle { Width = w, Height = 6, Top = 4, Left = 0, FillColor = Color.FromHex("#FF2A303B"), RadiusX = 3, RadiusY = 3 });
            cv.AddChild(new Rectangle { Width = Math.Max(2, w * fill), Height = 6, Top = 4, Left = 0, FillColor = c, RadiusX = 3, RadiusY = 3 });
            cv.AddChild(new Rectangle { Width = 2, Height = 14, Top = 0, Left = w * target - 1, FillColor = CText });
            return cv;
        }

        Canvas Ladder(PCard c)
        {
            double w = CardW - 24, x0 = 10, span = w - 20;
            double X(double p) => x0 + span * Math.Max(0, Math.Min(1, p));
            var cv = new Canvas { Width = w, Height = 44, Margin = new Thickness(0, 8, 0, 0) };
            cv.AddChild(new Rectangle { Width = span, Height = 4, Top = 19, Left = x0, FillColor = Color.FromHex("#FF2A303B") });
            if (!double.IsNaN(c.ZoneSl) && c.ZoneEntry > c.ZoneSl)
                cv.AddChild(new Rectangle { Width = X(c.ZoneEntry) - X(c.ZoneSl), Height = 8, Top = 17, Left = X(c.ZoneSl), FillColor = Color.FromHex("#AAFF6B6B") });
            if (!double.IsNaN(c.ZoneTp) && c.ZoneTp > c.ZoneEntry)
                cv.AddChild(new Rectangle { Width = X(c.ZoneTp) - X(c.ZoneEntry), Height = 8, Top = 17, Left = X(c.ZoneEntry), FillColor = Color.FromHex("#AA4CC38A") });
            foreach (var m in c.Marks)
            {
                double x = X(m.Pos);
                bool top = m.Kind == "fill" || m.Kind == "pend" || m.Kind == "now";
                Color col = m.Kind == "sl" ? CBad : m.Kind == "tp" ? COk : m.Kind == "now" ? CInfo : CText;
                if (m.Kind == "now")
                    cv.AddChild(new Ellipse { Width = 10, Height = 10, Left = x - 5, Top = 16, FillColor = CInfo, StrokeColor = Color.FromHex("#FF121620"), StrokeThickness = 2 });
                else if (m.Kind == "pend")
                    cv.AddChild(new Ellipse { Width = 10, Height = 10, Left = x - 5, Top = 16, FillColor = Color.FromHex("#FF121620"), StrokeColor = CText, StrokeThickness = 2 });
                else if (m.Kind == "fill")
                    cv.AddChild(new Ellipse { Width = 10, Height = 10, Left = x - 5, Top = 16, FillColor = CText });
                else
                    cv.AddChild(new Rectangle { Width = 3, Height = 16, Left = x - 1.5, Top = 13, FillColor = col });
                cv.AddChild(new TextBlock { Text = m.Label, ForegroundColor = col, FontSize = FontSize - 3, FontWeight = FontWeight.Bold,
                                            Left = Math.Max(0, Math.Min(w - 26, x - 9)), Top = top ? 0 : 30 });
            }
            return cv;
        }

        Grid Rows(PCard c)
        {
            var g = new Grid(c.Rows.Count + 1, 7) { Margin = new Thickness(0, 6, 0, 0) };
            string[] hd = { "", "LOTS", "ENTRY", "", "SL", "TP", "P/L" };
            for (int i = 0; i < hd.Length; i++) g.AddChild(Txt(hd[i], CDim, FontSize - 3, true), 0, i);
            for (int r = 0; r < c.Rows.Count; r++)
            {
                var x = c.Rows[r];
                g.AddChild(Txt(x.Name, CText, FontSize - 1, true), r + 1, 0);
                g.AddChild(Txt(x.Lots, CText, FontSize - 1), r + 1, 1);
                g.AddChild(Txt(x.Entry, CText, FontSize - 1), r + 1, 2);
                g.AddChild(Txt(x.Status, x.Filled ? CInfo : CDim, FontSize - 2), r + 1, 3);
                g.AddChild(Txt(x.Sl, x.Sl == "none" ? CBad : Color.FromHex("#FFFF9B9B"), FontSize - 1), r + 1, 4);
                g.AddChild(Txt(x.Tp, x.Tp == "none" ? CWarn : Color.FromHex("#FF8FE0B5"), FontSize - 1), r + 1, 5);
                g.AddChild(Txt(x.Pnl, x.PnlVal >= 0 ? COk : CBad, FontSize - 1, true), r + 1, 6);
            }
            return g;
        }

        Border CardView(PCard c)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical };
            var hd = new StackPanel { Orientation = Orientation.Horizontal };
            hd.AddChild(new TextBlock { Text = c.Symbol, ForegroundColor = CText, FontSize = FontSize + 3, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            hd.AddChild(Chip(c.Buy ? "BUY" : "SELL", c.Buy ? Color.FromHex("#FF1F8A5B") : Color.FromHex("#FFC2414B"), Color.White, FontSize - 1));
            hd.AddChild(Chip(c.Kind.ToUpperInvariant(), Color.FromHex("#FF2A303B"), CText, FontSize - 2));
            hd.AddChild(new TextBlock { Text = c.Stage, ForegroundColor = CDim, FontSize = FontSize - 1, VerticalAlignment = VerticalAlignment.Center });
            p.AddChild(hd);

            var stats = new Grid(1, 3) { Margin = new Thickness(0, 8, 0, 0) };
            stats.AddChild(Stat("RISK", c.Risk, c.RiskSub, c.RiskLvl == Lvl.Bad ? CBad : CText), 0, 0);
            stats.AddChild(Stat("AT TP", c.Reward, c.RewardSub, c.HasReward ? COk : CWarn), 0, 1);
            stats.AddChild(Stat("NOW", c.Now, c.NowSub, !c.HasNow ? CDim : c.NowVal >= 0 ? COk : CBad), 0, 2);
            p.AddChild(stats);
            p.AddChild(Meter(c.MeterFill, c.MeterTarget, c.RiskLvl == Lvl.Bad ? CBad : c.MeterFill > c.MeterTarget * 0.5 ? COk : CInfo));
            p.AddChild(Txt("risk if all fill and stop  ·  white line = your target", CDim, FontSize - 3));
            p.AddChild(Ladder(c));
            p.AddChild(Rows(c));
            p.AddChild(Txt(c.Costs, CDim, FontSize - 2, false, 6));
            foreach (var n in c.Notes) p.AddChild(Pill(n));
            return new Border { Child = p, Width = CardW, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 8, 10, 10), CornerRadius = new CornerRadius(6),
                                BackgroundColor = Color.FromHex("#FF1A1F29"), BorderColor = LvlColor(c.Status), BorderThickness = new Thickness(4, 1, 1, 1) };
        }

        void Draw(List<Note> global)
        {
            string footer = ""; Lvl fl = Lvl.Info;
            if (Journal)
            {
                if (!fileOk) { footer = "Journal off: " + fileErr; fl = Lvl.Warn; }
                else if (journal.Count == 0) footer = "Journal: no finished trades yet  ·  " + dir;
                else
                {
                    int w = journal.Count(t => t.Net > 0);
                    var rs = journal.Where(t => !double.IsNaN(t.R)).Select(t => t.R).ToList();
                    footer = "Journal: " + journal.Count + " trades  ·  " + (100.0 * w / journal.Count).ToString("0", Fmt.C) + "% won  ·  avg " +
                             (rs.Count > 0 ? Fmt.R(rs.Average()) : "-") + "  ·  net " + Fmt.SM(journal.Sum(t => t.Net)) + "  ·  report.html";
                }
            }
            var m = PanelBuilder.Build(setups, global, Account.Balance, Account.Equity, RiskTarget, DayNow(), footer, fl, Version);

            string sig = m.Signature() + collapsed;
            if (sig == lastSig) return;
            lastSig = sig;

            // header: the daily stop stays visible even when folded (dots when counting losses, a chip for day-down-in-R)
            foreach (var ch in headStatus.Children.ToList()) headStatus.RemoveChild(ch);
            if (m.DayChip != null) headStatus.AddChild(Chip(m.DayChip, LvlTint(m.DayLvl), m.DayLvl == Lvl.Ok ? CText : LvlColor(m.DayLvl), FontSize - 2));
            for (int i = 0; i < m.MaxLosses; i++)
                headStatus.AddChild(new Ellipse { Width = 11, Height = 11, Margin = new Thickness(0, 0, 4, 0), StrokeThickness = 2,
                                                  StrokeColor = i < m.Losses ? CBad : CDim, FillColor = i < m.Losses ? CBad : Color.FromHex("#00000000") });
            int reds = m.Cards.Sum(c => c.Notes.Count(n => n.Level == Lvl.Bad)) + m.Global.Count(n => n.Level == Lvl.Bad);
            int yellows = m.Cards.Sum(c => c.Notes.Count(n => n.Level == Lvl.Warn)) + m.Global.Count(n => n.Level == Lvl.Warn);
            if (reds > 0) headStatus.AddChild(Chip(reds + " to fix", CBad, Color.FromHex("#FF101318"), FontSize - 2));
            if (yellows > 0) headStatus.AddChild(Chip(yellows + " to check", CWarn, Color.FromHex("#FF101318"), FontSize - 2));
            if (reds == 0 && yellows == 0) headStatus.AddChild(Chip("all good", COk, Color.FromHex("#FF101318"), FontSize - 2));

            foreach (var ch in body.Children.ToList()) body.RemoveChild(ch);
            if (collapsed) return;

            body.AddChild(Txt(m.Balance, CDim, FontSize - 1, false, 6));
            body.AddChild(Txt(m.Target, CDim, FontSize - 1));
            var day = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            day.AddChild(Txt("TODAY  ", CDim, FontSize - 2, true));
            day.AddChild(Txt(m.DayLine, m.DayLvl == Lvl.Ok ? CText : LvlColor(m.DayLvl), FontSize, true));
            body.AddChild(day);
            if (m.DayChip != null) body.AddChild(Meter(m.DayUsed, 1.0, m.DayLvl == Lvl.Ok ? CInfo : LvlColor(m.DayLvl)));
            if (m.DayItems.Count > 0)
            {
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Width = CardW, Margin = new Thickness(0, 4, 0, 0) };
                foreach (var it in m.DayItems)
                    wrap.AddChild(new Border { BackgroundColor = LvlTint(it.Level), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 3, 5, 0),
                                               Child = new TextBlock { Text = it.Text, ForegroundColor = LvlColor(it.Level), FontSize = FontSize - 2, FontWeight = FontWeight.Bold } });
                body.AddChild(wrap);
            }
            if (!string.IsNullOrEmpty(m.AllRisk)) body.AddChild(Txt(m.AllRisk, CDim, FontSize - 1, false, 2));
            foreach (var n in m.Global) body.AddChild(Pill(n));

            if (m.Cards.Count == 0)
                body.AddChild(new Border { Width = CardW, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(6),
                                           BackgroundColor = Color.FromHex("#FF1A1F29"), Child = Txt("No pending orders or open positions.", CDim, FontSize) });
            foreach (var c in m.Cards) body.AddChild(CardView(c));
            if (!string.IsNullOrEmpty(m.Footer)) body.AddChild(Txt(m.Footer, m.FooterLvl == Lvl.Warn ? CWarn : CDim, FontSize - 2, false, 10));
        }
    }
}
