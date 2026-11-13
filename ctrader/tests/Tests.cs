using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using TradeWatchCore;

static class Tests
{
    static int pass, fail;
    static void Ok(bool cond, string name)
    {
        if (cond) pass++; else { fail++; Console.WriteLine("FAIL: " + name); }
    }
    static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

    // EURUSD on a EUR account: one pip on one unit is 0.0001 USD = 0.0001 / 1.085 EUR
    const double PV = 0.0001 / 1.085;

    static WItem Eur(bool buy, bool filled, long id, double lots, double entry, double? sl, double? tp, double comm = 0, string sym = "EURUSD")
        => new WItem { Symbol = sym, Buy = buy, Filled = filled, Id = id, Lots = lots, Units = lots * 100000, Entry = entry, Sl = sl, Tp = tp,
                       PipSize = 0.0001, PipValue = PV, Digits = 5, StepLots = 0.01, CommRT = comm, CommKnown = true, SpreadPips = 0.3 };

    static bool Has(Setup s, string code) => s.Notes.Any(n => n.Code == code);

    static int Main()
    {
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");   // Mark's Mac may use Dutch number format: files must not care

        // ---- grouping: never mix symbols or directions
        var mixed = new List<WItem> {
            Eur(true, false, 1, 0.02, 1.08500, 1.08300, 1.08700),
            Eur(false, false, 2, 0.02, 1.09000, 1.09200, 1.08800),
            Eur(true, false, 3, 0.02, 1.27000, 1.26800, 1.27200, 0, "GBPUSD"),
            Eur(true, true, 4, 0.02, 1.08510, 1.08300, 1.08700),
        };
        var g = Analyzer.Build(mixed, 240, 3);
        Ok(g.Count == 3, "three setups: EURUSD buy, EURUSD sell, GBPUSD buy");
        Ok(g.All(s => s.Items.All(i => i.Symbol == s.Symbol && i.Buy == s.Buy)), "every item sits in its own symbol + direction");
        var eurBuy = g.Single(s => s.Symbol == "EURUSD" && s.Buy);
        Ok(eurBuy.Items.Count == 2, "EURUSD buys together");
        Ok(Has(eurBuy, "both_dirs") && Has(g.Single(s => s.Symbol == "EURUSD" && !s.Buy), "both_dirs"), "both-directions note on both EURUSD setups");
        Ok(!Has(g.Single(s => s.Symbol == "GBPUSD"), "both_dirs"), "no both-directions note on GBPUSD");

        // ---- risk math: 0.02 lot, 20-pip stop
        var one = Analyzer.Build(new[] { Eur(true, false, 1, 0.02, 1.08500, 1.08300, 1.08800, 0.12) }, 240, 3).Single();
        double expRisk = 20 * PV * 2000 + 0.12;
        Ok(Near(one.FullRisk.Value, expRisk, 1e-9), "risk = pips x pip value x units + round-trip commission (" + one.FullRisk + ")");
        Ok(Near(one.FullReward.Value, 30 * PV * 2000 - 0.12, 1e-9), "reward at TP minus commission");
        Ok(Near(one.RiskPct, expRisk / 240 * 100, 1e-9), "risk % of balance");
        Ok(Has(one, "risk_ok") && !Has(one, "risk_high"), "3.8 EUR on 240 is 1.6%: inside the 3% target");
        Ok(one.Stage == "waiting to fill" && !one.ScaleIn, "single pending order is not a scale-in");

        // ---- risk too high, with sizes that fit
        var big = Analyzer.Build(new[] { Eur(true, false, 1, 0.10, 1.08500, 1.08300, 1.08800) }, 240, 3).Single();
        var rh = big.Notes.FirstOrDefault(n => n.Code == "risk_high");
        Ok(rh != null && rh.Level == Lvl.Bad, "0.10 lot with a 20-pip stop on 240 is above 3%");
        Ok(rh != null && rh.Text.Contains("0.10 -> 0.03"), "suggests 0.03 lot (" + rh?.Text + ")");

        // ---- missing stop
        var nosl = Analyzer.Build(new[] { Eur(true, false, 1, 0.02, 1.08500, null, 1.08800) }, 240, 3).Single();
        Ok(!nosl.FullRisk.HasValue && Has(nosl, "no_sl") && nosl.Worst == Lvl.Bad, "no SL: unlimited risk, red");
        var wrong = Analyzer.Build(new[] { Eur(true, false, 1, 0.02, 1.08500, 1.08600, 1.08400) }, 240, 3).Single();
        Ok(Has(wrong, "sl_side") && Has(wrong, "tp_side"), "SL and TP on the wrong side of a buy");

        // ---- range scale-in, buy: E1 1.08500, E2 1.08400, stop 1.08300
        var plan = new List<WItem> {
            Eur(true, false, 1, 0.02, 1.08500, 1.08300, 1.08700),
            Eur(true, false, 2, 0.02, 1.08400, 1.08300, 1.08500),
        };
        var si = Analyzer.Build(plan, 240, 3).Single();
        Ok(si.ScaleIn && si.Levels.Count == 2, "two limit levels = scale-in");
        Ok(si.Levels[0][0].Entry == 1.08500, "buy: entry 1 is the higher one");
        Ok(!Has(si, "tp_e2") && !Has(si, "stop_tight") && !Has(si, "stops_differ"), "a correct plan gets no warnings");
        Ok(Has(si, "risk_ok") || Has(si, "risk_low"), "risk note present");
        Ok(si.Worst <= Lvl.Info, "correct plan is green/blue");

        var badE2 = Analyzer.Build(new[] { plan[0], Eur(true, false, 2, 0.02, 1.08400, 1.08300, 1.08700) }, 240, 3).Single();
        Ok(Has(badE2, "tp_e2"), "entry 2 TP not at entry 1: warning");

        var tight = Analyzer.Build(new[] { Eur(true, false, 1, 0.02, 1.08500, 1.08370, 1.08700), Eur(true, false, 2, 0.02, 1.08400, 1.08370, 1.08500) }, 240, 3).Single();
        Ok(Has(tight, "stop_tight"), "stop 3 pips beyond entry 2 with a 10-pip gap is tighter than the plan");

        var diff = Analyzer.Build(new[] { Eur(true, false, 1, 0.02, 1.08500, 1.08320, 1.08700), Eur(true, false, 2, 0.02, 1.08400, 1.08300, 1.08500) }, 240, 3).Single();
        Ok(Has(diff, "stops_differ"), "different stops in one setup");

        // entry 1 filled
        var e1 = Analyzer.Build(new[] { Eur(true, true, 1, 0.02, 1.08502, 1.08300, 1.08700), plan[1] }, 240, 3).Single();
        Ok(e1.Stage == "entry 1 filled" && Has(e1, "e1_only"), "entry 1 filled stage and reminder");
        Ok(e1.FilledRisk.HasValue && e1.FilledRisk.Value < e1.FullRisk.Value, "filled risk is smaller than full plan risk");

        // both filled, TP still at the middle -> move TP
        var both = Analyzer.Build(new[] { Eur(true, true, 1, 0.02, 1.08502, 1.08300, 1.08700), Eur(true, true, 2, 0.02, 1.08400, 1.08300, 1.08500) }, 240, 3).Single();
        var tp2 = both.Notes.FirstOrDefault(n => n.Code == "tp2");
        Ok(both.Stage == "both entries filled" && tp2 != null && tp2.Level == Lvl.Bad, "both filled, TP not at entry 1: red");
        Ok(tp2 != null && tp2.Text.Contains("1.08502"), "tells the price of entry 1 (" + tp2?.Text + ")");
        var bothOk = Analyzer.Build(new[] { Eur(true, true, 1, 0.02, 1.08502, 1.08300, 1.08500), Eur(true, true, 2, 0.02, 1.08400, 1.08300, 1.08500) }, 240, 3).Single();
        Ok(!Has(bothOk, "tp2") && Has(bothOk, "tp2_ok") && !Has(bothOk, "tp_side"), "both filled and TP at entry 1: fine");

        // sell scale-in: entry 1 is the lower one
        var sell = Analyzer.Build(new[] { Eur(false, false, 1, 0.02, 1.09100, 1.09300, 1.08900), Eur(false, false, 2, 0.02, 1.09000, 1.09300, 1.08900) }, 240, 3).Single();
        Ok(sell.ScaleIn && sell.Levels[0][0].Entry == 1.09000, "sell: entry 1 is the lower one");
        Ok(Has(sell, "tp_e2"), "sell entry 2 TP must be at entry 1 (1.09000)");
        Ok(!Has(sell, "stop_tight"), "sell: 20 pips beyond entry 2 with a 10-pip gap is fine");

        // a stop order setup is not treated as a scale-in
        var st = Eur(true, false, 2, 0.02, 1.08700, 1.08500, 1.09000); st.IsLimit = false; st.OrderType = "stop";
        var mix = Analyzer.Build(new[] { plan[0], st }, 240, 3).Single();
        Ok(!mix.ScaleIn, "limit + stop order is not a scale-in");

        // spread wide against a tiny stop
        var sp = Eur(true, false, 1, 0.02, 1.08500, 1.08480, 1.08540); sp.SpreadPips = 0.5;
        Ok(Has(Analyzer.Build(new[] { sp }, 240, 3).Single(), "spread"), "0.5-pip spread on a 2-pip stop");

        // orphan pending order after the trade closed
        var orph = Analyzer.Build(new[] { plan[1] }, 240, 3, new List<string> { "EURUSD|BUY" }).Single();
        Ok(Has(orph, "orphan"), "pending order left after a close");

        // stop in profit
        var lockd = Analyzer.Build(new[] { Eur(true, true, 1, 0.02, 1.08500, 1.08550, 1.08700) }, 240, 3).Single();
        Ok(Has(lockd, "sl_profit") && lockd.FullRisk.Value < 0 && Has(lockd, "risk_free"), "trailed stop locks profit");

        // ---- day: positions closed together are one trade
        var t0 = new DateTime(2026, 11, 13, 10, 0, 0);
        var day = Day.Group(new[] {
            new ClosedPos { PositionId = 1, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddMinutes(40), Net = -3.7 },
            new ClosedPos { PositionId = 2, Symbol = "EURUSD", Buy = true, Open = t0.AddMinutes(10), Close = t0.AddMinutes(40.5), Net = -1.9 },
            new ClosedPos { PositionId = 3, Symbol = "EURUSD", Buy = false, Open = t0, Close = t0.AddMinutes(41), Net = 2.0 },
            new ClosedPos { PositionId = 4, Symbol = "EURUSD", Buy = true, Open = t0.AddHours(2), Close = t0.AddHours(3), Net = -2.0 },
        });
        Ok(day.Count == 3, "scale-in stopped together counts once; sell separate; later buy separate");
        Ok(day.Count(d => d.Net < 0) == 2, "two losses");
        // Mark's 13 Nov: gold -12.83, EURUSD +1.55, EURUSD -0.45 (a scratch). Balance 223.52, 3% risk = 6.71, scratch under 20% = 1.34
        double scr = Day.ScratchMoney(223.52, 3, 20);
        var mark = new[] { -12.83, 1.55, -0.45 };
        Ok(Near(scr, 1.34112, 1e-4), "scratch limit = 20% of 3% of balance");
        Ok(mark.Count(n => Day.IsLoss(n, scr)) == 1 && mark.Count(n => Day.IsScratch(n, scr) && n < 0) == 1, "-0.45 is a scratch, only gold is a loss");
        Ok(Day.IsLoss(-1.5, scr) && !Day.IsLoss(-1.3, scr) && !Day.IsLoss(0.2, scr), "scratch edge");
        Ok(Day.IsLoss(-0.01, 0), "scratch off (0%): every loss counts");
        // Mark's whole 13 Nov: gold -12.83, +1.55, scratch -0.45, -5.01. Day started at 240.26, so 1R = 7.21
        var mday = new List<TradeGroup> { new TradeGroup { Net = -12.83 }, new TradeGroup { Net = 1.55 }, new TradeGroup { Net = -0.45 }, new TradeGroup { Net = -5.01 } };
        double r1 = 240.26 * 0.03, scr2 = Day.ScratchMoney(240.26, 3, 20);
        var cnt = DayRules.Evaluate(mday, DayRule.Count_losses, r1, 2, 2, scr2);
        Ok(cnt.Losses == 2 && cnt.Scratches == 1 && cnt.Stopped, "count mode: 2 losses + 1 scratch = stopped at 2");
        Ok(!DayRules.Evaluate(mday, DayRule.Count_losses, r1, 2, 3, scr2).Stopped, "count mode with 3 allowed: not stopped");
        var r2 = DayRules.Evaluate(mday, DayRule.Day_down_in_R, r1, 2, 2, scr2);
        Ok(r2.Stopped && Near(r2.Net, -16.74, 1e-9) && r2.Used == 1, "day down 2R: -16.74 is past -14.42, stopped");
        var r3 = DayRules.Evaluate(mday, DayRule.Day_down_in_R, r1, 3, 2, scr2);
        Ok(!r3.Stopped && r3.OneLeft, "day down 3R: -16.74 of -21.62, one more full loss ends it");
        var many = Enumerable.Range(0, 10).Select(i => new TradeGroup { Net = i % 2 == 0 ? -7.2 : 7.0 }).ToList();
        var rm = DayRules.Evaluate(many, DayRule.Day_down_in_R, 7.2, 2, 2, 1.44);
        Ok(!rm.Stopped && rm.Losses == 5, "10 trades, 5 full losses, day only -1R: R mode lets you trade on");
        Ok(DayRules.Evaluate(many, DayRule.Count_losses, 7.2, 2, 2, 1.44).Stopped, "same day in count mode would have stopped");
        Ok(cnt.Items.Count == 4 && cnt.Items.Count(i => i.Level == Lvl.Bad) == 2 && cnt.Items.Single(i => i.Level == Lvl.Info).Text.Contains("-€0.45 · scratch"), "today's list marks each trade");
        Ok(r2.Line.Contains("stop at -€14.42") && r2.Chip.StartsWith("day -2.3R"), "day line text (" + r2.Line + " | " + r2.Chip + ")");
        Ok(Day.MaxLosses(3, 0) == 2 && Day.MaxLosses(1, 0) == 3 && Day.MaxLosses(3, 4) == 4, "daily stop: 2 above 1% risk, 3 otherwise, override wins");

        // ---- journal row from a finished campaign
        var camp = new Campaign { Symbol = "EURUSD", Buy = true, Start = t0, Kind = "scale-in", PlanRisk = 6.0, PlanRiskPct = 2.5, PlanReward = 5,
                                  PlanSl = 1.08300, PlanTp = 1.08700, PipSize = 0.0001, PipValue = PV, Digits = 5, Best = 1.08650, Worst = 1.08350 };
        camp.LastSl["1"] = 1.08300; camp.LastSl["2"] = 1.08300; camp.LastTp["1"] = 1.08500; camp.LastTp["2"] = 1.08500;
        camp.Breaks.Add("tp2");
        var parts = new List<ClosedPos> {
            new ClosedPos { PositionId = 1, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddHours(2), Net = -0.12, Units = 2000, EntryPrice = 1.08500, ClosePrice = 1.08500, Comm = -0.12 },
            new ClosedPos { PositionId = 2, Symbol = "EURUSD", Buy = true, Open = t0.AddMinutes(30), Close = t0.AddHours(2), Net = 1.72, Units = 2000, EntryPrice = 1.08400, ClosePrice = 1.08500, Comm = -0.12 },
        };
        var row = TradeBuilder.Finish(camp, parts);
        Ok(row.ExitType == "TP", "both closed at their TP: exit TP");
        Ok(Near(row.Net, 1.60) && Near(row.R, 1.60 / 6.0), "net and R");
        Ok(row.Entries == "1.08500x0.02 + 1.08400x0.02", "entries text (" + row.Entries + ")");
        Ok(!double.IsNaN(row.BestR) && row.BestR > 0, "best point in R");
        Ok(row.Breaks == "tp2", "rule breaks kept");

        // manual early exit, then price reaches TP
        var camp2 = new Campaign { Symbol = "EURUSD", Buy = true, Kind = "single", PlanRisk = 3.7, PlanRiskPct = 1.5, PlanSl = 1.08300, PlanTp = 1.08800,
                                   PipSize = 0.0001, PipValue = PV, Digits = 5 };
        camp2.LastSl["7"] = 1.08300; camp2.LastTp["7"] = 1.08800;
        var early = TradeBuilder.Finish(camp2, new List<ClosedPos> { new ClosedPos { PositionId = 7, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddHours(1), Net = 1.0, Units = 2000, EntryPrice = 1.08500, ClosePrice = 1.08560 } });
        Ok(early.ExitType == "manual", "closed between SL and TP: manual");
        var after = new List<PBar> { new PBar(t0.AddHours(1).AddMinutes(1), 1.08600, 1.08540), new PBar(t0.AddHours(1).AddMinutes(2), 1.08810, 1.08590) };
        Follow.Apply(early, after, false);
        Ok(early.Follow == "tp_after" && Near(early.Missed, 24 * PV * 2000, 1e-9), "missed: 24 pips to the TP (" + early.Missed + ")");

        // stop-out, then TP anyway
        var camp3 = new Campaign { Symbol = "EURUSD", Buy = true, Kind = "single", PlanRisk = 3.7, PlanRiskPct = 1.5, PlanSl = 1.08300, PlanTp = 1.08800, PipSize = 0.0001, PipValue = PV, Digits = 5 };
        camp3.LastSl["8"] = 1.08300; camp3.LastTp["8"] = 1.08800;
        var stopped = TradeBuilder.Finish(camp3, new List<ClosedPos> { new ClosedPos { PositionId = 8, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddHours(1), Net = -3.7, Units = 2000, EntryPrice = 1.08500, ClosePrice = 1.08300 } });
        Ok(stopped.ExitType == "SL", "closed at stop: SL");
        Follow.Apply(stopped, new List<PBar> { new PBar(t0.AddHours(2), 1.08200, 1.08100), new PBar(t0.AddHours(3), 1.08850, 1.08200) }, false);
        Ok(stopped.Follow == "tp_after", "after a stop-out, lower prices don't matter: TP came later");

        // early exit that was right
        var camp4 = new Campaign { Symbol = "EURUSD", Buy = true, Kind = "single", PlanRisk = 3.7, PlanSl = 1.08300, PlanTp = 1.08800, PipSize = 0.0001, PipValue = PV, Digits = 5 };
        var good = TradeBuilder.Finish(camp4, new List<ClosedPos> { new ClosedPos { PositionId = 9, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddHours(1), Net = -1, Units = 2000, EntryPrice = 1.08500, ClosePrice = 1.08450 } });
        Follow.Apply(good, new List<PBar> { new PBar(t0.AddHours(2), 1.08460, 1.08290) }, false);
        Ok(good.Follow == "sl_after" && good.Missed < 0, "good early exit: saved money");
        var none = TradeBuilder.Finish(camp4, new List<ClosedPos> { new ClosedPos { PositionId = 10, Symbol = "EURUSD", Buy = true, Open = t0, Close = t0.AddHours(1), Net = -1, Units = 2000, EntryPrice = 1.08500, ClosePrice = 1.08450 } });
        Follow.Apply(none, new List<PBar> { new PBar(t0.AddHours(2), 1.08460, 1.08400) }, false);
        Ok(none.Follow == "", "still waiting inside the window");
        Follow.Apply(none, new List<PBar> { new PBar(t0.AddHours(2), 1.08460, 1.08400) }, true);
        Ok(none.Follow == "none", "window over, nothing hit");

        // a bar touching both counts as a loss
        Ok(Follow.FirstHit(true, 1.08800, 1.08300, new[] { new PBar(t0, 1.08900, 1.08200) }) == "sl", "both in one bar: assume the stop");

        // cancelled order what-if
        var cr = new CancelRow { Id = "5", Symbol = "EURUSD", Dir = "BUY", Time = t0, Reason = "cancelled", Entry = 1.08400, Sl = 1.08300, Tp = 1.08500, Units = 2000, PipSize = 0.0001, PipValue = PV, Digits = 5 };
        Follow.Apply(cr, new[] { new PBar(t0.AddMinutes(5), 1.08450, 1.08390), new PBar(t0.AddMinutes(6), 1.08510, 1.08420) }, false);
        Ok(cr.Follow == "tp" && Near(cr.Result, 10 * PV * 2000, 1e-9), "cancelled order would have won 10 pips");
        var cr2 = new CancelRow { Id = "6", Symbol = "EURUSD", Dir = "SELL", Time = t0, Reason = "expired", Entry = 1.09000, Sl = 1.09200, Tp = 1.08800, Units = 2000, PipSize = 0.0001, PipValue = PV, Digits = 5 };
        Follow.Apply(cr2, new[] { new PBar(t0.AddMinutes(5), 1.08950, 1.08900) }, true);
        Ok(cr2.Follow == "never", "never filled");
        var cr3 = new CancelRow { Id = "7", Symbol = "EURUSD", Dir = "SELL", Time = t0, Reason = "cancelled", Entry = 1.09000, Sl = 1.09200, Tp = 1.08800, Units = 2000, PipSize = 0.0001, PipValue = PV, Digits = 5, Leftover = true };
        Follow.Apply(cr3, new[] { new PBar(t0.AddMinutes(5), 1.09100, 1.08700) }, true);
        Ok(cr3.Follow == "n/a", "leftover entry 2 after a finished trade: no what-if");

        // ---- CSV round trip (Dutch culture, comma in text)
        early.FollowText = "Closed early, \"then\" TP";
        var back = TradeRow.FromCsv(Csv.Parse(string.Join(",", TradeRow.Cols) + "\n" + early.ToCsv() + "\n").Skip(1).Single());
        Ok(back.FollowText == early.FollowText && Near(back.Net, early.Net) && back.Open == early.Open && Near(back.Missed, early.Missed, 1e-6) && back.Entries == early.Entries,
           "trade row survives the CSV");
        Ok(early.ToCsv().Contains("1.085"), "dots, not commas, for decimals");
        Ok(double.IsNaN(TradeRow.FromCsv(Csv.Parse(new TradeRow { Id = "x", Symbol = "A", Dir = "BUY", Kind = "single", Entries = "", ExitType = "TP", Breaks = "", Open = t0, Close = t0 }.ToCsv()).Single()).Sl),
           "missing SL stays empty");
        var cback = CancelRow.FromCsv(Csv.Parse(cr.ToCsv()).Single());
        Ok(cback.Follow == "tp" && Near(cback.Entry, 1.084) && cback.Time == t0, "cancel row survives the CSV");

        // ---- campaign survives a restart (state.json)
        var json = JsonSerializer.Serialize(new Dictionary<string, Campaign> { { camp.Key, camp }, { "X|SELL", new Campaign { Symbol = "X" } } }, Json.Opt);
        var cb = JsonSerializer.Deserialize<Dictionary<string, Campaign>>(json, Json.Opt)["EURUSD|BUY"];
        Ok(cb.PlanRisk == 6.0 && cb.Breaks.Contains("tp2") && cb.LastTp["2"] == 1.08500 && cb.Buy, "campaign state round trip");
        var nanJson = JsonSerializer.Serialize(new Campaign { Symbol = "X" }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        Ok(nanJson.Contains("NaN"), "NaN needs named-literal handling");

        // ---- report
        var trades = new List<TradeRow> { row, early, stopped, good };
        stopped.Breaks = "risk_high"; stopped.PlanRiskPct = 4.5;
        var rep = Report.Build(trades, new List<CancelRow> { cr, cr2 }, 3, t0.AddDays(1));
        Ok(rep.Contains("## Overview") && rep.Contains("## What to work on") && rep.Contains("## What you missed out on"), "report sections");
        Ok(rep.Contains("closed 1 trade(s) early"), "early exit lesson");
        Ok(rep.Contains("risked more than 3.0%"), "risk lesson");
        Ok(rep.Contains("would have won"), "cancelled order lesson");
        Ok(rep.Contains("| Trades | 4 (2 won, 2 lost, 0 scratch) |"), "trade count line (" + rep.Split('\n').FirstOrDefault(l => l.StartsWith("| Trades")) + ")");
        var scratchRow = TradeRow.FromCsv(Csv.Parse(early.ToCsv()).Single()); scratchRow.Net = -0.4; scratchRow.R = -0.1;
        var rep2 = Report.Build(new List<TradeRow> { row, stopped, scratchRow }, new List<CancelRow>(), 3, t0.AddDays(1));
        Ok(rep2.Contains("(1 won, 1 lost, 1 scratch)") && rep2.Contains("| Win rate | 50% (scratches left out) |"), "scratch in the report");
        Ok(!rep.Contains(",00"), "no Dutch decimal commas in the report");
        Ok(Report.Build(new List<TradeRow>(), new List<CancelRow>(), 3, t0).Contains("No finished trades"), "empty report");
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "sample-report.md"), rep);
        var html = ReportHtml.Build(trades, new List<CancelRow> { cr, cr2 }, 3, t0.AddDays(1));
        Ok(html.Contains("<h2>What to work on</h2>") && html.Contains("class=tile") && html.Contains("<polyline"), "report page has tiles, curve and sections");
        Ok(html.Contains("<span class=neg>-€") && html.Contains("<span class=pos>+€"), "money coloured red and green");
        Ok(!html.Contains("<h2>Overview</h2>"), "overview shown as tiles, not twice");
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "sample-report.html"), html);

        // ---- panel model
        var pi1 = Eur(true, true, 11, 0.02, 1.08502, 1.08300, 1.08700, 0.12); pi1.NetProfit = 0.55; pi1.Current = 1.08535; pi1.SwapPerNight = -0.04;
        var pi2 = Eur(true, false, 12, 0.02, 1.08400, 1.08300, 1.08500, 0.12); pi2.Current = 1.08535;
        var gb = Eur(false, false, 13, 0.08, 1.27000, 1.27250, null, 0, "GBPUSD"); gb.Current = 1.26930; gb.PipValue = 0.0001 / 1.085; gb.CommKnown = false;
        var ps = Analyzer.Build(new[] { pi1, pi2, gb }, 240, 3);
        var gl = new List<Note> { new Note(Lvl.Warn, "daily_last", "day|last", "One more loss ends your day (1 of 2).") };
        var t9 = new DateTime(2026, 11, 13, 9, 30, 0);
        var dayGroups = new List<TradeGroup> { new TradeGroup { Symbol = "EURUSD", Buy = true, Close = t9, Net = -3.70 }, new TradeGroup { Symbol = "EURUSD", Buy = false, Close = t9.AddHours(2), Net = 1.10 }, new TradeGroup { Symbol = "GBPUSD", Buy = true, Close = t9.AddHours(3), Net = -0.40 } };
        var dst = DayRules.Evaluate(dayGroups, DayRule.Day_down_in_R, 7.2, 2, 2, 1.44);
        var pm = PanelBuilder.Build(ps, gl, 240, 240.55, 3, dst, "Journal: 12 trades  ·  58% won  ·  avg +0.21R  ·  net +€9.40  ·  report.html", Lvl.Info, "1.2.1");
        var ec = pm.Cards.Single(c => c.Symbol == "EURUSD");
        Ok(ec.Rows.Count == 2 && ec.Rows[0].Name == "E1" && ec.Rows[0].Filled && !ec.Rows[1].Filled, "card rows: E1 filled, E2 pending");
        var slm = ec.Marks.Single(x => x.Kind == "sl"); var tpm = ec.Marks.Where(x => x.Kind == "tp").ToList(); var e2m = ec.Marks.Single(x => x.Label == "E2");
        Ok(slm.Pos < e2m.Pos && e2m.Pos < ec.ZoneEntry && tpm.Max(x => x.Pos) > ec.ZoneEntry && tpm.Min(x => x.Pos) >= e2m.Pos, "buy ladder: SL left, E2, E1, TP right");
        var gc = pm.Cards.Single(c => c.Symbol == "GBPUSD");
        var gsl = gc.Marks.Single(x => x.Kind == "sl"); var gnow = gc.Marks.Single(x => x.Kind == "now");
        Ok(gsl.Pos < gc.ZoneEntry && gnow.Pos > gc.ZoneEntry, "sell ladder is flipped: SL still on the left, price below entry on the right");
        Ok(gc.RiskLvl == Lvl.Bad && gc.Status == Lvl.Bad && gc.Reward == "no TP", "GBPUSD: risk too high, no TP");
        Ok(pm.Signature() == PanelBuilder.Build(ps, gl, 240, 240.55, 3, dst, pm.Footer, Lvl.Info, "1.2.1").Signature(), "same data, same picture (no redraw)");
        System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "panel-preview.html"), Preview.Panel(pm));

        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
