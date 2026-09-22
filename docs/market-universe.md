# Market Universe and Instrument Eligibility

Planning document with the initial paper-training discovery subset now
implemented. Live-trading eligibility remains unimplemented and unavailable.

This document defines how the platform decides **which instruments may be
used, for what purpose, and in which trading mode**. It is the gate that
sits in front of research, backtesting, paper trading, and — much later —
Spot and Futures execution.

Nothing in this document asserts that any listed pair is a good trade, is
liquid enough, or will remain tradable. Every statement about an instrument
must be re-derived from live exchange data and the platform's own rolling
measurements.

---

## 1. Principles

1. **The exchange is the source of truth, not this file.** The configured
   seed list below is a *research starting point*. The platform must load
   the current instrument catalogue from the Kraken `AssetPairs`
   endpoint and treat any configured symbol that is absent, not `TRADING`,
   or lacking `SPOT` permission as unusable.
2. **A configured pair is not an approved pair.** Configuration only makes
   a symbol a candidate. Eligibility is *computed*, continuously, from
   evidence.
3. **Eligibility is per purpose, not global.** A pair may be eligible for
   backtesting and simultaneously ineligible for paper trading, Spot live
   trading, or a 1-minute strategy.
4. **Eligibility is per strategy and per timeframe.** A pair accepted for
   a 4-hour strategy is *not* automatically acceptable for a 1-minute
   strategy; the cost and liquidity requirements are materially different.
5. **Evidence beats a single reading.** A pair is never selected on one
   current 24-hour volume figure. Rolling and median measures over a
   configurable window are required.
6. **Degradation is a one-way door until reconciled.** Losing eligibility
   takes effect immediately; regaining it requires fresh evidence and,
   where positions or orders exist, reconciliation.
7. **Fail closed.** Missing data, unknown status, unloaded filters, or a
   stale measurement all mean *not eligible*.

---

## 2. Initial Spot research seed (50 USD pairs)

All of these begin as **`Tracked` only**. None is research-eligible,
backtest-eligible, paper-eligible, or live-eligible on creation.

Symbols use **Kraken's own asset codes**, so Bitcoin is `XBT` and Dogecoin
is `XDG`. The quote asset is **USD**, which carries Kraken's deepest spot
liquidity. This is a data-selection choice for research only and is
unrelated to a user's reporting currency, which each user chooses.

```
XBTUSD    ETHUSD    SOLUSD    XRPUSD    ADAUSD
XDGUSD    LINKUSD   AVAXUSD   LTCUSD    DOTUSD
TRXUSD    XLMUSD    BCHUSD    ATOMUSD   UNIUSD
NEARUSD   FILUSD    ETCUSD    AAVEUSD   ALGOUSD
XTZUSD    XMRUSD    ZECUSD    DASHUSD   ICPUSD
INJUSD    SUIUSD    APTUSD    ARBUSD    OPUSD
TIAUSD    SEIUSD    RENDERUSD GRTUSD    MANAUSD
SANDUSD   AXSUSD    CRVUSD    COMPUSD   SNXUSD
LDOUSD    PEPEUSD   SHIBUSD   WIFUSD    BONKUSD
ONDOUSD   ENAUSD    JUPUSD    PYTHUSD   TAOUSD
```

This list is an **initial research seed, not a permanent list and not an
automatically live-tradable list.** It is stored as configuration (seed
data), is versioned, and may be changed by an administrator without a code
change.

The paper-training Start flow does not use this fixed seed. It loads the
current Kraken Spot catalogue, filters active EUR-quoted cryptocurrency pairs,
requires thirty complete daily candles preceding validation and at least EUR 1M
median daily EUR quote volume, then ranks at most 40 pairs. This grants no
general or live eligibility: candidates still have to pass strategy validation,
selection, and untouched holdout to be labeled qualified. Automatic selection
evaluates the largest bounded top-liquidity subset across eleven approved strategy evaluators and
5-minute, 15-minute, 30-minute, and 1-hour candles. Each interval receives an
equal 600-candle sample split into 420 validation and 180 untouched holdout
candles. The ten-worker selection enforces one worker per strategy and then favors unseen intervals and
symbols before using performance rank to fill remaining capacity. The strongest
unqualified finalists may use otherwise empty worker capacity only for explicitly
labeled fake-funds forward exploration; this never grants live eligibility.

