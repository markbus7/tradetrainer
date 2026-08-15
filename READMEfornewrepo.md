# Trade Trainer

A self-contained trade training app built on Al Brooks' price-action framework. It takes you from wherever you are now up to a certified "Master" tier — reading context, bar counting, entries, stops, targets, position sizing, trade management, and live bar-by-bar execution against generated (but structurally honest) charts.

Open `index.html` directly in a browser — no build step, no server required.

## What's inside

- **Market engine** — seeded chart generator with volatility clustering, fat-tailed shocks, wick sweeps at pivots, and volume expansion on breakouts. Bar sequences are calibrated against real market data: on a real tape ~54% of bars close the opposite colour to the previous one and runs of six or more same-colour bars are ~2.5% of all runs; the generator sits at 49% and 2.9%. Every context's win rate is checked against the tape it actually produces, and none is more than a few points from the figure it quotes. 14 Brooks *contexts* (spike-and-channel, tight and broad channels, two-legged pullbacks, trading ranges, barbwire, breakout mode, failed and successful breakouts, wedge reversals, major trend reversals, final flags, climaxes), each mirrored into a bearish twin, and each carrying its own ground truth — always-in direction, cycle phase, measured move, invalidation, and the bar count at the decision bar — so every drill can be graded.
- **Chart tools** — candles, volume, 20-bar EMA, pan/zoom/pinch, a measured-move projector (A → B → C), and a draggable long/short position tool with live reward-to-risk.
- **Range geometry** — a trading range takes a realistic number of bars to traverse (~22–28 per swing), so ATR lands at a sensible fraction of the range height rather than a fifth of it. That is what makes a fade at the edge actually possible: before this, the signal bar alone was worth a fifth of the range and every fade entered mid-range for under 1R.
- **Curriculum** — 11 tiers, 17 drill kinds: context naming, always-in direction, cycle phase, signal-bar quality, Brooks bar counting (High/Low 1–4), entry placement, stop placement, targets, measured moves, take-or-pass discipline, scalp-vs-swing, trade management, sizing and expectancy maths, full trade plans, and live execution against a fill simulator.
- **Progress** — a placement test sets your starting tier; progress, skill mastery, and a simulated trade log persist to `localStorage`.

Everything runs client-side in a single HTML file.

## The beginner track

Tier 0 — *The first three setups* — comes before everything else and cannot be skipped by the placement test. It drills only the three highest-probability setups Brooks tells a new trader to trade (the two-legged pullback, the failed breakout back into a range, and the major trend reversal), end to end: recognise it, count it, enter it, stop it, target it, manage it, and pass on everything else. The rest of the curriculum unlocks behind it.

## Scaffolding fades with the tier

Questions never show the answer, at any tier. What fades is the **feedback**. Tiers 0–2 reveal everything and name it; tiers 3–5 keep the structure drawn but drop the naming labels; tiers 6–8 drop the range box and keep only the trade levels; Live execution and Mastery certification draw nothing but the level the drill actually asked you to place. Getting a question wrong restores the full picture at any tier — the threshold is the same 80% that counts toward a streak — and the lesson cards are always fully marked, reachable any time from the **Learn** button. When the chart stops naming something you were asked to find, the feedback states it in words instead.

## The funded account

The final tier hands you $25,000 with a prop-firm rule set: reach +6%, never lose 3% in a session, never sit 5% below your peak, five trades per session and forty in total. You set risk per trade and place the stop; size follows from both. Breach a limit and the run ends.

It is a constraint exercise, not a profit score — process is still the grade. The target is reachable because the edge is real, so blowing the drawdown is nearly always a sizing decision, and the debrief proves it by replaying *your own trades* at other risk levels: same reads, same order, different size, different outcome. It then models your realised win rate and average R into pass / blow-up / never-got-there odds against risk. On a typical edge here 1% passes about nine times in ten, 3% roughly halves that, and 5% blows up about four runs in ten — while no risk setting rescues weak reads.

## The management tier

A tier between Live execution and Mastery certification grades what you *pressed*, not what you named. Three rules decide it, all from Brooks:

