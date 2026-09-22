# Running locally

## Visual Studio

Open `Trading.sln` and press F5 or Ctrl+F5. The solution starts
`src/Trading.Web` on `https://localhost:5200` and opens the sign-in page.

Two pieces of configuration make that work, and both are committed so the
behaviour is the same on every machine:

- **`Trading.Web` is the first project in `Trading.sln`.** Visual Studio picks
  the first project in the solution as its default startup project when it has
  no saved preference. Previously that was `Trading.Domain`, a class library,
  so a fresh clone produced *"A project with an Output Type of Class Library
  cannot be started directly."*
- **`Trading.slnLaunch`** declares the startup explicitly, so the choice is
  visible in the run dropdown and survives deleting the `.vs` folder.

If Visual Studio ever starts the wrong project again, delete the untracked
`.vs` folder. It holds per-user state that overrides both of the above.

## Command line

```
cd src/Trading.Web
dotnet run --launch-profile https
```

### Enterprise Application Control

On a machine where Windows Code Integrity requires enterprise-signed DLLs,
normal Visual Studio F5 can fail with `0x800711C7` while loading an unsigned
project assembly. Cleaning, rebuilding, `Unblock-File`, or changing execution
policy does not satisfy that signing rule.

In Solution Explorer, confirm that **Trading.Web** is the startup project and
that the **https** profile is selected. If `0x800711C7` persists, the device
administrator must sign or allow the development output under the enforced
policy. The repository does not weaken Application Control or provide a bypass.
After a policy change, restart Visual Studio before trying the profile again;
already-running processes retain their previous Code Integrity state.

## Opt-in public Kraken candle stream

`Trading.Workers.MarketData` is inert by default. To start its public OHLC v2
stream, configure a database connection named `TradingDb` and explicitly set
the symbols and native Kraken intervals, for example:

```json
"MarketDataStreaming": {
  "Enabled": true,
  "Symbols": [ "BTC/USD", "ETH/USD", "SOL/USD", "XRP/EUR", "TRX/EUR", "DOGE/EUR", "ADA/EUR" ],
  "Intervals": [ "OneMinute", "FiveMinutes" ],
  "MaximumReconnectDelaySeconds": 30
}
```

Run it with `dotnet run --project src/Trading.Workers.MarketData`. This uses
only Kraken's public WebSocket endpoint; do not configure credentials, API
keys, or authorization headers. A candle is persisted only after a subsequent
interval proves it is closed.

## Web-started historical qualification and paper-training worker

Paper training uses fake funds only and has no live or futures order route.
Starting from `/experiments` first loads Kraken's active Spot catalogue. It
considers EUR-quoted cryptocurrency pairs only, requires thirty complete daily
candles preceding the strategy test, and requires median daily EUR quote volume
of at least EUR 1,000,000. It ranks at most 40 pairs and evaluates eleven approved
single-series strategies that have equivalent historical evaluators across approved 5-minute,
15-minute, 30-minute, and 1-hour closed candles.

The web Start control uses 600 closed candles per interval so every timeframe
has the same sample count and remains within Kraken's response limit. The first
420 candles are validation data used to rank candidates; the ten strongest
diversity-first strategy/pair/interval candidates are evaluated on the untouched
final 180 candles. To keep work bounded, the candidate search evaluates the
largest top-liquidity subset that fits its 250-candidate ceiling.
Selection permits at most one active worker per strategy, so the ten-worker pool
tests ten distinct strategy families rather than filling slots with repeated
copies of one high-ranked family.
API callers may supply both endpoints of an explicit 10-to-40-day UTC range;
the end controls the completed-candle cutoff and the start controls the earlier
liquidity snapshot. A slot starts forward paper
trading as either qualified paper or clearly labeled unqualified exploration.
Exploration never grants live eligibility. The strategy gates remain a
non-negative return, at least three completed modeled round trips, and no more
than 20% maximum drawdown; a user may make these gates stricter but not weaker.
Fees and adverse slippage remain included.

