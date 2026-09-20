# Strategy Research Plan

Planning document. No strategy execution is implemented from this document.

## 0. Honest framing

These are **ten falsifiable research templates for backtesting and paper
trading**, not trading recommendations and not strategies expected to be
profitable. Published evidence is mixed and strongly regime-dependent: one
2025 Bitcoin study reported favourable hourly Bollinger mean reversion
while a separate cost-aware hourly study found mean reversion unprofitable
and favoured trend following; another out-of-sample project found momentum
failing in its later sample and short-horizon reversal overwhelmed by
turnover costs. There is no identified universal winner.

Transaction costs, slippage, overfitting, liquidity, and regime change can
turn an apparently successful backtest into a losing live strategy. The
platform's job is to **reject** configurations, not to find a winner.

**The platform must never claim a strategy guarantees or is expected to
produce profit, and must never automatically promote the highest-return
backtest to live trading.**

---

## 1. Timeframes versus time zones

These are distinct concepts and must not be conflated:

- **Timeframe** — 1m, 5m, 10m, 15m, 30m, 1h, 4h, 1d, optional derived 4d.
- **Time zone / session** — Asia, Europe, North America, weekend, UTC hour.
- **Regime timeframe** — the slower timeframe that decides whether the
  strategy may trade at all.
- **Signal timeframe** — the timeframe producing entry and exit decisions.
- **Execution timeframe** — the faster timeframe used to control entry
  quality.

Rules:

- Persist every timestamp in UTC. Define every experiment in UTC.
- Convert to local time only for display.
- Store the **IANA time-zone database version** with every seasonality
  experiment.
- Never hard-code a session such as "London open" to a fixed UTC hour;
  daylight-saving transitions change the mapping.
- Test time-of-day filters independently on training, validation, holdout,
  and walk-forward periods.
- Kraken provides native 1m, 5m, 15m, 30m, 1h, 4h and 1d klines. **10m is
  derived from ten closed 1m candles** and marked derived. If 4d is
  implemented it is derived from four closed 1d candles using one
  documented UTC boundary.

---

## 2. Rules common to all ten templates

These rules matter more than any individual indicator.

### 2.1 Instrument universe

Eligibility is governed entirely by `docs/market-universe.md`. A strategy
may evaluate an instrument only when trading is enabled by the connector,
the quote asset is on the administrator allowlist, sufficient gap-free
history exists, liquidity and spread requirements pass, exchange filters
are loaded, the instrument is not newly listed (unless the strategy is
specifically approved for that condition), and no operational block
applies.

Research starts with highly liquid pairs. Expanding immediately across
every listed token creates survivorship, delisting, liquidity, and
execution-quality problems.

### 2.2 Execution assumptions every test must model

Maker and taker fee scenarios; bid-ask spread; adverse slippage;
signal-to-order latency; quantity step; price tick; minimum quantity;
minimum notional; rejected orders; partial fills where modelled; funding
for futures; margin costs where applicable; unknown order outcomes; missed
candles and stale data.

### 2.3 Rejection gates

A strategy configuration is **rejected** when any of the following holds:

- It wins only on training data.
- A small increase in assumed fees or slippage destroys it.
- Most gains come from one instrument or one trade.
- Neighbouring parameters fail badly (no stable plateau).
- It has too few independent trades to evaluate.
- It requires unrealistic fills.
- It performs only on currently surviving coins.
- It fails forward paper trading.
- Drawdown breaches the approved research threshold.
- It loses its behaviour across walk-forward windows.
- A simpler benchmark performs similarly after costs.

**Do not select whichever of the ten workers has the highest historical
return.** That is an optimization-selection trap and is explicitly
forbidden.

### 2.4 Position sizing

Size from allowed risk and a volatility measure (e.g. ATR distance), never
from a fixed coin quantity. Agreement between multiple models may raise
exposure only within the same platform maximum — never multiply leverage
because several models agree.

### 2.5 Prohibited behaviours

No martingale. No unrestricted averaging down. No automatic leverage
increase. No exposure increase when data is stale. Entries only after a
closed candle.

---

## 3. Required specification per strategy family

The approved template record for each family must define:

research hypothesis; supported product types; supported instruments;
regime requirements; regime timeframe; signal timeframe; execution
timeframe; required historical warm-up; parameter definitions; default
research ranges; entry conditions; exit conditions; invalidation
conditions; position-sizing interface; fee, spread, slippage and latency
assumptions; stale-data behaviour; expected weaknesses; rejection
criteria; required unit tests; required backtests; paper-trading
eligibility criteria; live-approval status.

---

## 4. The ten approved research families

### 4.1 Multi-timeframe EMA trend continuation
**Hypothesis:** when a higher timeframe has an established directional
trend, a lower-timeframe pullback followed by renewed momentum may offer a
better entry than an unfiltered crossover.

| Profile | Regime | Signal | Execution |
|---|---|---|---|
| Fast | 1h | 5m | 1m |
| Intraday | 4h | 15m | 5m |
| Swing | 1d | 1h | 15m |
| Slow swing | 1d / derived 4d | 4h | 1h |

**Long conditions:** HTF fast EMA above slow EMA; HTF slow-EMA slope
positive; price above HTF slow EMA; signal timeframe pulls toward its
medium EMA without invalidating the higher trend; signal closes back above
its fast EMA; volume not below the approved minimum relative to its
rolling baseline; spread, liquidity and stale-data gates pass; entry only
after a closed signal candle.

**Exits:** signal close below medium EMA; HTF trend invalidation; ATR
trailing exit; maximum holding timeout; risk-engine exit; data-health
transition to an unsafe state.

**Parameters (families, not independent integers):** fast EMA 8–30; medium
EMA 20–80; slow EMA 50–250; ATR lookback 10–30; ATR exit distance
(bounded); minimum volume ratio; maximum spread; cooldown candles. Prefer
stable plateaus over point optima.

**Spot:** long/flat only initially. Short behaviour is researched
separately under futures.

**Weaknesses:** repeated false crossings in sideways markets; high
turnover in fast variants; late entry near trend exhaustion.

### 4.2 Donchian breakout ensemble
**Hypothesis:** sustained trends may be captured by breakouts beyond
recent ranges; several channel lengths reduce dependence on one lookback.

**Timeframes:** 15m signal / 4h regime; 1h signal / 1d regime; 4h signal /
1d or derived 4d regime. Avoid 1m initially (noise and cost).

**Long:** HTF trend filter positive; closed candle closes above the prior
Donchian upper channel; breakout volume passes the filter; ATR neither
extremely low nor above the approved ceiling; spread and liquidity pass;
no entry if price has already moved an excessive ATR distance beyond the
channel; size from allowed risk and ATR distance.

**Exits:** close below the shorter Donchian exit channel; ATR trailing
stop; HTF regime reversal; risk limit; close-only transition.

**Ensemble:** three independent channel families (short, medium, long)
producing a normalized consensus — zero agreement: no position; one model:
reduced research exposure; two: normal research exposure; three: still
capped by the same platform maximum.

**Weaknesses:** failed breakouts; gap-like liquidation moves; entries
after an extended move; churn in ranges.

### 4.3 Bollinger mean reversion in a ranging regime
**Hypothesis:** in a demonstrably range-bound market, movement beyond a
volatility envelope followed by re-entry may revert toward the rolling
centre. Evidence is conflicting, so this family is enabled **only after an
explicit ranging-regime test passes**.

**Timeframes:** 5m/1h, 15m/4h, 1h/1d. Do not start with 1m.

**Ranging filter (all configurable):** low absolute slope of the HTF
moving average; ADX below a tested threshold if implemented; price
oscillating around its medium average; no HTF breakout; volatility below a
crisis threshold; no stale or incomplete data.

**Long setup:** regime is range-bound; price closes below the lower band;
RSI below its configured lower region; the following candle closes back
inside the band; spread and liquidity pass; enter on the next permitted
execution event.

**Exits:** middle band; upper band (secondary variant); maximum holding
period; range invalidation; volatility shock; risk stop.

**Critical restriction:** never keep buying merely because price keeps
falling. One entry, or a tightly bounded approved staged-entry model. **No
unrestricted averaging down.**

**Weaknesses:** a range turning into a trend; costs erasing small
reversion gains; excessive turnover on low timeframes.

