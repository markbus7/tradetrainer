using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using TradeWatchCore;

// Draws the chart panel model as HTML, close to how the bot lays it out in cTrader,
// so the look can be checked without cTrader. Not used by the bot itself.
static class Preview
{
    static string H(string s) => WebUtility.HtmlEncode(s ?? "");
    static string Col(Lvl l) => l == Lvl.Bad ? "#FF6B6B" : l == Lvl.Warn ? "#F2B84B" : l == Lvl.Info ? "#7FB3FF" : "#4CC38A";
    static string Tint(Lvl l) => l == Lvl.Bad ? "#3B1F25" : l == Lvl.Warn ? "#3A3120" : l == Lvl.Info ? "#1C2A3D" : "#17322A";
    static string Mark(Lvl l) => l == Lvl.Bad ? "✗" : l == Lvl.Warn ? "!" : l == Lvl.Info ? "i" : "✓";

    static string Pill(Note n) =>
        $"<div class=pill style='background:{Tint(n.Level)}'><span class=dot style='background:{Col(n.Level)}'>{Mark(n.Level)}</span>" +
        $"<span style='color:{(n.Level == Lvl.Ok ? "#E6E9EF" : Col(n.Level))};{(n.Level == Lvl.Bad ? "font-weight:700" : "")}'>{H(n.Text)}</span></div>";

    public static string Panel(PanelModel m)
    {
        var keep = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { return Draw(m); } finally { System.Globalization.CultureInfo.CurrentCulture = keep; }
    }