### 2.1 Known caveats in the seed list

These are flagged so the implementation does not silently assume the seed
is correct:

- **Several of these symbols are recent listings** (for example `WIFUSD`,
  `BONKUSD`, `JUPUSD`, `ENAUSD`, `ONDOUSD`).
  Recent listings have short history, unstable liquidity, and wide
  spreads. They must enter the newly-listed restricted state (§7) and are
  expected to remain research-only for a long time.
- **Kraken's `AssetPairs` response carries no listing date.** Listing age
  is therefore *unknown* for every instrument until the platform has
  observed enough of its own candle history. Unknown age fails the
  new-listing gate; it must never be treated as "old enough".
- **Some symbols may not exist on Kraken Spot, may be delisted, or may
  exist only as a Futures contract or on a different quote asset.** The
  catalogue sync must record "configured but not present on the exchange"
  as an explicit, visible state rather than dropping the symbol silently.
- **Kraken's internal asset codes differ from its display codes**
  (`XXBT`/`ZUSD` versus `XBT`/`USD`). Only the connector may perform that
  translation, and the four-character prefix strip must not be applied to
  genuine three-character assets such as `XRP` and `ZEC`.
- **Ticker collisions are possible** (short tickers such as `OP` are
  reused across projects and exchanges). The platform keys
  instruments on the exchange's own symbol identifier plus base/quote
  asset codes, never on a display name.
- **Memecoins and very low-capitalisation assets in this list carry
  materially higher slippage and gap risk** than the majors. The
  eligibility thresholds, not the seed list, are what protect the
  platform.

### 2.2 Permanent exclusions from this research universe

The following are excluded from the initial strategy universe regardless
of liquidity:

- Stablecoin-quoted or stablecoin-based pairs whose **base** asset is a
  stablecoin (for example `USDCUSD`, `USDTUSD`, `DAIUSD`, `PYUSDUSD`).
- Tokenized stocks and tokenized equity/ETF products.
- Pairs whose **base** asset is fiat. Note that a fiat *quote* asset is
  expected and permitted: this universe is USD-quoted, and USD is fiat.
  The classification exclusions apply to the base asset; the quote asset
  is governed separately by the configured allowed-quote list.
- Non-cryptocurrency instruments of any kind.
- Leveraged tokens and any instrument whose value is a derived, rebalanced
  basket (their behaviour breaks candle-based strategy assumptions).

Exclusion is implemented as a maintained **asset-classification list**
(`AssetClass`: `Cryptocurrency`, `Stablecoin`, `TokenizedEquity`, `Fiat`,
`LeveragedToken`, `Unknown`). An asset whose class is `Unknown` is **not**
eligible for anything beyond `Tracked`.

---

## 3. Instrument state model

Each instrument has exactly one **lifecycle state** plus a set of
independently computed **eligibility grants**.

### 3.1 States

| State | Meaning |
|---|---|
| `Tracked` | Known to the platform. Catalogue and candles may be collected. No research, no backtest, no trading. |
| `ResearchEligible` | Enough history and data health for exploratory analysis and the scanner. |
| `BacktestEligible` | Complete, gap-free required history; filters loaded; cost model parameters available. |
| `PaperEligible` | Backtest-eligible plus live data health and current liquidity/spread evidence. |
| `SpotProvingEligible` | Paper-eligible plus a validated Spot account whose execution path has been proven against recorded Kraken responses. Kraken has no public Spot sandbox, so this grant permits only minimum-size orders under the Phase 9 proving ceiling. |
| `SpotLiveEligible` | Spot proving-eligible plus explicit administrator approval and passing live-liquidity thresholds. |
| `FuturesTestEligible` | A futures contract exists, margin/funding data is available, and the path is validated on the Kraken Futures demo environment. |
| `FuturesLiveEligible` | Futures test-eligible plus explicit administrator approval; only after Spot live is stable. |
| `Suspended` | Blocked by the exchange, by data health, by liquidity failure, or by an operator. New exposure forbidden. |
| `Removed` | Delisted or permanently withdrawn from the platform. Records preserved. |

