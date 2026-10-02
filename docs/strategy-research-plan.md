# Strategy Research Plan

The paper runtime implements these ten approved strategy families and their
five-check consensus thresholds. This remains a research specification, not a
claim of profitability or live-trading approval.

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

- **Timeframe** — 1m, 5m, 10m, 15m, 30m, 1h, 4h, 1d.
- **Time zone / session** — Asia, Europe, North America, weekend, UTC hour.
- **Regime timeframe** — the slower timeframe that decides whether the
  strategy may trade at all.
- **Signal timeframe** — the timeframe producing entry and exit decisions.
- **Execution timeframe** — the closed-candle interval on which an admitted
  entry is modeled. In this initial design it always equals the signal
  timeframe. A future faster execution profile is a new strategy version and
  must never be confused with the separate 1-minute position manager.

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
  derived from ten closed 1m candles** and marked derived.

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
| Fast | 1h | 5m | 5m |
| Intraday | 4h | 15m | 15m |
| Swing | 1d | 1h | 1h |
| Slow swing | 1d | 4h | 4h |

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
1d regime. Avoid 1m initially (noise and cost).

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

**Timeframes:** 1h/5m/5m; 4h/15m/15m; 1d/1h/1h. The final value is
execution and equals signal in this initial design.

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

**Timeframes:** 15m/4h; 1h/1d; 4h/1d.

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

**Timeframes:** 5m/1h; 15m/4h; 1h/1d; 4h/1d.

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

## 5. Normative strategy contracts

Sections 0-4 explain the research hypotheses. This section is the normative
contract for future implementation. Exactly these ten stable identities are in
scope. Similar existing experimental adapters are not silently grandfathered
into this catalogue.

### 5.1 Shared contract

**Versioning.** Every approved record has a stable id, semantic version, content
fingerprint, parameter-schema version, timeframe-profile version, and cost-model
version. Changing a check, threshold, veto, indicator, range, universe,
timeframe, session, exit, or cost assumption creates a new version and requires
new evidence.

**Support.** Initial support is Kraken Spot, EUR-quoted `Cryptocurrency` bases,
and paper mode only. Futures, short entry, margin, leverage, and live execution
are `NotApproved`. Pair eligibility is point-in-time and computed per strategy
version and profile; no hard-coded pair is presumed profitable.
The normalized interval catalogue supports native 1m, 5m, 15m, 30m, 1h, 4h
and 1d plus derived 10m. A family may use only the profiles listed in its
current contract; adding a 10m, 30m, or other profile creates a new version and
requires complete approval evidence.
The already implemented derived 4-day market-data capability is retained but
is not an approved interval in this ten-family catalogue.

**Five-channel result.** Each family emits exactly five independently
explainable channels. A channel is `Bullish`, `Bearish`, or `Neutral`, with
source candle identities and measured decimal values. Unless a contract below
is stricter:

- `BUY` requires at least four bullish channels and no mandatory veto.
- `SELL` requires at least four bearish channels or a mandatory exit.
- `HOLD` applies otherwise.
- Spot `SELL` is reduce-only or full close of an existing long. It never opens
  a short.
- Entries use the next permissible event after the signal candle closes.

**Mandatory vetoes.** All families inherit the vetoes in
`continuous-paper-scanner.md` section 10. Vetoes override all votes. Missing,
unsafe, stale, future, or insufficient evidence fails closed. Session filters
use versioned IANA profiles. All calculations use `decimal` and UTC.

**Sizing interface.** A strategy produces direction, entry reference,
invalidation/protective-stop reference, optional target, maximum holding
instant/candles, and evidence. It never chooses quantity or an adapter. The risk
pipeline receives equity, cash, exposure, volatility, normalized Kraken
filters, fee/spread/slippage estimates, platform/user ceilings and correlation
state, then either returns an exact conservatively rounded paper quantity or a
typed rejection. Agreement never raises exposure above a ceiling.

