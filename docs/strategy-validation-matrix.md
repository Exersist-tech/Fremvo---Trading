# Strategy and Continuous Scanner Validation Matrix

Status: design and acceptance plan only. It authorizes no execution.

## 1. Evidence partitions

Every strategy-version/profile evaluation uses chronological, immutable
partitions:

1. **Training** for bounded parameter search.
2. **Validation** for model and parameter selection.
3. **Walk-forward** folds with training strictly before each evaluation window.
4. **Untouched holdout**, evaluated once after all choices and thresholds are
   frozen.
5. **Forward paper** using only information available at each UTC instant.

Embargo/purge periods are required where overlapping lookbacks or labels could
leak information between partitions. Holdout results never return to training,
ranking-weight selection, strategy editing, or gate threshold selection.
Datasets record candle, universe, asset-classification, delisting, cost-model,
code, strategy, parameter, and timezone-profile fingerprints.

## 2. The twelve approval gates

Words such as “acceptable” are not executable requirements. An administrator
must approve numeric thresholds per strategy version and profile before the
holdout is opened.

| # | Gate | Required evidence | Failure |
|---|---|---|---|
| G1 | Training after costs | Net result and risk metrics under baseline non-zero fees, spread, slippage and latency | Non-positive or below predeclared threshold |
| G2 | Validation | Predeclared net/risk thresholds on validation without retuning | Any threshold fails |
| G3 | Walk-forward consistency | Minimum passing-fold ratio, bounded dispersion and no terminal collapse | Too few folds pass or instability exceeds bound |
| G4 | Untouched holdout | One evaluation; predeclared degradation and absolute thresholds | Reuse, retuning, or collapse |
| G5 | Parameter neighborhood | Required fraction of adjacent bounded configurations remains acceptable | Isolated point optimum |
| G6 | Fee stress | Approved adverse maker/taker schedule | Result breaches stress threshold |
| G7 | Slippage/spread stress | Approved adverse spread/slippage and combined-cost scenarios | Result breaches stress threshold |
| G8 | Concentration | Maximum contribution by one pair, one trade, one period, or correlated group | Any concentration ceiling exceeded |
| G9 | Trade sufficiency | Minimum independent completed trades per profile, with confidence limits reported | Too few independent observations |
| G10 | Point-in-time data | Historical universe, listing/delisting, classifications and candles as known then | Survivorship or future leakage |
| G11 | Drawdown | Maximum drawdown and duration within approved limits | Either limit exceeded |
| G12 | Forward paper safety | Minimum duration/trades with no duplicate, unknown-unreconciled, state-loss, cross-owner, or live-route event | Any execution-state safety failure |

Passing gates does not imply profitability or live suitability. A simpler
benchmark must also be reported; similar performance after costs is a reason to
reject unnecessary complexity.

## 3. Shared strategy contract tests

| ID | Requirement | Test oracle |
|---|---|---|
| S-001 | Exactly ten stable family ids | Catalogue equals the normative ids; no extra runtime family is approved |
| S-002 | Immutable semantic version | Content change changes version/fingerprint and invalidates old approval |
| S-003 | Five channels | Each result contains exactly five named outcomes with evidence |
| S-004 | BUY threshold | 4-5 bullish channels BUY; 0-3 do not, unless contract requires all five |
| S-005 | SELL threshold | 4-5 bearish channels reduce/close a long; never open a Spot short |
| S-006 | HOLD | Neither threshold produces no exposure change |
| S-007 | Veto precedence | Every mandatory veto blocks a 5/5 bullish result |
| S-008 | Closed candles | Incomplete/forming candles cannot influence a decision |
| S-009 | UTC | Non-UTC or future timestamps fail closed |
| S-010 | Determinism | Same strategy/version/parameters/evidence produces byte-stable result/fingerprint |
| S-011 | Parameter bounds | Below/above bounds and malformed types are rejected, not clamped |
| S-012 | Warm-up | One candle short blocks; exact required safe history evaluates |
| S-013 | Sizing separation | Strategy output has no quantity, adapter, credentials, or exchange command |
| S-014 | Cost evidence | Zero/missing cost snapshot cannot qualify |
| S-015 | Stale-data safety | Stale market/account evidence blocks new or increasing exposure |
| S-016 | Halt safety | Platform/user/account/pair/strategy halt vetoes admission |
| S-017 | Reward/risk | Below-minimum reward/risk vetoes entry |
| S-018 | No profit claim | Contracts, UI and reports use research language only |

## 4. Timeframe and data-quality matrix

Run every supported interval through the applicable rows.