**All pairs begin as `Tracked` only.**

These are *not* a simple linear ladder: an instrument can be
`BacktestEligible` while failing `PaperEligible`. The higher grants are
strictly additive — a grant can never be held without every lower grant
also currently holding.

### 3.2 Transition rules

```
Tracked ──(history + data health)──────────► ResearchEligible
ResearchEligible ──(complete history + filters)──► BacktestEligible
BacktestEligible ──(live data health + liquidity)─► PaperEligible
PaperEligible ──(replay path proven)──────────► SpotProvingEligible
SpotProvingEligible ──(admin approval)────────► SpotLiveEligible
PaperEligible ──(contract + margin data)──────► FuturesTestEligible
FuturesTestEligible ──(administrator approval)► FuturesLiveEligible

any state ──(failure / operator action)───────► Suspended
Suspended ──(fresh evidence + reconciliation)─► previous grants re-earned
any state ──(delisted)────────────────────────► Removed
```

- **Downgrades are immediate and automatic.** Any failing gate revokes
  that grant and every grant above it, in the same evaluation.
- **Upgrades are never automatic past `PaperEligible`.** `SpotLiveEligible`
  and `FuturesLiveEligible` always require an explicit, audited human
  approval in addition to passing every gate.
- Every transition writes an immutable audit event recording the previous
  state, the new state, the triggering gate, and the measured values.

---

## 4. Eligibility gates

A configured pair may be used for a given purpose **only when every one of
the following holds**:

| # | Gate | Requirement |
|---|---|---|
| 1 | Exchange status | Current catalogue status is `TRADING`. |
| 2 | Permissions | `SPOT` permission present (and the corresponding permission for the requested product type). |
| 3 | Quote asset | Quote asset is `USD` for this initial universe. |
| 4 | Filters loaded | Price tick, quantity step, min quantity, max quantity, and min notional are all loaded and non-null. |
| 5 | Data health | No unresolved gaps, duplicates, stale-data events, or out-of-order events inside the required window. |
| 6 | History complete | The required history for the requested timeframe is present and gap-free. |
| 7 | Liquidity | Rolling and median quote volume meet the threshold for the requested timeframe and mode. |
| 8 | Spread | Rolling spread is at or below the configured maximum. |
| 9 | Slippage | Estimated slippage for the intended order size is at or below the configured maximum. |
| 10 | Listing age | Listing age exceeds the configured minimum, or the instrument is explicitly approved for the newly-listed research track. |
| 11 | Asset class | Asset class is `Cryptocurrency` and not on the exclusion list. |
| 12 | Strategy approval | The pair (or its instrument class) is approved for the specific strategy, timeframe, product type, and trading mode requested. |

Gate 12 is the reason eligibility cannot be a single boolean on the
instrument: it is a **function of (instrument, strategy, timeframe,
product type, trading mode)**.

### 4.1 Thresholds vary by timeframe

Thresholds are configuration, not constants in code, and are defined
**per timeframe band** because cost sensitivity differs sharply:

| Timeframe band | Relative liquidity requirement | Relative spread tolerance | Notes |
|---|---|---|---|
| 1m, 5m | Highest | Tightest | Turnover is high; spread and slippage dominate results. |
| 10m, 15m, 30m | High | Tight | |
| 1h, 4h | Moderate | Moderate | |
| 1d | Lowest of the set | Widest of the set | Still subject to absolute floors. |

A pair failing the 1-minute band may legitimately remain eligible for the
4-hour band. The reverse must also be representable.