**Costs.** Every profile records taker and maker scenarios, bid-ask spread,
adverse slippage, signal-to-fill latency, rejections, missed fills, and partial
fills where supported. Approval includes baseline and adverse cost scenarios;
no zero-cost result qualifies.

### 5.2 `MTF_EMA_CONTINUATION` version `1.0.0`

**Hypothesis:** a renewed lower-timeframe move after a controlled pullback in an
established higher-timeframe trend may outperform chasing the initial move.

| Field | Contract |
|---|---|
| Profiles | `1h/5m/5m`, `4h/15m/15m`, `1d/1h/1h`, `1d/4h/4h` as regime/signal/execution |
| Indicators | Regime EMA 50/200, signal EMA 20/50, ATR 14, volume SMA 20; optional ADX is a new version |
| Warm-up | At least 201 regime and 51 signal closed candles plus ATR/volume warm-up |
| Parameters | Regime EMA 40-80 and 150-250; signal EMA 10-30 and 30-80; ATR 10-30; stop 1.5-3.5 ATR; volume ratio 1.0-1.5; cooldown 2-10 |
| Maximum hold | Versioned per profile; initial research range 8-80 signal candles |
| Expected strength | Enters established trends after a pullback rather than chasing the first break |

Five channels:

1. Regime close versus EMA 200.
2. Regime EMA 50 versus EMA 200.
3. Regime EMA 50 slope over the approved lookback.
4. Controlled signal pullback to EMA 20/50 without structural invalidation.
5. Closed resumption through EMA 20 with approved volume.

BUY is four bullish channels. Bearish channel values are the exact directional
inverse; four bearish channels close a Spot long. Entry is rejected when ATR is
outside approved percentiles or the candle is excessively extended. Exit
priority is hard stop, safety/reconciliation exit, regime invalidation, signal
close beyond EMA 50, ATR trail, then maximum hold. Primary failure modes are
sideways whipsaw and late trend exhaustion. Required tests cover slope
boundaries, pullback/resumption ordering, no same-candle look-ahead, profile
alignment, and ranging-regime rejection.

### 5.3 `DONCHIAN_ENSEMBLE` version `1.0.0`

**Hypothesis:** range breakouts supported by longer-channel direction, regime,
and participation may identify sustained expansion.

| Field | Contract |
|---|---|
| Profiles | `4h/15m/15m`, `1d/1h/1h`, `1d/4h/4h`; 1m signals unsupported |
| Indicators | Prior Donchian 20/55/100, EMA 200, ATR 14, volume SMA 20 |
| Warm-up | At least 201 regime and 101 signal candles |
| Parameters | entry channels fixed by version; exit channel 10-30; stop 2.0-4.0 ATR; volume 1.1-2.0; maximum extension 0.5-2.0 ATR |
| Maximum hold | 14-120 signal candles by profile |
| Expected strength | Captures persistent range expansion with multi-horizon confirmation |

Five channels:

1. Regime close versus EMA 200.
2. Close beyond the prior closed Donchian 20 boundary.
3. Donchian 55 or 100 boundary direction/break.
4. Breakout volume versus rolling baseline.
5. Breakout extension inside the approved ATR limit.

Four bullish channels enter; channel boundaries invert for an exit vote. Three
is watch-only. Channel values always come from prior closed candles. Exit
priority is hard stop, safety exit, failed breakout back inside the channel,
short exit-channel break, higher-regime reversal, ATR trail, then maximum hold.
Main failures are false breakouts and range churn. Tests pin prior-candle
channels, extension rejection, multiple-channel agreement without leverage,
and no 1m profile.

### 5.4 `BB_RSI_RANGE_REVERSION` version `1.0.0`

**Hypothesis:** after a range is independently established, an extreme followed
by a closed return inside the envelope may revert toward its centre.