- **Breakeven is about timing.** Move the stop to your entry once the trade has moved ~1R and the structure is intact — not early in choppy tape, because a stop sitting in the noise hands the trade back. Press it at 0.2R and the drill marks you down with the reason.
- **Trail in a trend, hold a fixed target in a range.** Trail behind the most recent higher low as a trend extends, and a trend is the one place a runner with no target makes sense. In two-sided trading a trailing stop is tapped by noise long before price reaches the other side.
- **Scaling in is graded on planned size, not on whether you did it.** Pushing open risk past the 1R you planned is the error; adding itself is not.

Every managed trade reports the blended result next to what holding untouched would have returned on the same tape — so the cost or gain of your management is a number, not an opinion.

## Managing a live trade

Positions are multi-leg. Once you are filled during a live replay you can take part off, move the stop to your **average** entry, trail it behind recent bars, scale in a second leg, drop the target and hold a runner, or close out. The read-out shows open size, average price, what you have banked and what is still at risk, all in R — and scaling in visibly pushes "at risk now" above 1R, which is why the total has to be planned before the first entry.

Everything is measured against the risk you originally planned (`R = total P&L ÷ initial planned risk`), so 1R means the same thing before and after you manage the trade. A trade with no actions taken produces exactly the result the old single-exit engine did, verified across 880 charts.

## Scalp, swing, and scaling in

Brooks uses "scalp" in two senses and the app now separates them. The **strict** definition is arithmetic — a scalp is a trade whose reward is *smaller* than its risk, usually over in one to three bars, which is why it needs a win rate above 70% and a good scalper runs 80–90%. A **swing** is a trade whose reward is at least twice the risk, and Brooks says swing trading should be the foundation for every trader. The looser sense, as in "buy low, sell high and scalp", just means take profit inside the range rather than holding for a trend. The scalp-or-swing drill now reports the chart's actual reward-to-risk so you can see which sense applies. The stop goes in the same place either way — scalp and swing differ in the target, never the stop.

**Scaling in** is a planned technique, not the same thing as adding because you are losing. Add once or twice rather than five times, add on a signal rather than at a price, and decide the total risk *before* the first entry so the first one is sized smaller. A scaled position is **one position at an average price**: breakeven means the average entry, not your first fill — which is exactly how a netting platform such as cTrader treats it.

## Why that bar

The chart brackets one bar and calls it the decision. Three things about that now get said on every revealed trade, because all three are checkable and none of them was obvious:

- **Which push it belongs to.** On 59% of charts the entry bar is not itself one of the numbered attempts — it is a continuation of the last counted push, because a push only counts again once a bar closes back the other way first.
- **What makes it the signal bar inside that push** — where it closes in its own range, which is Brooks' test. A bar closing 3% off its low is the one to sell below; the one two bars earlier closing 25% up from its low is weaker.
- **That the earlier bars were tradeable too.** Brooks does not hand out one blessed bar per correction. Entering sooner buys a better price against a bar that has proved less — a real trade-off, not a mistake. The drill asks at one bar because it has to ask somewhere.

The Help panel carries a short list of the questions this file should answer before they are asked — why that bar, why the count says H4 when the rule says second entry, where the target came from, why a textbook pattern is marked *not a trade*, which stop the grade uses, and how to reproduce a chart from its seed.

## Passing standard

A node is passed only when a **single session** clears both the pass mark (85% by default) and a required run of consecutive clean answers. An average alone can hide a skill you only half have, so the streak is what removes the doubt. Mastery certification requires 92% with an 8-answer streak.

## Fidelity notes

The teaching text aims to be Brooks' method rather than a paraphrase of it, and a few points are worth stating explicitly because they are commonly got wrong:

- **Bar counting is a bar count, not a leg count.** Following the Brooks Trading Course glossary: a High 1 is a bar with a high above the prior bar in a bull flag; if there is then a bar with a *lower high* (one or several bars later), the next bar in the correction whose high is above the prior bar's high is a High 2; third and fourth occurrences are a High 3 and 4. The re-arm condition is that single lower-high bar — **not** a new low for the pullback, and **not** a completed leg. The second leg does not have to trade below the first leg's low. The app computes the count off the actual bars with this rule, so the answer is whatever the tape shows — sometimes a High 1, sometimes a High 3.
- **Initial risk and actual risk are different numbers.** The protective stop goes one tick beyond the signal bar, and entry-to-stop is the *initial risk* — what you size from, and what belongs in the trader's equation, since you don't tighten a stop just because the market goes sideways after entry. The *actual risk* is entry to the extreme of the pullback that happens after you're filled; it is knowable only in hindsight and is usually far smaller. Every stop question reports both, measured off the tape. A stop beyond the structure is a defensible wider alternative — just a bigger stop, not a separately-named concept.
- **Entry side matters.** With the trend you enter on a stop beyond the signal bar so price has to prove itself; in a range you fade the edge with a limit. Getting this backwards turns a correct read into a losing trade, so it is drilled and graded separately.
- **A stop is a price on the chart, never a distance.** Every stop in the app sits a few ticks beyond a low or high you can point at — the level the context nominates, a swing, or the signal bar's own extreme — and the feedback names which one this example used, with the price and how far it is from the entry. Nothing about placement is derived from ATR; ATR is only a tolerance for grading how close your placement was. The level must also be one you could see at the decision bar: a stop taken from a bar that has not printed yet is a level the tape has, by construction, already traded through.
- **Winning is not the same as winning comfortably.** The feedback reports what fraction of the stop the trade actually used before it worked. Around half is typical; above 90% is a narrow win that a few ticks would have turned into a loss on the identical read, and it says so rather than letting a survived stop read as a well-placed one.
- **Every chart is the pattern it is named — every chart, not most of them.** The teaching text and the chart generator were written separately, and nothing forced them to agree, so a "major trend reversal" could be drawn with no trend in it to reverse. Each context now carries a predicate written from its own text — a reversal shows a trend with lower highs before it reverses, a two-legged pullback shows an established trend and two legs, a coil narrows, barbwire overlaps — and **a chart that fails its predicate is thrown away and regenerated**. Shaping the generator so the pattern usually appears is not the same guarantee: you see one chart at a time, and "usually" is what left 42% of reversals with no trendline break in them. The predicates only read structure visible before the decision bar, never the outcome, so nothing about the tape is fitted to the result.
- **A swing is named at one bar and measured to another.** The bar a structure turns on is what gives a swing its name; the furthest point the leg reached is what gives it its height. Measuring from the named bar when a bar inside the leg went further shortens the leg and shortens every target projected off it, silently and only sometimes — which is the worst way for an error to behave. A test now asserts on every context that no bar between a measured leg's two ends traded past either of them.
- **The count belongs to one correction.** Bar counts run High/Low 1 to 4. Past a High 4 the correction has become a trading range and the count has stopped meaning anything, so it restarts at the most recent swing the market turned from. Without that reset the charts were labelling High 11.
- **Each context declares its own rules; nothing is inferred from a family tag.** Whether Brooks takes the first attempt or waits for the second, and whether the trade is trailed or taken at a fixed target, are stated on the context and read from its own teaching. Inferring them from "this is a trend" graded a **two-legged pullback** as a first-entry setup — the one thing its own text rules out, since a two-legged pullback *is* the H2 — and graded a **broad channel** as a trend to trail when its text calls it a trading range on a tilt and says take profit at the top. A test now asserts that no context's graded answer contradicts the paragraph printed beside it.

## Reading the trade off the chart

The trade is drawn as a trade, not as three horizontal lines. Entry, stop and target **start at the bar you take them on** and run forward — a level stretching off to the left edge reads as support that was always there, which is the opposite of what an entry is. Between them sit two blocks: risk under the entry, reward above it, labelled `risk 1R` and `reward 2.40R`, so the question those two levels exist to answer is answered on the chart.

Every structural point the chart draws or measures from is capped at the decision bar. The contexts deliberately look a couple of bars past the signal to catch a wick, which is fine for shaping the tape and fatal for anything drawn from it: it was putting the origin of a measured move one bar off the right-hand edge, so the projection was invisible and the target looked arbitrary. That affected about one chart in eight.