### 4.2 Mandatory absolute floors

Per-timeframe tuning may only make requirements **stricter** than the
platform's absolute floors. An operator cannot configure a threshold that
is more permissive than the floor. This mirrors the risk-engine rule that
user limits may only sit below mandatory platform ceilings.

---

## 5. Rolling measurements

For every tracked instrument the platform maintains a rolling metrics
record, recomputed on a schedule and stored with the UTC timestamp of the
observation window:

- Rolling quote volume (configurable window, e.g. 7/30 days)
- **Median** quote volume over the window (resistant to a single spike)
- Minimum quote volume observed in the window
- Rolling average spread and worst observed spread
- Estimated slippage for a set of reference order sizes
- Price depth at configured distances from mid, when depth data is available
- Trade frequency (trades per interval)
- Count and total duration of data gaps
- Count of stale-data events
- Listing age (first observed candle, and exchange onboarding date when available)
- Current exchange trading status
- Current exchange filter set, with the time it was last refreshed

**Rule:** selection must never use a single current 24-hour volume value
alone. At minimum, both a rolling measure and a median measure must pass.
A pair whose volume is carried by one spike day fails the median test and
is therefore not eligible.

All measurements are `decimal`. All timestamps are UTC.

---

## 6. Recalculation

- Eligibility is recalculated on a **configurable schedule** by a
  background worker, and additionally on demand.
- It is also recalculated immediately on: catalogue status change, filter
  change, a stale-data event, a detected data gap, and operator action.
- Each recalculation stores an **evaluation record** containing the
  measured values and the pass/fail result of every gate, so any decision
  is explainable after the fact.
- A recalculation that cannot complete (for example the catalogue sync
  failed) must **not** silently leave stale grants in place: grants whose
  supporting evidence is older than the configured maximum age expire and
  fall back to `Tracked`.

---

## 7. Newly listed instruments

Newly listed instruments enter a **configurable restricted state**:

- They are `Tracked`, and may become `ResearchEligible` only.
- They cannot become `BacktestEligible` until the minimum history
  requirement is satisfied, because short history produces unreliable and
  non-falsifiable results.
- They cannot become `PaperEligible` or any live grant until listing age,
  liquidity stability, and spread stability have all been demonstrated
  over the configured observation window.
- The restriction window and the evidence requirements are configuration.
- A strategy may only trade newly listed instruments if it is explicitly
  designed and approved for that condition.

---

## 8. Degradation behaviour

If a symbol becomes suspended, loses a required permission, has stale
data, or fails a liquidity requirement, then **immediately**:

1. **Block new entries.** No new positions on that instrument.
2. **Block position increases.** No order that would increase exposure.
3. **Preserve existing order and position records.** Nothing is deleted,
   and no record is rewritten to hide the event.
4. **Allow only validated cancellation or exposure reduction.** Reduce-only
   and close-only operations remain permitted, and each one is still
   validated against current filters before submission.
5. **Require reconciliation before returning to normal status.** If any
   order state is unknown, it must be reconciled — never blindly retried —
   before the instrument can regain any grant.

This behaviour is enforced by the risk engine and the trading pipeline,
not by the UI. An instrument-level block must be impossible to bypass by
calling an API endpoint directly.

An instrument block composes with, and never overrides, the existing halt
scopes (emergency stop, market halt, user halt, account halt, strategy
halt, close-only, reduce-only). The most restrictive rule always wins.

---

## 9. Strategy approval requirements

Every strategy approval record must specify, explicitly:

- Supported pair or instrument class
- Minimum history required
- Minimum liquidity required
- Maximum spread accepted
- Maximum estimated slippage accepted
- Supported timeframes
- Supported product type (`Spot`, `Margin`, `Futures` — separate
  capabilities, never a flag on a Spot order)
- Approved trading modes (`Backtest`, `Paper`, `SpotTest`, `SpotLive`,
  `FuturesTest`, `FuturesLive`)