| Field | Contract |
|---|---|
| Profiles | `4h/15m/15m`, `1d/1h/1h`; 1m signals unsupported |
| Indicators | Bollinger 20/2, RSI 14, ADX 14, EMA 50 slope, ATR percentile |
| Warm-up | At least 101 regime and 31 signal candles |
| Parameters | Bollinger 15-30; deviation 1.8-2.5; RSI lower 20-35; RSI exit 45-70; stop 1.0-2.5 ATR; hold 8-80 |
| Add policy | No averaging down; initial version permits one opening fill only |
| Expected strength | Restricts mean reversion to independently classified ranges and requires re-entry confirmation |

Five channels:

1. ADX below the approved range threshold.
2. Absolute EMA 50 slope below the approved range threshold.
3. A prior closed candle beyond the lower/upper band.
4. RSI in the approved corresponding extreme.
5. A later closed trigger candle back inside the band.

BUY requires four bullish values including channel 5; bearish values reduce or
close a Spot long and never short. Entry solely because price remains outside a
band is forbidden. Exit priority is hard stop, safety exit, strong bearish trend
invalidation, middle-band target, optional versioned upper-band target, then
maximum hold. Main failure is a range becoming a downtrend. Tests cover
two-candle ordering, regime loss, repeated-extreme no-add behavior, and
cost-sensitive small targets.

### 5.5 `RSI_TREND_PULLBACK` version `1.0.0`

**Hypothesis:** RSI can time a controlled pullback inside an independently
established trend, but an oversold reading alone has no entry meaning.

| Field | Contract |
|---|---|
| Profiles | `1h/5m/5m`, `4h/15m/15m`, `1d/1h/1h` |
| Indicators | Regime EMA 50/200, signal EMA 20, RSI 14, ATR 14, volume SMA 20 |
| Warm-up | At least 201 regime and 31 signal candles |
| Parameters | pullback RSI 30-45; turn 1-3 candles; exit RSI 60-80; EMA 10-30; stop 1.5-3.0 ATR |
| Maximum hold | 8-80 signal candles |
| Expected strength | Separates a turning pullback from an RSI reading that is still deteriorating |

Five channels:

1. Regime close above/below EMA 200.
2. Regime EMA 50 above/below EMA 200.
3. Price holds/breaks higher-timeframe structural invalidation.
4. RSI enters the pullback zone and then turns in the position direction.
5. Closed price confirmation through EMA 20 or prior high/low with acceptable
   volume.

Four bullish channels enter only after the RSI turn. Four bearish channels
close. Oversized reversal candles and inadequate reward-to-risk are vetoed.
Exit priority is hard stop, safety exit, structural break, EMA failure, upper
RSI target, ATR trail, regime reversal, then maximum hold. Main failure is
misclassifying reversal as pullback. Tests cover falling-RSI rejection,
turn-window bounds, structural invalidation, and timeframe alignment.

### 5.6 `MACD_VOLUME_ACCELERATION` version `1.0.0`

**Hypothesis:** direction, momentum acceleration, price structure, and
participation together may filter late or weak MACD crosses.

| Field | Contract |
|---|---|
| Profiles | `4h/15m/15m`, `1d/1h/1h`, `1d/4h/4h` |
| Indicators | MACD 12/26/9, regime EMA 50/200, signal EMA 50, ATR 14, volume SMA 20 |
| Warm-up | At least 201 regime and 60 signal candles |
| Parameters | MACD fast 8-16, slow 20-35, signal 5-12; volume 1.0-1.8; stop 1.5-3.5 ATR |
| Variants | Early, standard, and conservative are separate semantic versions |
| Maximum hold | 8-100 signal candles by profile |
| Expected strength | Requires trend, momentum acceleration, price structure, and participation to agree |

Five channels:

1. Higher-timeframe EMA trend direction.
2. MACD line versus signal line with versioned cross recency.
3. Histogram sign/slope over the approved closed-candle window.
4. Price versus signal EMA 50.
5. Volume versus rolling baseline.

Four bullish channels enter; four bearish channels close. A variant cannot be
selected at runtime unless its version is approved. Exit priority is hard stop,
safety exit, MACD reverse cross, histogram deterioration, EMA failure, ATR
trail, regime failure, then maximum hold. Main failures are lag and exhaustion
volume. Tests cover exact crossover identity, histogram sequence, variant
fingerprints, and no mutable variant switch.