**And the target says where it came from.** A target beyond every high and low on the screen is unreadable — you can see the level but not what put it there. When it is a measured move, the measurement is drawn: the leg that was measured, and that same height stood up again from the point it is projected off. A measured move is a **height** taken from one place and stood up somewhere else, so both halves are drawn as the same shape: a capped vertical bar with the height on it. One over the leg that was measured — the start price run across to the end bar, then dropped to the end price, so the right angle *is* the height. One standing on the point it projects from, out in the free space past the last bar where a projection belongs, with a dashed run back to the exact point it starts from. Two bars of visibly equal length, which is the claim.

**And it says how much of that is new ground.** Projecting a leg from the end of a correction has an arithmetic consequence that is easy to miss: the ground demanded beyond the leg's own end is exactly *the leg minus whatever the correction gave back*. It holds on every projected chart here, and it is why the same measurement produces very different-looking targets:

| setup | the correction gave back | new ground the target asks for |
|---|---|---|
| spike-and-channel, successful breakout, final flag | 0% | a **full leg** beyond the leg's end |
| two-legged pullback | ~73% | **0.28×** the leg |
| major trend reversal | ~77% | **0.24×** the leg |
| tight channel | −23% (price had already advanced) | **1.23×** the leg |
| range fade, failed breakout, wedge | 100% | **none** — the target is where the leg began |

The reward is about a full leg either way; what changes is how much fresh ground the market has to break to pay it, because a deep pullback got you in that much further back.

**A leg is measured between the extremes it actually reached.** Between the two ends of a measured leg, no bar traded past either of them — that is now true by construction on every context, and the feedback states both prices so it can be checked against the bars rather than believed. The bug this replaces was subtle and only bit in the tail: the ends were snapped to a local extreme within a few bars of the pattern's own anchor, which is right for *naming* a swing and wrong for *measuring* one. When a bar inside the leg had printed further than the bar the anchor landed on, the leg started short — up to 74% short in the worst case — and the projected target was short by exactly what was skipped. It affected 7–26% of charts depending on the context, and it is the reason a two-legged pullback could show its measured move starting from a high that visibly was not the highest one in that stretch. A swing gets its name at the bar the structure turns on; it gets measured to the furthest point the leg went.

The same rule fixed the major trend reversal's own measurement. Its rule is *"measure the first leg up off the low"*, but the app was measuring to the **bar that broke the trendline** — a bar somewhere inside that rally, typically 8% below its top and sometimes half way down it. The break is the *requirement*, not the measurement, and it keeps its own label; the leg is now measured to the highest point the rally reached, and a chart where price later exceeded that high is rejected, because then the leg has not finished.

A correction is also required to stay **inside** the leg it is correcting. One that retraces the whole thing has taken out the swing the trend was built on — a lower low in a bull trend, a higher high in a bear one — and whatever that is, it is not a pullback. It also wrecks the measured move, which is projected off the correction's extreme: past 100% the target barely clears the prior low, which is how a "highest-probability" setup ended up quoting 0.73R.

And when a target does not pay, **the fix is never a bigger target**. The objective is set by the structure — a measured move, a range edge, the level the pattern is trying to reach — not by what the arithmetic needs it to be. Moving it out until the ratio looks acceptable does not make the market go there. In words too, with the arithmetic — *the leg from 23,245 to 22,204 is 1,041 tall, and that height from 23,256 gives 22,215*. When the target is instead a level already on the chart — a range edge, a wedge's origin — it says so, because that is a different kind of claim.

## The reward has to pay for the risk

A correct read is not a trade. Brooks is arithmetic about it: *"like all trend reversals, the probability of a swing is usually only about 40%"*, and *"40% probability setups won't have a positive trader's equation unless you assess they are capable of reaching a risk/reward of 1:2."* A **swing** means a reward at least twice the risk; a **scalp** may risk more than it makes, but only while the win rate carries the equation.

So every generated chart is measured against its own style, and one that fails is tagged **not a trade** rather than quietly presented as a good example. The target is never stretched to make the number — it stays the structural measured move, because inventing a target to clear a threshold is the same defect as an ATR-derived stop.

