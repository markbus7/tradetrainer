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

## Passing standard

A node is passed only when a **single session** clears both the pass mark (85% by default) and a required run of consecutive clean answers. An average alone can hide a skill you only half have, so the streak is what removes the doubt. Mastery certification requires 92% with an 8-answer streak.

## Fidelity notes

The teaching text aims to be Brooks' method rather than a paraphrase of it, and a few points are worth stating explicitly because they are commonly got wrong:

- **Bar counting is a bar count, not a leg count.** Following the Brooks Trading Course glossary: a High 1 is a bar with a high above the prior bar in a bull flag; if there is then a bar with a *lower high* (one or several bars later), the next bar in the correction whose high is above the prior bar's high is a High 2; third and fourth occurrences are a High 3 and 4. The re-arm condition is that single lower-high bar — **not** a new low for the pullback, and **not** a completed leg. The second leg does not have to trade below the first leg's low. The app computes the count off the actual bars with this rule, so the answer is whatever the tape shows — sometimes a High 1, sometimes a High 3.
- **Initial risk and actual risk are different numbers.** The protective stop goes one tick beyond the signal bar, and entry-to-stop is the *initial risk* — what you size from, and what belongs in the trader's equation, since you don't tighten a stop just because the market goes sideways after entry. The *actual risk* is entry to the extreme of the pullback that happens after you're filled; it is knowable only in hindsight and is usually far smaller. Every stop question reports both, measured off the tape. A stop beyond the structure is a defensible wider alternative — just a bigger stop, not a separately-named concept.
- **Entry side matters.** With the trend you enter on a stop beyond the signal bar so price has to prove itself; in a range you fade the edge with a limit. Getting this backwards turns a correct read into a losing trade, so it is drilled and graded separately.