During forward paper observation, every worker loads safe closed 5-minute,
15-minute, 30-minute, and 1-hour series for its pair. The selected primary
interval must produce the complete strategy signal. At least one of the other
three intervals must provide bullish directional confirmation through either
the same strategy condition or a higher closed candle than its predecessor.
Missing, stale, incomplete, or blocked evidence at any required interval fails
closed. At startup, the market-data worker loads 47 authoritative closed Kraken
candles for every active pair and interval so indicator warmup does not require
waiting for live candles and supporting timeframes retain at least 35 bars at
the latest primary-candle close.
The decision fingerprint commits to all four candle series for reproducibility.
The candidate catalog includes five hybrid setups in addition to the original
single-indicator families: RSI-MACD confluence, EMA-RSI trend, Bollinger-MACD
recovery, Donchian-volume breakout, and EMA-volume pullback. These are separate,
versioned strategies rather than hidden changes to the behavior of an existing
strategy.
The same confirmation may add to an existing long position only when the closed
primary candle is strictly above that worker's average entry. Each add consumes
one favorable mark, uses reduced risk sizing, and remains subject to cash,
exposure, quantity, notional, and per-position addition limits. Paper workers
never average down, borrow funds, or route an addition to a live exchange.

Development configuration enables the paper-only prerequisites and worker, but
the web, experiment-worker, and market-data processes must all be running:

```
dotnet run --project src/Trading.Web --launch-profile https
dotnet run --project src/Trading.Workers.Experiments
dotnet run --project src/Trading.Workers.MarketData
```

The `/experiments` workspace has separate **Setup workers**, **Workers**, and
**Results** tabs. Setup contains discovery controls and historical qualification
evidence. Workers is the default wide monitor; it refreshes every ten seconds
and shows each currently activated worker's strategy, EUR pair, candle interval, qualification
or exploration status, runtime state, cash, position quantity, fee-inclusive average
entry, latest stored 5-minute price, market value, unrealized and realized profit
and loss, trade count, and additions used versus the worker's immutable
per-position cap. Each of the ten most recent simulated ledger entries is displayed
on its own detail row beneath its worker, including time, direction, quantity, fill
price, fee, current average entry, and latest price. The monitor is owner-scoped
and read-only.

In Visual Studio, select the **Paper training** solution launch profile. It
starts `Trading.Web`, `Trading.Workers.MarketData`, and
`Trading.Workers.Experiments` together. Starting only `Trading.Web` leaves
activated slots at `WaitingForWorker` because no process exists to create or
run them.

Connect and validate a read-and-trade Kraken account with withdrawal permission
disabled, then open `https://localhost:5200/experiments`. Historical OHLC is read
through Kraken's public endpoint; the connected account is an ownership/readiness
gate and its credentials are never sent to that endpoint or to the browser.

For non-development environments, configure the prerequisites explicitly:

```json
{
  "ConnectionStrings": { "TradingDb": "Server=(localdb)\\MSSQLLocalDB;Database=Trading;Trusted_Connection=True;TrustServerCertificate=True" },
  "Experiments": {
    "PaperTraining": {
      "Enabled": true,
      "Prerequisites": {
        "DurableClosedCandleSource": true,
        "ApprovedResearchGroupsAndGates": true,
        "WorkerRiskPolicy": true,
        "PaperFillPolicy": true,
        "OutputLedger": true,
        "ProtectiveScheduler": true
      }
    },
    "ProtectiveExits": {
      "Enabled": true
    }
  }
}
```

The experiment host discovers owners from durable active SQL state; it no longer
requires an operator-maintained user-id allowlist. An Administrator or
RiskOfficer may start slots for an owner through
`POST /api/paper-training/{ownerId}/start`; an ordinary user is limited to their
own identity. The normal Stop control durably disables activation, and the
emergency-stop endpoint remains available.

Each Start creates a fresh set of child workers for only the accepted discovered
slots. Every symbol has independent fake cash, positions, random state, ledger,
and result state. The market-data worker merges configured subscriptions with
the symbols from every durable active paper activation and refreshes that set
every minute, so newly selected pairs receive forward closed candles without a
worker restart. With persisted closed candles available, each worker
re-fetches and revalidates exactly fourteen
chronological closed candles for plan geometry from the strategy's safe
chronological evidence set (currently at least thirty one-hour candles), and writes
decisions, execution claims, workers, and simulated paper ledger entries to
the database. The platform-owned strategy-to-plan mapping, exchange filters,
sizing ceilings, and risk evidence are constants; configuration and browser
input cannot select an adapter, quantity, or route. A simulated fill changes a
worker only after the mandatory paper pipeline has completed. Re-reading a
worker replays its immutable ledger, so balances and positions are
deterministic across restarts. It never registers a live/futures adapter,
exchange client, credentials, or network dependency.