### 4.4 RSI pullback within a higher-timeframe trend
**Hypothesis:** RSI is a pullback-timing tool inside an established trend,
not a signal to buy every oversold reading.

**Timeframes:** 1h/5m/1m; 4h/15m/5m; 1d/1h/15m.

**Long:** HTF price above its slow EMA; HTF fast EMA above slow EMA;
signal RSI drops into a configurable pullback zone; price remains above
the HTF structural invalidation level; RSI turns up and the signal candle
closes above its previous close or a short EMA; volume and spread gates
pass; entry only after candle closure.

**Exits:** previous swing structure fails; RSI reaches the upper exit
region; close below signal medium EMA; ATR trailing exit; HTF trend
invalidation.

**Distinction from 4.3:** 4.3 expects a range, 4.4 expects continuation.
The same RSI reading has opposite meaning in different regimes.

**Weaknesses:** strong trends may not pull back enough; weak trends can
look established just before reversal; thresholds vary by asset and
timeframe.

### 4.5 MACD and volume-confirmed trend acceleration
**Hypothesis:** a trend may be more credible when momentum acceleration
and volume participation agree.

**Timeframes:** 15m/4h; 1h/1d; 4h/1d or 4d.

**Long:** HTF trend positive; MACD line crosses above its signal line;
histogram turns from declining to rising or crosses positive (variant
dependent); price above an approved trend average; volume exceeds the
rolling baseline; volatility within permitted bounds; scanner liquidity
and spread gates pass.

**Exits:** MACD reverse crossing; histogram deterioration for N closed
candles; trend-average failure; ATR trailing exit; HTF reversal.

**Variants** (early / standard / conservative) share a strategy identifier
but each requires a **separately approved version**.

**Weaknesses:** MACD lag; volume spikes near exhaustion; excessive
turnover with fast parameters.

### 4.6 Volatility-compression breakout
**Hypothesis:** declining realized volatility may precede directional
expansion. Direction comes from the breakout, never from predicting it.

**Timeframes:** 5m/1h; 15m/4h; 1h/1d; 4h/1d or 4d.

**Compression definitions (single or ensemble):** Bollinger bandwidth in a
low historical percentile; ATR relative to price in a low percentile;
narrow Donchian range; consecutive inside-range behaviour.

**Entry:** compression persists for a minimum number of completed candles;
price closes outside the compression boundary; volume expands relative to
baseline; breakout distance not excessively extended; liquidity and spread
pass; regime filter does not strongly oppose the direction; closed-candle
confirmation only.

**Exits:** return inside the compression range; ATR trailing stop;
opposite breakout; time-based failure if expansion never follows; risk
stop.

**Weaknesses:** false breakouts; news spikes with poor fills; slippage
precisely when the signal triggers; compression persisting far longer than
expected.

### 4.7 Cross-sectional momentum rotation
**Hypothesis:** within a liquid eligible universe, assets with stronger
medium-term relative performance may continue outperforming for a period.
A 2024 factor study across 31 prominent cryptocurrencies reported
predictive power for momentum and value in its dataset; a later public
project reported its momentum result failing out of sample. Treat this as
a portfolio-level hypothesis, not a trade-level certainty.

**Interval:** daily ranking, 4h monitoring, weekly-or-slower rebalance
candidate. Not a 1m strategy.

**Process:** build a **survivorship-aware** universe available *at each
historical timestamp*; exclude illiquid, newly listed, blocked or
incomplete instruments; compute medium-horizon returns at a fixed UTC
evaluation time; adjust ranking for realized volatility; apply an absolute
trend filter so a "least bad loser" is never bought; select a capped
number of highest qualifying assets; weight by bounded inverse volatility
or another approved allocation policy; rebalance only when rank movement
exceeds a buffer; enforce turnover and concentration limits.

**Exits:** falls below the buffered rank threshold; absolute trend turns
negative; liquidity deteriorates; instrument becomes ineligible; portfolio
risk ceiling reached.

**Weaknesses:** survivorship and listing bias; high turnover; momentum
crashes; concentration in highly correlated altcoins.

### 4.8 Relative-strength pullback rotation
**Hypothesis:** rather than buying the strongest instrument immediately
after acceleration, wait for a controlled pullback while its relative
ranking remains strong.

