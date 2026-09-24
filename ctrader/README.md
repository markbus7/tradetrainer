# Trade Trainer Reader — cTrader cBot

The "Reads the chart" Coach from Trade Trainer, ported to a cTrader cBot. It finds the
11 Al Brooks setups the app teaches (bull and bear) from closed bars only, and trades
them the way the app's auto-backtest does.

The C# engine was checked against the app on 24 generated charts (8 markets, 3
timeframes): all 1,709 setups identical — same setup, same bar, same entry, stop and target.

## Install
1. cTrader → **Algo** → **New cBot**, delete the template, paste all of `TradeTrainerReader.cs`, **Build**.
2. Add an instance per chart you want traded (symbol + timeframe), e.g. XAUUSD, US100.cash,
   EURUSD, USDJPY, GER40.cash, US500.cash, UKOIL.cash on 15m.
3. BTCUSD on weekends only: set **Days to open trades** = `Weekends_only`.

## Parameters
- **Risk per trade** — % of equity at risk if the stop is hit (default 1%).
- **Skip if spread >** — no new trade while the spread is wider than this share of ATR.
- **Cancel unfilled order after** — bars (default 6, as in the app).
- **Setups** — switch individual setups on or off.

## Test before trading
Backtest on several years of real data with your broker's spread and commission.
When the backtest stops, the log prints a per-setup report (trades, win rate, average R,
costs included). The rules were tuned on the app's generated charts and have never been
tested on real markets. Then run it on a demo account before any real money.