### 5.7 `VOLATILITY_COMPRESSION_BREAKOUT` version `1.0.0`

**Hypothesis:** persistent low volatility followed by a closed boundary break
with participation may identify a transition to expansion.

| Field | Contract |
|---|---|
| Profiles | `4h/15m/15m`, `1d/1h/1h`, `1d/4h/4h` |
| Indicators | Bollinger bandwidth, ATR/price, Donchian boundary, volume SMA 20, EMA regime filter |
| Warm-up | Compression lookback plus 51 regime candles; initial lookback 100-500 |
| Parameters | bandwidth percentile 5-30; persistence 3-20; Donchian 10-50; volume 1.2-2.0; stop 1.5-3.5 ATR |
| Maximum hold | 10-100 signal candles |
| Expected strength | Waits for measurable compression and closed expansion rather than anticipating a break |

Five channels:

1. Bandwidth is inside the approved low historical percentile.
2. ATR/price is inside the approved low historical percentile.
3. Compression persists for the minimum closed-candle count.
4. Price closes beyond the prior compression/Donchian boundary.
5. Volume expands above its rolling baseline.

Four bullish channels enter only when the regime does not oppose direction;
four bearish channels close. Excess extension, widened costs, or no longer
current compression veto entry. Exit priority is hard stop, safety exit, return
inside compression, opposite break, no-expansion timeout, ATR trail, regime
reversal, then maximum hold. Tests cover percentile training boundaries,
compression persistence, prior-boundary use, and false-break cost stress.

### 5.8 `CROSS_SECTIONAL_MOMENTUM` version `1.0.0`

**Hypothesis:** among a point-in-time eligible universe, diversified assets with
positive absolute and relative momentum may persist, after costs and turnover.

| Field | Contract |
|---|---|
| Profile | `1d` ranking, `4h` monitoring, slow versioned rebalance |
| Indicators | Medium/long return ranks, trend strength, liquidity rank, volatility and turnover penalties |
| Warm-up | Longest return horizon plus 200 daily candles for every universe member |
| Parameters | Horizon and weight ranges sum to one; top/exit rank buffers; volatility and turnover caps |
| Portfolio | Capped holdings, bounded inverse-volatility weights, pair/correlation limits |
| Maximum hold | Until the earlier of buffered rank exit, risk exit, or 1-90 daily candles |
| Expected strength | Compares assets cross-sectionally while requiring positive absolute trend and bounded diversification |

Five channels:

1. Top approved point-in-time momentum percentile.
2. Positive absolute return.
3. Daily close above the long-term trend average.
4. Liquidity, spread, history, and tradability pass.
5. Volatility and portfolio-correlation limits pass.

All five selection channels are required for BUY. Falling below the buffered
exit rank, negative absolute trend, liquidity loss, suspension, replacement, or
portfolio-risk exit produces SELL. Current constituents may not be backfilled
into history. Main failures are momentum crashes, survivorship bias, correlated
altcoin exposure, and turnover. Tests cover universe snapshots, delistings,
rank ties, weight bounds, buffers, and no holdout-ranked weights.

### 5.9 `RS_PULLBACK_ROTATION` version `1.0.0`

**Hypothesis:** selecting established relative leaders but waiting for a
controlled pullback may reduce late momentum entries.

| Field | Contract |
|---|---|
| Profile | `1d` selection, `4h` setup, `1h` signal/execution |
| Indicators | EUR and XBT-relative returns, universe-median return, volatility-adjusted rank, EMA 20/50, RSI 14, ATR 14 |
| Warm-up | Longest rank horizon plus 200 daily, 60 four-hour, and 31 hourly candles for every candidate |
| Parameters | Top/exit rank buffers, pullback depth, RSI cooling/turn, stop 1.5-3.0 ATR, hold 1-30 days |
| Portfolio | Correlation and concentration gates are mandatory |
| Maximum hold | 1-30 days, fixed by version |
| Expected strength | Avoids buying relative leaders at full extension by requiring a controlled pullback and renewal |

