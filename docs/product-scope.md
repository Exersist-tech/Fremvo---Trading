# Product Scope

## 1. Vision

A global, invitation-only platform that lets individual users connect their
**own** exchange accounts (starting with Kraken) and run **approved,
platform-vetted** trading strategies — first in paper trading, later in
supervised live trading — with strong risk controls, auditability, and no
custody of user funds.

The platform is a **decision and execution engine on top of exchanges the
user already controls**, not a custodian, broker-dealer, or fund manager.

## 2. Non-goals (permanent)

- The platform never holds, transfers, or withdraws user funds. Only
  `TRADE` and `READ` exchange API permissions are ever required; `WITHDRAW`
  permission is rejected during connection validation.
- Users cannot upload or author arbitrary strategy code. Only approved
  templates with approved parameter ranges are selectable.
- No guarantee, implication, or marketing claim of profitability is ever
  produced, in the UI, reports, or documentation.
- No tax, legal, or investment advice is given. Reporting is informational
  only and clearly labeled as such.
- No unrestricted microservice sprawl — the platform stays a small number of
  deployable units (a Blazor web app, a small set of worker services) until
  scale genuinely requires more.

## 3. Users and roles

| Role | Description |
|---|---|
| `Guest` | Unauthenticated visitor; can only view invitation/landing content. |
| `TrialUser` | Invited user in a time/feature-limited trial. |
| `User` | Full standard user with an active plan/entitlement. |
| `SupportAgent` | Read-only access to a user's account for support, with audited access and no ability to trade or view secrets. |
| `RiskOfficer` | Can view risk exposure, trigger halts, cannot modify strategies or user funds. |
| `Administrator` | Manages invitations, users, plans, global halts, approved strategy catalog. Requires MFA. |
| `SystemService` | Non-human identity used by worker services against internal APIs. |

Invitation-only: registration requires a valid, single-use (or capped-use)
invitation code issued by an Administrator or an existing user's referral
allowance (if enabled later). No public self-service sign-up initially.

## 4. Functional scope (target end-state)

1. **Identity & access** — invitations, registration, authentication (with
   MFA for administrators, optional/encouraged for users), roles,
   authorization, audit log of sensitive actions.
2. **Exchange account connection** — Kraken Spot and Futures (testnet and
   live), API key validation (permission + IP restriction checks), secret
   storage via Key Vault references only.
3. **Market data** — ingestion of trades/candles from Kraken, normalization
   into platform-neutral candle intervals, gap/duplicate/staleness detection,
   historical storage for backtesting and charting.
4. **Charting & indicators** — standard technical indicators computed from
   stored candles, used by strategies and displayed on charts.
5. **Market scanner** — background service that evaluates configured
   screening criteria (indicator/volume/volatility conditions) across a
   symbol universe and produces ranked candidate lists.
6. **Approved strategy templates** — a small, curated set of parameterized
   strategies (e.g., trend-following, mean-reversion) with bounded parameter
   ranges enforced by the platform, never user-supplied code.
7. **Backtesting** — deterministic, reproducible historical simulation
   including fees, spread, slippage, exchange filters, partial fills.
8. **Optimization** — parameter search using training/validation/holdout/
   walk-forward splits; holdout is never used for parameter selection.
9. **Experiment workers** — up to 10 isolated strategy execution contexts
   (paper or live) with independent state; one worker's failure is isolated.
10. **Paper trading** — simulated order execution against real-time market
    data with fake balances, using the exact same strategy/risk pipeline as
    live trading.
11. **Automated Spot trading (live)** — real order placement on Kraken Spot
    once paper trading, risk controls, and reconciliation are proven.
12. **Leveraged futures trading (live)** — Kraken USD-M futures, long/short,
    isolated from Spot, gated behind additional safety requirements.
13. **Risk engine** — pre-trade and continuous risk evaluation, halts at
    multiple scopes, staleness protection, exposure ceilings.