A strategy may run against an instrument only when the instrument's
current grants satisfy **all** of the above for the requested mode. An
approval is versioned and immutable; changing any field creates a new
version and requires re-approval.

---

## 10. Required tests

Domain and application tests, all runnable without network access:

**Catalogue and classification**
1. A configured symbol absent from the exchange catalogue is reported as
   `ConfiguredButNotListed` and is not eligible for anything.
2. A symbol whose status is not `TRADING` is not eligible for anything
   above `Tracked`.
3. A symbol lacking `SPOT` permission is not Spot-eligible.
4. A non-USD quote asset is excluded from this universe.
5. Stablecoin, tokenized-equity, fiat, and leveraged-token classes are
   excluded even when every liquidity gate passes.
6. An asset of `Unknown` class never exceeds `Tracked`.

**Seed and defaults**
7. The 50-pair seed loads and **every pair is `Tracked` only**, with no
   eligibility grants.
8. No seed pair is live-eligible by default, for Spot or Futures.

**Gates**
9. Missing exchange filters block `BacktestEligible` and above.
10. A single 24-hour volume spike with a failing median does **not**
    produce eligibility.
11. A pair passing the 4-hour liquidity band but failing the 1-minute band
    is eligible for 4-hour strategies and rejected for 1-minute strategies.
12. Spread above maximum blocks paper and live grants.
13. Estimated slippage above maximum blocks paper and live grants.
14. An incomplete required-history window blocks `BacktestEligible`.
15. A data gap inside the required window blocks eligibility until resolved.
16. Evidence older than the configured maximum age expires the grants.

**Newly listed**
17. A newly listed instrument cannot exceed `ResearchEligible` inside the
    restricted window.
18. A newly listed instrument becomes `BacktestEligible` only after the
    minimum history requirement is met.

**Degradation**
19. Suspension blocks new entries and position increases.
20. Suspension still permits reduce-only and close-only operations.
21. Suspension preserves existing order and position records unchanged.
22. An instrument with an unknown order state cannot regain grants until
    reconciliation completes.
23. A stale-data event immediately revokes `PaperEligible` and above.
24. Instrument blocks combine with halt scopes such that the most
    restrictive rule wins.

**Transitions and audit**
25. Revoking a lower grant revokes every grant above it in the same
    evaluation.
26. `SpotLiveEligible` cannot be reached without an explicit recorded
    administrator approval, even when all gates pass.
27. `FuturesLiveEligible` cannot be reached before Spot live is stable.
28. Every transition writes an audit event containing the previous state,
    new state, triggering gate, and measured values.
29. Eligibility evaluation is deterministic: identical inputs produce an
    identical result and an identical explanation.

---

## 11. Acceptance criteria

1. The instrument catalogue is retrieved from the Kraken
   `AssetPairs` endpoint and stored exchange-neutrally; no Kraken
   DTO reaches `Trading.Domain`.
2. The 50-pair seed is present, versioned, administrator-editable, and
   every pair is `Tracked` only.
3. Eligibility is computed per instrument **per purpose, per timeframe,
   per product type, and per trading mode**, and is never a single global
   boolean.
4. Every eligibility decision is explainable: an operator can see each
   gate, its measured value, its threshold, and its pass/fail result, with
   the UTC time of measurement.
5. No instrument is Spot-live or Futures-live eligible without an explicit,
   audited administrator approval.
6. Rolling and median liquidity measures are both required; a single
   24-hour figure cannot grant eligibility.
7. Degradation immediately blocks new entries and increases while
   preserving records and permitting validated reduction.
8. Recalculation runs on schedule, on relevant events, and expires stale
   evidence rather than trusting it.
9. All monetary and quantity values are `decimal`; all timestamps are UTC.
10. No trading is executed as part of this work.

---

## 12. Explicitly excluded from this work

- Any order placement, Spot or Futures, test or live.
- The Kraken order endpoints.
- Futures contract eligibility beyond the state definitions above.
- Automatic promotion of any instrument to a live grant.
- Any withdrawal-related capability (permanently out of scope).