| Interval | Native/derived | Closure test | Gap/duplicate test | Boundary test | Entry support |
|---|---|---|---|---|---|
| 1m | Kraken native | Required | Required | UTC minute | Position management only; never entry in this design |
| 5m | Kraken native | Required | Required | UTC 5-minute | Yes by profile |
| 10m | Derived from 1m | All ten sources closed | Missing/duplicate source blocks | UTC minute divisible by 10 | Yes by profile |
| 15m | Kraken native | Required | Required | UTC quarter-hour | Yes by profile |
| 30m | Kraken native | Required | Required | UTC half-hour | Yes by profile |
| 1h | Kraken native | Required | Required | UTC hour | Yes by profile |
| 4h | Kraken native | Required | Required | Documented Kraken UTC boundary | Yes by profile |
| 1d | Kraken native | Required | Required | Documented UTC day boundary | Regime/ranking and approved signals |

For every interval test missing, duplicate, stale, late, out-of-order,
future-dated, wrong-symbol, wrong-interval, non-positive OHLC/volume, and unsafe
derived provenance. Closed-window repository queries must prove they do not
read the following candle.

## 5. Family-specific matrix

| Family | Required focused cases |
|---|---|
| `MTF_EMA_CONTINUATION` | EMA ordering and slope boundaries; pullback before resumption; extension/ATR veto; ranging whipsaw fixture; all four profiles aligned without future regime candles |
| `DONCHIAN_ENSEMBLE` | Prior-candle 20/55/100 channels; exactly 3 watch-only and 4 actionable; volume and extension limits; failed-break exit; no 1m signal |
| `BB_RSI_RANGE_REVERSION` | ADX/slope range gate; outside-band then later re-entry ordering; falling outside band is HOLD; no averaging down; trend-transition exit |
| `RSI_TREND_PULLBACK` | RSI enters zone then turns; still-falling RSI blocks; structural invalidation; oversized reversal veto; upper RSI and EMA exits |
| `MACD_VOLUME_ACCELERATION` | Exact cross recency; histogram sequence; price/volume confirmations; early/standard/conservative versions cannot alias; reverse-cross exit |
| `VOLATILITY_COMPRESSION_BREAKOUT` | Percentiles use training-only history; persistence count; prior boundary; volume expansion; no-expansion timeout; adverse-slippage false break |
| `CROSS_SECTIONAL_MOMENTUM` | Historical universe snapshots; delisted member; stable ties; absolute and relative trend; inverse-volatility bounds; rank buffer and turnover penalty |
| `RS_PULLBACK_ROTATION` | EUR/XBT/median/volatility-adjusted ranks; “least bad loser” rejection; pullback ATR depth; 4h setup before 1h confirmation; stale rank veto |
| `SESSION_BREAKOUT` | IANA DST gap/fold; tzdb version; UTC storage; no-session baseline; weekend/overlap; session alone cannot signal; each end behavior versioned |
| `REGIME_CONSENSUS` | Every classifier boundary; identical input determinism; complete breadth universe; component approval; two-family threshold; component veto; crisis/unknown close-only; allocation factors <= 1 |

## 6. Kraken eligibility and order-filter tests

| ID | Scenario | Expected result |
|---|---|---|
| K-001 | Pair absent/offline/cancel-only/post-only when entry requires normal trading | Veto |
| K-002 | Non-EUR quote or non-cryptocurrency/unknown base class | Excluded |
| K-003 | Missing tick, quantity step, minimum quantity or minimum notional | Veto |
| K-004 | Price not aligned to tick | Reject; never silently round to a materially different order |
| K-005 | Quantity below step/minimum after conservative flooring | Reject |
| K-006 | Notional below minimum after costs | Reject |
| K-007 | Rolling volume passes but median volume fails | Ineligible |
| K-008 | Spread or slippage exceeds strategy limit | Veto |
| K-009 | Eligibility evidence expires during queue wait | Reject at admission |
| K-010 | Kraken catalogue symbol differs from display symbol | Connector normalizes; domain identity remains stable |

Tests use recorded/synthetic responses and cannot submit an exchange order.

## 7. Continuous scanner and queue matrix

| ID | Scenario | Expected result |
|---|---|---|
| C-001 | Five-minute tick, no new strategy signal candle | No new observation/decision |
| C-002 | Same tick/evidence replay | One immutable observation |
| C-003 | Full eligible universe with only HOLD results | Zero workers |
| C-004 | HOLD, bearish Spot, vetoed and rejected signals | Zero workers; reasons recorded |
| C-005 | Twelve valid signals and zero open positions | Top ten admitted atomically; two queued; ten workers |
| C-006 | Twelve valid signals and ten open positions | Twelve evaluated/ranked; zero new workers |
| C-007 | Position closes with queued opportunity still fresh | Full revalidation then one admission |
| C-008 | Queued signal becomes stale/ineligible | Terminal rejection; no worker |
| C-009 | New signal for same strategy/pair/profile | Older queued signal superseded |
| C-010 | Concurrent admission race at capacity nine | Exactly one succeeds; capacity never exceeds ten |
| C-011 | Worker creation fails inside reservation transaction | Reservation rolls back; no leaked capacity |
| C-012 | Open worker faults | Other workers and scanner continue |
| C-013 | Fault has unknown execution outcome | Capacity remains reserved until reconciliation |
| C-014 | Fully closed/reconciled worker | Worker terminates and capacity releases; worker is not reassigned |
| C-015 | Rank ties | Stable key produces reproducible ordering |
| C-016 | Training-profit-only high candidate | Ranking cannot use that field directly |
| C-017 | Existing correlated exposure | Concentration gate rejects or lowers admission eligibility, never strategy evidence |
| C-018 | Owner isolation | Candidate, queue, capacity and worker records never cross owners |
| C-019 | Admitted entry is explicitly rejected or expires unfilled | Admission closes and capacity releases; no position or fill is fabricated |
| C-020 | Two candidates share one strategy on different pairs | Both may rank; no legacy one-worker-per-strategy cap, but pair/correlation/exposure gates still apply |