Five channels:

1. Asset remains in the top approved relative-strength group.
2. Daily absolute trend remains positive.
3. Four-hour pullback reaches EMA 20/50 within approved ATR depth.
4. Four-hour RSI cools without bearish structural invalidation.
5. One-hour closed candle confirms renewed upside.

All five channels are required for BUY. Buffered rank loss, daily invalidation,
four-hour structure break, ATR trail, maximum hold, or rebalance closes.
Relative strength cannot qualify merely because every asset fell more. Main
failure is entering after leadership peaks. Tests cover multi-benchmark ranks,
pullback depth, confirmation ordering, concentration, and stale-rank veto.

### 5.10 `SESSION_BREAKOUT` version `1.0.0`

**Hypothesis:** an independently valid breakout may have different execution
quality during a versioned session, but session alone predicts no direction.

| Field | Contract |
|---|---|
| Profiles | Base Donchian/compression profile plus versioned IANA session; stored times UTC |
| Indicators | Base breakout evidence, session membership, liquidity, spread/slippage, validation comparison |
| Warm-up | Base strategy warm-up plus sufficient observations in every compared session bucket |
| Parameters | Session profile, overlap/weekend rule, minimum liquidity, maximum costs, ending behavior |
| Baseline | Every version must beat its identical no-session baseline under predeclared gates |
| Maximum hold | Inherited from the immutable base strategy; session-end behavior is separately versioned |
| Expected strength | Uses session only to improve execution quality for an already valid breakout |

Five channels:

1. The immutable base breakout reaches its entry threshold.
2. The approved versioned session profile is active.
3. Session-specific liquidity passes.
4. Session-specific spread and slippage pass.
5. Validation/walk-forward evidence for this filter passes the no-session
   baseline gate.

All five are required for BUY. Base exits remain authoritative. Session ending
behavior (`NoEffect`, `BlockEntries`, `TightenTrail`, or `Exit`) creates a new
version. IANA database version and DST ambiguity policy are evidence. Main
failure is multiple-testing a disappearing session effect. Tests cover DST
gaps/folds, UTC conversion, weekend/overlap profiles, baseline comparison, and
session-alone HOLD.

### 5.11 `REGIME_CONSENSUS` version `1.0.0`

**Hypothesis:** enabling only independently approved families appropriate to a
simple, deterministic market regime may reduce predictable strategy/regime
mismatch.

| Field | Contract |
|---|---|
| Profile | `1d` and `4h` regime; delegated signal/execution profile remains part of each component version |
| Regimes | `TREND_UP`, `TREND_DOWN`, `RANGE`, `COMPRESSION`, `EXPANSION`, `CRISIS`, `UNKNOWN` |
| Indicators | Long trend, medium trend strength, volatility percentile, bandwidth/compression, point-in-time breadth |
| Warm-up | Maximum classifier/component warm-up across the complete breadth universe |
| Parameters | Versioned classifier thresholds and bounded component agreement/allocation factors |
| Maximum hold | Inherited from the admitted component; regime crisis/unknown is an immediate new-exposure veto |
| Expected strength | Prevents families from trading in regimes their own evidence does not approve |

The classifier first records long-term trend, medium-term trend strength,
volatility percentile, bandwidth/compression, and point-in-time breadth. Those
are classifier inputs, not the ensemble's five directional decision channels.

Five channels:

1. The deterministic regime permits new Spot long exposure.
2. The strongest regime-eligible component family votes in the direction.
3. A second independent regime-eligible component family confirms.
4. Point-in-time breadth and liquidity support the direction.
5. Portfolio concentration and cost-adjusted reward-to-risk permit admission.