**About one chart in six is deliberately one of these.** The read is right and the price is wrong, which is the situation Brooks states the arithmetic for and the one a real trader meets constantly. On those, *stand aside* is the graded answer in the take-or-pass drill, the feedback shows the equation that makes it so, and the chart header says so outright. Naming a setup and taking it are two different skills.

The reversals now look the way Brooks describes them rather than the way an unambitious target made them look: **around 40% win rates at 2.2–2.6R**, which is a positive equation built out of reward rather than frequency. Before this they showed 54–60% at around 1R — flattering win rates bought by targets so close that the trade could not pay, and in the worst case a 0.36R "major trend reversal" whose own numbers came to −0.18R per trade.

## First entry, second entry, second leg

"Second entry" means the second **bar-count** attempt — the High 2. "Second leg" means the second **leg** of the correction. They are different lenses on the same pullback and they usually disagree: a correction that takes fifteen bars and two legs will often read High 3 or High 4. In a two-legged pullback they land on the same bar, because the H2 that triggers you comes off the second leg — which is why Brooks treats it as the cleanest second-entry setup there is.

**A strong trend bar ends the count.** Brooks on a breakout like that: *"the count goes to zero."* The flag is over, and whatever pullback comes next starts again at one. Counting straight through a breakout bar is how a correction that has already resolved keeps accumulating a High 3, High 4, High 5. The decision bar is exempt — it is the setup bar, not a breakout that ends anything.

**A wedge is a High 3.** *"All wedge bottoms and wedge bull flags are High 3 buy set-ups; all wedge tops and wedge bear flags are Low 3 sell set-ups."* The three pushes **are** the count in a wedge, so the app counts them as such rather than running a bar-by-bar count through the pattern and reporting something else.

**The attempts are drawn on the chart.** "Is this really the second entry?" cannot be answered from a chart that never shows which attempt each bar is — the counts existed only in the text snapshot. On reveal, every counted attempt is numbered on the bar it happened on, the first two carry the trigger they would have given you with **no stop and no target** (they are not the trade, they are what the trade is measured against), and the bar you actually enter on is bracketed and named — `SIGNAL BAR · H2`, or `L4` when that is what the bars say.

That last case is worth expecting rather than being surprised by. The rule is about the first two attempts; the count is what the bars did. A correction that grinds on for twenty bars routinely reads H3 or H4 by the time it triggers, and Brooks' point is that the higher the count runs the weaker the trend's grip. Both numbers are right and they disagree — so the app now states which attempt the entry bar is instead of leaving it to be inferred.

Which one you take is a property of the market, not of the pattern's name. It is one-sided in a spike, a tight channel and a breakout that has earned belief, so the first attempt is the trade. It is two-sided in a range, at a failed breakout, in every reversal — and in a broad channel, which is a trading range on a tilt — so the first attempt usually fails, traps the traders who took it, and that failure is what fuels the second.

Where the tape gave no second attempt at all the feedback says so, because that silence is the argument: in a tight channel, waiting for a second entry means no trade at all on roughly half the charts here.

## What a major trend reversal has to contain

Brooks is specific and the app now draws all of it: a real bear trend — a sequence of lower highs and lower lows, not one big bar — then a countertrend move strong enough to break the trendline drawn along those lower highs, then a test of the old low that holds. **The test comes as a higher low or as a lower low that reverses, about equally often**, so both are drawn and the chart labels whichever one it actually is. A lower-low test is the failed breakout of the trend's own low and is the stronger version of the pattern.

That sequence is the whole difference from a failed breakout, which happens inside a trading range where there was never a trend to reverse. The two are drilled head to head for exactly that reason.

**The trendline is drawn.** Brooks' line runs along the trend's own lower highs, so the break is found on the bars — the line is extended forward and the break is the first bar that actually clears it. If nothing clears it there was no trendline break, and the chart is not used. The line is drawn on the chart with the bar that broke it, so the claim can be checked rather than taken on trust; it fades with the tier like everything else, and comes back on any answer you get wrong.

**And the test has to be a test.** A lower-low test is a *failed* attempt to resume the trend — bears trapped a little below the old low. It is capped at 0.8 ATR and 15% of the trend's height; deeper than that the trend simply continued and there is no setup yet, whatever the shape looks like afterwards.