    static string Draw(PanelModel m)
    {
        var sb = new StringBuilder();
        sb.Append(@"<!doctype html><meta charset=utf-8><style>
body{margin:0;background:#0b0e13 linear-gradient(180deg,#0f131a,#0b0e13);font:12px -apple-system,'Segoe UI',sans-serif;color:#E6E9EF}
.box{display:inline-block;margin:8px;background:#121620f0;border:1px solid #2E3542;border-radius:8px;padding:8px 10px 10px}
.head{display:flex;align-items:center;gap:0}.tg{width:26px;height:22px;border-radius:4px;background:#ffffff33;margin-right:8px;text-align:center;line-height:22px}
.dim{color:#9AA4B2}.b{font-weight:700}.chip{border-radius:4px;padding:1px 6px;margin-right:6px;font-weight:700;font-size:11px;color:#101318}
.ld{display:inline-block;width:7px;height:7px;border-radius:50%;border:2px solid #9AA4B2;margin-right:4px}
.card{width:430px;box-sizing:border-box;margin-top:10px;padding:8px 10px 10px;border-radius:6px;background:#1A1F29;border:1px solid;border-left-width:4px}
.stats{display:grid;grid-template-columns:1fr 1fr 1fr;margin-top:8px}.cap{font-size:10px;font-weight:700;color:#9AA4B2}.val{font-size:17px;font-weight:700}.sub{font-size:11px;color:#9AA4B2}
.meter{position:relative;height:14px;margin-top:6px}.meter i{position:absolute;top:4px;height:6px;border-radius:3px;left:0}
.lad{position:relative;height:44px;margin-top:8px}.lad *{position:absolute}
table{width:100%;margin-top:6px;border-collapse:collapse;font-size:11px;table-layout:fixed}td,th{text-align:left;padding:1px 0}th{font-size:9px;color:#9AA4B2}
.pill{display:flex;gap:8px;align-items:flex-start;border-radius:5px;padding:4px 6px;margin-top:4px}.dot{flex:none;width:18px;height:18px;border-radius:9px;color:#101318;font-weight:700;text-align:center;line-height:18px;font-size:11px}
</style><div class=box>");
        int reds = m.Cards.Sum(c => c.Notes.Count(n => n.Level == Lvl.Bad)) + m.Global.Count(n => n.Level == Lvl.Bad);
        int yel = m.Cards.Sum(c => c.Notes.Count(n => n.Level == Lvl.Warn)) + m.Global.Count(n => n.Level == Lvl.Warn);
        sb.Append("<div class=head><span class=tg>–</span><span class=b style='font-size:14px'>TRADE WATCH</span><span class=dim style='font-size:11px'>&nbsp; v" + H(m.Title.Split(' ').Last()) + "</span><span style='margin-left:10px;display:flex;align-items:center'>");
        for (int i = 0; i < m.MaxLosses; i++) sb.Append(i < m.Losses ? "<span class=ld style='background:#FF6B6B;border-color:#FF6B6B'></span>" : "<span class=ld></span>");
        if (reds > 0) sb.Append($"<span class=chip style='background:#FF6B6B;margin-left:4px'>{reds} to fix</span>");
        if (yel > 0) sb.Append($"<span class=chip style='background:#F2B84B'>{yel} to check</span>");
        if (reds == 0 && yel == 0) sb.Append("<span class=chip style='background:#4CC38A;margin-left:4px'>all good</span>");
        sb.Append("</span></div>");
        sb.Append($"<div class=dim style='margin-top:6px;font-size:11px'>{H(m.Balance)}</div><div class=dim style='font-size:11px'>{H(m.Target)}</div>");
        string dayCol = m.Losses >= m.MaxLosses ? "#FF6B6B" : m.Losses == m.MaxLosses - 1 ? "#F2B84B" : "#E6E9EF";
        sb.Append($"<div style='margin-top:6px'><span class='dim b' style='font-size:10px'>TODAY &nbsp;</span><span class=b style='color:{dayCol}'>{m.Losses} of {m.MaxLosses} losses · {(m.Scratches > 0 ? m.Scratches + " scratch · " : "")}{m.Trades} trade{(m.Trades == 1 ? "" : "s")} · </span><span class=b style='color:{(m.DayPnlVal >= 0 ? "#4CC38A" : "#FF6B6B")}'>{H(m.DayPnl)}</span></div>");
        if (!string.IsNullOrEmpty(m.AllRisk)) sb.Append($"<div class=dim style='font-size:11px;margin-top:2px'>{H(m.AllRisk)}</div>");
        foreach (var n in m.Global) sb.Append("<div style='width:430px'>" + Pill(n) + "</div>");
        foreach (var c in m.Cards)
        {
            sb.Append($"<div class=card style='border-color:{Col(c.Status)}'>");
            sb.Append($"<div style='display:flex;align-items:center'><span class=b style='font-size:15px;margin-right:8px'>{H(c.Symbol)}</span>" +
                      $"<span class=chip style='background:{(c.Buy ? "#1F8A5B" : "#C2414B")};color:#fff'>{(c.Buy ? "BUY" : "SELL")}</span>" +
                      $"<span class=chip style='background:#2A303B;color:#E6E9EF;font-size:10px'>{H(c.Kind.ToUpperInvariant())}</span><span class=dim>{H(c.Stage)}</span></div>");
            string riskCol = c.RiskLvl == Lvl.Bad ? "#FF6B6B" : "#E6E9EF";
            string nowCol = !c.HasNow ? "#9AA4B2" : c.NowVal >= 0 ? "#4CC38A" : "#FF6B6B";
            sb.Append("<div class=stats>" +
                      $"<div><div class=cap>RISK</div><div class=val style='color:{riskCol}'>{H(c.Risk)}</div><div class=sub>{H(c.RiskSub)}</div></div>" +
                      $"<div><div class=cap>AT TP</div><div class=val style='color:{(c.HasReward ? "#4CC38A" : "#F2B84B")}'>{H(c.Reward)}</div><div class=sub>{H(c.RewardSub)}</div></div>" +
                      $"<div><div class=cap>NOW</div><div class=val style='color:{nowCol}'>{H(c.Now)}</div><div class=sub>{H(c.NowSub)}</div></div></div>");
            string mc = c.RiskLvl == Lvl.Bad ? "#FF6B6B" : c.MeterFill > c.MeterTarget * 0.5 ? "#4CC38A" : "#7FB3FF";
            sb.Append($"<div class=meter><i style='width:100%;background:#2A303B'></i><i style='width:{c.MeterFill * 100:0.0}%;background:{mc}'></i>" +
                      $"<i style='left:{c.MeterTarget * 100:0.0}%;width:2px;height:14px;top:0;border-radius:0;background:#E6E9EF'></i></div>");
            sb.Append("<div class=sub style='font-size:9px'>risk if all fill and stop · white line = your target</div>");
            // ladder: same geometry as the bot (406 px wide, 10 px margins)
            double w = 406, x0 = 10, span = w - 20;
            Func<double, double> X = p => x0 + span * Math.Max(0, Math.Min(1, p));
            sb.Append("<div class=lad style='width:406px'>");
            sb.Append($"<i style='left:{x0}px;top:19px;width:{span}px;height:4px;background:#2A303B'></i>");
            if (!double.IsNaN(c.ZoneSl) && c.ZoneEntry > c.ZoneSl) sb.Append($"<i style='left:{X(c.ZoneSl):0}px;top:17px;width:{X(c.ZoneEntry) - X(c.ZoneSl):0}px;height:8px;background:#FF6B6BAA'></i>");
            if (!double.IsNaN(c.ZoneTp) && c.ZoneTp > c.ZoneEntry) sb.Append($"<i style='left:{X(c.ZoneEntry):0}px;top:17px;width:{X(c.ZoneTp) - X(c.ZoneEntry):0}px;height:8px;background:#4CC38AAA'></i>");
            foreach (var mk in c.Marks)
            {
                double x = X(mk.Pos); bool top = mk.Kind == "fill" || mk.Kind == "pend" || mk.Kind == "now";
                string col = mk.Kind == "sl" ? "#FF6B6B" : mk.Kind == "tp" ? "#4CC38A" : mk.Kind == "now" ? "#7FB3FF" : "#E6E9EF";
                if (mk.Kind == "now") sb.Append($"<i style='left:{x - 5:0}px;top:16px;width:6px;height:6px;border-radius:50%;background:#7FB3FF;border:2px solid #121620'></i>");
                else if (mk.Kind == "pend") sb.Append($"<i style='left:{x - 5:0}px;top:16px;width:6px;height:6px;border-radius:50%;background:#121620;border:2px solid #E6E9EF'></i>");
                else if (mk.Kind == "fill") sb.Append($"<i style='left:{x - 5:0}px;top:16px;width:10px;height:10px;border-radius:50%;background:#E6E9EF'></i>");
                else sb.Append($"<i style='left:{x - 1.5:0}px;top:13px;width:3px;height:16px;background:{col}'></i>");
                sb.Append($"<b style='left:{Math.Max(0, Math.Min(w - 26, x - 9)):0}px;top:{(top ? 0 : 30)}px;font-size:9px;color:{col}'>{H(mk.Label)}</b>");
            }
            sb.Append("</div><table><tr><th></th><th>LOTS</th><th>ENTRY</th><th></th><th>SL</th><th>TP</th><th>P/L</th></tr>");
            foreach (var r in c.Rows)
                sb.Append($"<tr><td class=b>{H(r.Name)}</td><td>{H(r.Lots)}</td><td>{H(r.Entry)}</td><td style='color:{(r.Filled ? "#7FB3FF" : "#9AA4B2")};font-size:10px'>{H(r.Status)}</td>" +
                          $"<td style='color:{(r.Sl == "none" ? "#FF6B6B" : "#FF9B9B")}'>{H(r.Sl)}</td><td style='color:{(r.Tp == "none" ? "#F2B84B" : "#8FE0B5")}'>{H(r.Tp)}</td>" +
                          $"<td class=b style='color:{(r.PnlVal >= 0 ? "#4CC38A" : "#FF6B6B")}'>{H(r.Pnl)}</td></tr>");
            sb.Append($"</table><div class=sub style='margin-top:6px;font-size:10px'>{H(c.Costs)}</div>");
            foreach (var n in c.Notes) sb.Append(Pill(n));
            sb.Append("</div>");
        }
        if (!string.IsNullOrEmpty(m.Footer)) sb.Append($"<div class=dim style='margin-top:10px;font-size:10px'>{H(m.Footer)}</div>");
        sb.Append("</div>");
        return sb.ToString();
    }
}