**Timeframes:** 1d selection, 4h setup, 1h execution.

**Rules:** instrument is in the top qualifying relative-strength group;
daily absolute trend positive; 4h price pulls back toward an approved
trend average; 4h RSI cools without entering a structural downtrend state;
1h candle confirms renewed direction; liquidity and spread pass; portfolio
correlation and concentration limits pass.

**Exits:** loses relative-strength rank; daily trend invalid; ATR trailing
exit; maximum holding period; portfolio rebalance.

**Weaknesses:** rankings reverse abruptly; several selected assets may be
one hidden Bitcoin-risk position; delayed entry can miss continuation.

### 4.9 Session-conditioned breakout
**Hypothesis:** trend and breakout behaviour may vary by UTC hour,
weekday, or overlap between major regions. **Session information modifies
an existing strategy; it never creates a trade by itself.**

**`SessionProfile`:** `IanaTimeZone`, `LocalStartTime`, `LocalEndTime`,
`AllowedWeekdays`, `DaylightSavingPolicy`, `MinimumLiquidity`,
`MaximumSpread`, `StrategyMode`. Versioned, with the IANA database version
recorded.

**Variants to test:** no session filter (the baseline); Asia business
hours; Europe business hours; Europe/North America overlap; weekend;
per-UTC-hour; per-weekday.

**Do not assume a named session is superior.** The system must discover
whether a filter improves the strategy in an untouched evaluation period.

**Candidate:** the Donchian or compression breakout, with entry allowed
only when the session profile is active, current spread and realized
liquidity pass, the session model has enough historical observations, the
filter improved validation performance *after costs*, the improvement
persists in walk-forward testing, and the filter is approved for the
instrument class.

**Weaknesses:** time-zone and daylight-saving mistakes; multiple-testing
bias from trying every hour; patterns disappearing after discovery; macro
releases causing extreme slippage; weekend market structure differing from
weekdays.

### 4.10 Deterministic regime-switching ensemble
**Hypothesis:** no single family dominates trend, range, calm and crisis
regimes. A deterministic classifier enables only the strategies suited to
the current state. This does **not** "learn the winning strategy"; it
prevents incompatible strategies from running simultaneously.

**Regimes:** trending up; trending down; ranging; volatility compression;
volatility expansion; crisis/dislocation; unknown or insufficient data.

**Classifier inputs:** 1d trend slope; 4h trend slope; 1h realized
volatility; ATR percentile; Bollinger bandwidth percentile; trend-strength
measure; market breadth (share of eligible universe above a long-term
average); BTC market trend; data-health state.

| Regime | Eligible research families |
|---|---|
| Trending up | EMA continuation, Donchian, RSI pullback, rotation |
| Trending down | Flat for Spot; approved short trend strategies for Futures only |
| Ranging | Bollinger mean reversion |
| Compression | Compression breakout, only on confirmed exit |
| Expansion | Hold existing trend positions; reduced new-entry allowance |
| Crisis | Close-only or no new exposure |
| Unknown | No new exposure |

**Anti-overfitting rule:** the classifier must be simple, versioned, and
based **only on information available at the event timestamp**. An
optimization process must never be permitted to redefine regimes until
historical performance looks attractive.

**Weaknesses:** delayed transitions; misclassification disabling the
useful strategy; an overly complex classifier becoming another overfit
strategy; turnover from frequent switching.

---

## 5. Strategy approval lifecycle

```
Draft
 -> BacktestApproved
 -> PaperApproved
 -> SpotTestApproved
 -> SpotLiveApproved
 -> FuturesTestApproved
 -> FuturesLiveApproved
 -> Suspended
 -> Deprecated
```

- **All strategies begin as `Draft`.**
- Each transition requires the rejection gates of §2.3 to pass and an
  explicit, audited human approval. No transition is automatic.
- `SpotLiveApproved` requires working market data, backtesting, paper
  trading, risk controls, testnet trading, idempotency and reconciliation.
- `FuturesLiveApproved` requires Spot live to be stable first.
- Any strategy may be moved to `Suspended` immediately by an operator; the
  strategy halt scope takes effect without waiting for approval workflow.
- Approvals are immutable and versioned; a parameter-range change creates
  a new version requiring re-approval.

---

