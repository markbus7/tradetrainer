# Trade Trainer

A self-contained trade training app that takes you from wherever you currently are up to a certified "Master" tier — measured moves, stop placement, targets, position sizing, and live bar-by-bar execution against generated (but structurally honest) charts.

Open `trade-trainer.html` directly in a browser — no build step, no server required.

## What's inside

- **Market engine** — seeded chart generator with volatility clustering, fat-tailed shocks, wick sweeps at pivots, and volume expansion on breakouts. 19 chart structures (flags, triangles, ranges, double tops/bottoms, head & shoulders, cup & handle, wedges, AB=CD, plus deliberate chop), each carrying its own ground truth (break level, invalidation, measured target) so every drill can be graded.
- **Chart tools** — candles, volume, EMAs, pan/zoom/pinch, a measured-move projector (A → B → C), and a draggable long/short position tool with live reward-to-risk.
- **Curriculum** — 8 tiers, 30 drill nodes, 13 drill kinds: structure marking, pattern ID, measured-move projection, stop/target placement, full trade plans, sizing & expectancy maths, take-or-pass discipline, and live execution against a fill simulator.
- **Progress** — a placement test sets your starting tier; progress, skill mastery, and a simulated trade log persist to `localStorage`.

Everything runs client-side in a single HTML file.