Property-based concurrency testing should attempt many simultaneous admissions
and assert `0 <= capacity <= 10` after every committed transition.

## 8. One-minute position-management matrix

| ID | Scenario | Expected result |
|---|---|---|
| M-001 | Closed safe 1m candle, no exit | HOLD; no new decision that adds exposure |
| M-002 | Forming 1m touches stop/target | No closed-candle strategy exit |
| M-003 | Closed 1m crosses hard stop | Exact reduce-only exit through normal pipeline |
| M-004 | Closed 1m reaches target | Exact reduce-only exit |
| M-005 | Trailing/invalidation condition | Exit only with complete approved evidence |
| M-006 | Missing/gapped/stale 1m sequence | Fail closed; block increases and apply documented safety state |
| M-007 | Repeated same 1m candle | One claim and at most one fill |
| M-008 | Unknown exit outcome | Reconcile before retry |
| M-009 | Partial close | Remaining position stays owned by same worker |
| M-010 | Full close | Portfolio and cash replay identically after restart; capacity releases |
| M-011 | Bullish 1m evidence without strategy signal | Cannot open or add |
| M-012 | Emergency stop | No admission; existing position follows approved close-only policy |

## 9. Backtest and robustness plan

For each strategy-version/profile:

1. Freeze hypothesis, ranges, data partition dates, universe policy, costs,
   benchmarks, metrics, and all twelve numeric gates.
2. Build point-in-time EUR universes. Preserve unavailable/delisted pairs and
   record unknown classification as ineligible.
3. Generate deterministic datasets with safe closed candles and warm-up that
   precedes, but does not score in, each partition.
4. Search only bounded training parameters. Prefer stable plateaus and simpler
   configurations.
5. Choose on validation under baseline costs. Record every attempted
   configuration to expose multiple testing.
6. Run time-ordered walk-forward folds, recomputing universe and parameters
   from information available before each fold.
7. Run sensitivity:
   - neighboring parameters;
   - higher maker/taker fees;
   - wider spreads;
   - higher adverse slippage;
   - delayed/missed execution;
   - rejection and partial-fill scenarios;
   - reduced liquidity and combined stress.
8. Report by pair, trade, month/regime, direction, holding duration and
   correlated group. Include turnover, drawdown depth/duration, exposure,
   rejection rate and sample uncertainty.
9. Lock code, specification, parameters, costs and gate thresholds.
10. Evaluate untouched holdout once. Failure creates a rejected evidence
    bundle; it does not trigger retuning against holdout.
11. If holdout passes, run forward paper through the same decision, risk,
    claim, execution, reconciliation, portfolio and audit pipeline.

Backtests model fees, spread, slippage, latency, Kraken filters, rejected
orders, missed entries and partial fills where supported. They never assume
fill at the signal close when execution occurs later.

## 10. Forward-paper safety acceptance

Paper evidence must demonstrate, for the predeclared minimum duration and trade
count:

- no production exchange call;
- no duplicate decision, claim, command or fill;
- no blind retry after unknown status;
- deterministic restart replay;
- preserved worker isolation;
- exact owner isolation;
- correct partial/full close accounting;
- no worker allocated before opportunity admission;
- capacity never above ten;
- one-minute management never creates exposure;
- known rejected/unfilled admissions release capacity without a fabricated fill;
- halt and stale-data behavior;
- complete immutable audit evidence.

Performance may still fail separately. Operational safety passing does not make
a losing strategy acceptable, and performance passing does not excuse an
execution-state failure.

## 11. Reproducibility manifest

Every result bundle records:

- strategy id/version/content and parameter fingerprints;
- scanner ranking/capacity policy versions where applicable;
- source commit and build identity;
- dataset, universe and asset-classification fingerprints;
- candle intervals and derivation versions;
- timezone profile and IANA database version;
- cost/filter model versions and decimal assumptions;
- random seed;
- partition/fold boundaries in UTC;
- attempted parameter configurations;
- metrics and gate outcomes;
- approver identity and approval UTC.

Two runs with the same manifest must produce the same decisions, simulated
fills, portfolio ledger and metrics.