## 6. Evaluation requirements

Every strategy must be evaluated using: training data; validation data;
walk-forward windows; **one** untouched holdout evaluation; forward paper
trading; realistic fee assumptions; spread and slippage stress; latency
stress; missing-data stress; parameter-neighbourhood stability;
instrument-level attribution; regime-level attribution.

The backtesting design must prevent: look-ahead bias; survivorship bias;
use of currently listed symbols for historical universes; optimization
against untouched holdout data; repeated holdout evaluation; selection by
total profit alone; unrealistic fills; silent omission of delisted
instruments; uncontrolled multiple testing.

---

## 7. Ten-worker experiment arrangement

Workers are **reusable experiment groups**, not permanent one-strategy
slots. Do not assign one worker per strategy and then pick the historical
winner.

- **Group A — strategy-family comparison.** All ten families (§4.1–4.10)
  against the same dataset split, fee policy, slippage assumptions,
  capital, and eligible universe.
- **Group B — timeframe robustness.** One family across 1m, 5m, 15m, 30m,
  1h, 4h, 1d, 1h/5m, 4h/15m, 1d/1h. Note: more timeframes do **not** mean
  more independent evidence; many derive from the same price series.
- **Group C — cost stress.** One configuration against base costs, higher
  fees, higher slippage, delayed execution, wider spread, partial fills,
  missed signal candle, temporary disconnection, reduced liquidity, and a
  combined stress scenario. **A strategy surviving only the optimistic
  model is not eligible for paper trading.**

---

## 8. Research priority order

A design recommendation only — **not** a claim that the first will earn
the most:

1. Donchian breakout ensemble
2. Multi-timeframe EMA continuation
3. RSI pullback in trend
4. Volatility-compression breakout
5. Regime-switching ensemble
6. MACD acceleration
7. Bollinger mean reversion
8. Relative-strength pullback
9. Cross-sectional momentum
10. Session-conditioned variants

Trend following has supportive research, but transaction costs and regime
dependence remain major concerns, and mean-reversion evidence is
particularly conflicting.

**The correct first mode for every strategy is historical test, then
forward paper trading. None starts approved for live Spot or Futures.**

---

## 9. Required tests

1. Every template is created in `Draft` and is not paper- or live-approved.
2. An approval transition without a recorded human approver is rejected.
3. A parameter set outside the approved range is rejected.
4. Signals are generated only from closed candles; an unclosed candle
   produces no decision.
5. Derived 10m candles are built only from ten closed 1m candles and are
   marked derived; a derived 4d candle uses one documented UTC boundary.
6. Session filters resolve via IANA identifiers across a daylight-saving
   transition without a hard-coded UTC hour.
7. The session-filtered variant is always evaluated against the
   no-session baseline.
8. The regime classifier uses only data available at the event timestamp
   (no future leakage), and is deterministic and versioned.
9. Mean reversion cannot place a second entry while the first is open
   beyond the bounded staged-entry model (no unrestricted averaging down).
10. Ensemble agreement never raises exposure above the platform maximum.
11. Cross-sectional ranking uses the universe as it existed at each
    historical timestamp (survivorship test with a delisted instrument).
12. Each rejection gate in §2.3 rejects a deliberately constructed
    failing configuration.
13. A configuration that passes only the optimistic cost model is denied
    paper-trading eligibility.
14. Holdout data can be evaluated once; a second evaluation is refused.
15. Stale data blocks new and exposure-increasing decisions for every
    family.
16. No strategy output, UI text, or report claims expected or guaranteed
    profit.

---

## 10. Acceptance criteria

1. All ten families are documented with the full §3 specification.
2. All begin as `Draft`; none is paper- or live-approved.
3. Approval transitions are audited, versioned, and human-gated.
4. Evaluation uses train/validation/walk-forward/untouched-holdout plus
   forward paper trading, with cost and stress scenarios.
5. Selection by highest historical return is impossible by design.
6. No profit claim appears anywhere in the product.
7. No strategy execution code is written as part of this planning task.

---

## 11. Explicitly excluded

- Implementing strategy execution or signal generation.
- Any live or proving-stage order placement.
- Automatic promotion of any strategy to any approval state.
- Any withdrawal capability (permanently out of scope).