BUY requires all five channels in version 1, making this stricter than the
shared four-of-five rule. Component families are analysis-only inputs; they do
not allocate their own workers. `TREND_UP` may qualify when two approved
components agree. `RANGE` and `COMPRESSION` remain HOLD in version 1 until two
independent approved component families exist for that regime.
`TREND_DOWN`, `CRISIS`, and `UNKNOWN` prohibit new Spot exposure and may
reduce/close. Any hard component veto vetoes the ensemble. Allocation factors
(regime confidence, agreement, liquidity, correlation) are each zero-to-one
and cannot increase base risk. Main failures are delayed classification and
complexity. Tests cover every classifier boundary, component eligibility,
conflicts, breadth survivorship, crisis close-only, and risk-factor bounds.

### 5.12 Approval evidence common to every family

No version becomes paper-approved unless all of these predeclared gates pass:

1. positive training performance after modeled costs;
2. acceptable validation performance;
3. consistent time-ordered walk-forward windows;
4. untouched holdout does not collapse;
5. neighboring parameters form a reasonable plateau;
6. higher fees do not immediately destroy the result;
7. higher slippage does not immediately destroy the result;
8. result is not dependent on one pair or one trade;
9. sufficient independent trades exist for the selected timeframe;
10. data and universe are point-in-time and survivorship-aware;
11. maximum drawdown is within the predeclared research threshold; and
12. forward paper trading completes without execution-state errors.

“Acceptable”, “consistent”, “collapse”, “reasonable”, “immediately”, and
“sufficient” must be replaced by numeric administrator-approved thresholds
before a run starts. They cannot be chosen after viewing holdout results.

## 6. Strategy approval lifecycle

The normative lifecycle and evidence bundle are in
`strategy-approval-workflow.md`. All versions begin `Draft`. This design can
advance at most through `PaperApproved`; live approval is explicitly
`NotAvailable`. Every promotion is immutable, version-specific, audited, and
human-approved. Suspension is immediate and does not wait for promotion
workflow.

---

## 7. Evaluation requirements

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

## 8. Continuous scan and experiment arrangement

The future model does not preassign ten workers to strategies or pairs.
`continuous-paper-scanner.md` defines stateless evaluation across the current
eligible Kraken Spot EUR universe. Scanning, HOLD, rejection, and queued
opportunities allocate no worker. A concrete opportunity receives an isolated
worker only after final admission, and at most ten admitted/open paper-position
contexts may exist.

Historical experiments still compare families, timeframes, and costs using
identical point-in-time datasets. Those experiments are not runtime workers and
cannot consume paper capacity. Do not select the highest historical winner.

---

## 9. Research priority order

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

## 10. Required tests

The complete traceable matrix is in `strategy-validation-matrix.md`. Minimum
cross-family tests include:

1. Every template is created in `Draft` and is not paper- or live-approved.
2. An approval transition without a recorded human approver is rejected.
3. A parameter set outside the approved range is rejected.
4. Signals are generated only from closed candles; an unclosed candle
   produces no decision.
5. Derived 10m candles are built only from ten closed 1m candles, use the
   documented UTC ten-minute boundary, and are marked derived.
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
17. Universe scans, HOLD outcomes, vetoes, and queued candidates allocate zero
    workers.
18. Admission creates exactly one isolated worker atomically and never exceeds
    ten admitted/open position contexts.
19. One-minute position management cannot create or add exposure.
20. Closing and reconciling a position releases capacity without reassigning
    the terminated worker.

---

## 11. Acceptance criteria

1. All ten families are documented with the full §3 specification.
2. All begin as `Draft`; none is paper- or live-approved.
3. Approval transitions are audited, versioned, and human-gated.
4. Evaluation uses train/validation/walk-forward/untouched-holdout plus
   forward paper trading, with cost and stress scenarios.
5. Selection by highest historical return is impossible by design.
6. Continuous scans use no worker capacity; workers are allocated only to
   admitted concrete opportunities.
7. Active positions use closed 1-minute management without a 1-minute entry
   path.
8. No profit claim appears anywhere in the product.
9. No strategy execution code is written as part of this planning task.

---

## 12. Explicitly excluded

- Implementing strategy execution or signal generation.
- Any live or proving-stage order placement.
- Automatic promotion of any strategy to any approval state.
- Any withdrawal capability (permanently out of scope).