Protective exits remain independently disabled unless `ProtectiveExits:Enabled`
is set. When it is enabled alongside paper training, the scheduler intersects
its configured owners with the durable active-training approvals. It reads an
open replayed paper worker and its immutable approved-plan stop/target evidence,
then only submits an exact closed-candle trigger through the durable
decision-claim and paper pipeline. Missing, stale, malformed, or unapproved
evidence is a no-op; a completed close is appended to the worker's durable
paper ledger, so its balance and position replay correctly after restart.

Fixed stop and target checks have priority. If neither is touched, the scheduler
may protect an already-profitable position after it reaches at least `0.5R`,
where `R` is the opening entry-to-stop distance. A full paper close then requires
all of the following closed-candle evidence: a confirmed 5-minute local peak and
lower high, RSI rolling down from at least 60, a weakening MACD histogram, and
falling price plus RSI on at least one of 15-minute, 30-minute, or 1-hour data.
All four timeframe series must be present, safe, contiguous, and fresh. This is
a deterministic momentum-reversal rule, not a claim that the exact market top
can be predicted. Missing or contradictory evidence keeps the position open.

## Opt-in Kraken Live Proving profile

The normal `https` profile cannot send a real order. To exercise the
supervised real-money path locally, explicitly select **Live Proving (Kraken)**
in Visual Studio or run:

```
dotnet run --launch-profile "Live Proving (Kraken)"
```

This profile is deliberately separate from the default and permits only the
development administrator, `SOLUSD`, `SOLEUR`, or `XBTUSD`, a maximum order
notional of 25 in the pair's quote currency, and a proving ceiling of 10. It is
still a real Kraken route: connect only
a read-and-trade key with withdrawal permission disabled, start at Proving,
and submit a small order you are prepared to place. Wait for an observed,
reconciled fill before promoting the account to Live. The seeded
administrator's stable rollout ID is
`8dd8e906-4ae0-4190-9082-64ec755c2913`; it is a local development identity,
not a secret or production authorization mechanism.

## HTTPS is the default profile

The launch profile binds `https://localhost:5200` first and
`http://localhost:5225` second.

The authentication cookie is issued with `Secure`, `HttpOnly` and
`SameSite=Strict`. Browsers and curl treat `http://localhost` as a secure
context, so plain HTTP does work locally — but it does not resemble how the
application is served anywhere else. Developing over HTTPS keeps local
behaviour and deployed behaviour the same, so a cookie or redirect problem
shows up here rather than after deployment.

A local development certificate is required:

```
dotnet dev-certs https --check --trust
```

## Known friction

- **A running app locks its own binary.** `Trading.Web.exe` must be stopped
  before the solution will rebuild, otherwise the build fails on a file lock.
- **Port 5200 is not shared.** Only one instance can bind it. Stop a
  command-line instance before starting one from Visual Studio.
- **`dotnet test Trading.sln` does not build `Trading.Web`.** Build the
  solution separately when checking web changes compile.

## Demo sign-in

Development seeds two accounts:

| Account | Email | Password |
|---|---|---|
| Trader | `trader@fremvo.local` | `DemoPassword123!` |
| Administrator | `admin@fremvo.local` | `DemoPassword123!` |

These exist for local development only. The passwords are verified: they are
stored as PBKDF2-SHA256 hashes and an incorrect password is refused.

Sign in at `/login`. That page does nothing but authenticate, and on success it
forwards you to `/chart`, or back to whichever page sent you there. Every
application page (`/chart`, `/positions`, `/orders`, `/exchange`,
`/experiments`, `/optimization`, `/admin/*`) redirects to `/login` when you have
no session. The navigation bar shows the signed-in account and a **Sign out**
button on every page.

If the development database was created before a schema change, the seeder
detects the missing tables **or columns** on startup, drops the database and
recreates it with the demo data. Locally seeded data is therefore disposable.
This exists only because there are no EF migrations yet and must never be used
in a deployed environment.