14. **User administration** — Administrator UI for invitations, user
    management, plan assignment, strategy catalog approval, global halts.
15. **Trials, plans, entitlements** — time-boxed trials, plan tiers gating
    feature access (e.g., number of experiment workers, live trading
    eligibility); no payment processing yet, but the entitlement model is
    designed so billing can be added later without redesign.
16. **International reporting** — user-selected language, time zone,
    reporting currency; optional country-specific export formats; explicit
    "not tax/legal/financial advice" disclaimers.
17. **Observability & operations** — structured logging, metrics, alerting,
    runbooks, disaster recovery for market data and trading state.

## 4A. Market universe and strategy research

The set of instruments the platform may use, and the strategies it may
run against them, are governed by two dedicated planning documents:

- **`docs/market-universe.md`** — the initial 50-pair USDT Spot research
  seed, the ten instrument states, the twelve eligibility gates, rolling
  liquidity/spread/slippage measurement, newly-listed restrictions, and
  degradation behaviour. All seed pairs begin as `Tracked` only; none is
  automatically live-tradable, and the live exchange catalogue — not the
  seed list — is the source of truth.
- **`docs/strategy-research-plan.md`** — the ten approved research
  families, the `Draft → … → Deprecated` approval lifecycle, rejection
  gates, session/regime handling, and the ten-worker experiment groups.
  These are falsifiable research templates; the platform never claims a
  strategy is or will be profitable, and never promotes the
  highest-return backtest to live trading.

## 5. Non-functional requirements- **Correctness over speed**: all financial math in `decimal`; no floating
  point in the money/quantity path.
- **Determinism**: backtests must be exactly reproducible given the same
  inputs, parameters, and random seed.
- **Auditability**: every state transition in the trading pipeline
  (MarketEvent → … → AuditEvent) is persisted and traceable end-to-end by a
  correlation identifier.
- **Safety by default**: live trading and leverage are off by default per
  user, per account, and globally; multiple independent halt switches exist.
- **Isolation**: a bug or crash in one experiment worker or one user's
  strategy must not affect others.
- **Least privilege**: exchange API keys request only the minimum required
  permissions; secrets never reach the browser or logs.
- **Extensibility**: adding a second exchange must not require changes to
  Domain, strategy, risk, or backtesting code — only a new connector
  implementing existing ports.
- **Availability**: market-data ingestion and risk halting are the most
  availability-critical paths; the web UI can tolerate brief unavailability
  more readily than order-safety controls.
- **Internationalization**: no hard-coded country, currency, time zone, or
  number-format assumptions anywhere in Domain or Application layers.
- **Testability**: all business rules covered by fast, deterministic unit
  tests; no test ever places a live production order.

## 6. Explicit staged rollout (see `docs/implementation-plan.md` for detail)

1. Foundations (solution skeleton *only after this plan is approved*),
   identity, invitations.
2. Exchange account connection + secret storage (non-trading Paper stage only).
3. Market data ingestion + storage + indicators + charts.
4. Market scanner.
5. Approved strategy templates + backtesting engine.
6. Optimization (train/validation/holdout/walk-forward).
7. Paper trading + experiment workers (isolated, up to 10).
8. Risk engine + halts + duplicate-order protection + idempotency +
   reconciliation.
9. Kraken Spot **proving stage** (recorded-response replay in CI, plus a minimum-size supervised live path; Kraken has no public Spot sandbox).
10. Kraken Spot **live** trading (small, gated rollout).
11. Kraken Futures trading on the Kraken Futures **demo environment**.
12. Kraken Futures **live** trading (gated, after Spot live is stable).
13. User administration, plans/trials/entitlements.
14. International reporting.
15. Hardening: security review, DR drills, alerting maturity, billing prep.

Live trading (step 9+) is only reachable after steps 1–8 are complete and
demonstrated via tests and paper-trading parity. Leveraged futures (step 11+)
is only reachable after Spot live trading (step 10) is stable.
