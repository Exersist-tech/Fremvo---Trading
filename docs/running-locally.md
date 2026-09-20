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

## Opt-in public Kraken candle stream

`Trading.Workers.MarketData` is inert by default. To start its public OHLC v2
stream, configure a database connection named `TradingDb` and explicitly set
the symbols and native Kraken intervals, for example:

```json
"MarketDataStreaming": {
  "Enabled": true,
  "Symbols": [ "BTC/USD" ],
  "Intervals": [ "OneMinute", "FiveMinutes" ],
  "MaximumReconnectDelaySeconds": 30
}
```

Run it with `dotnet run --project src/Trading.Workers.MarketData`. This uses
only Kraken's public WebSocket endpoint; do not configure credentials, API
keys, or authorization headers. A candle is persisted only after a subsequent
interval proves it is closed.

## Opt-in paper-training worker

Paper training is disabled by default. It uses fake funds only, has no live or
futures route, and does not read exchange credentials. After preparing the
durable closed-candle, approved-group, risk, fill, ledger, and protective
scheduler prerequisites, configure the worker explicitly:

```json
{
  "ConnectionStrings": { "TradingDb": "Server=(localdb)\\MSSQLLocalDB;Database=Trading;Trusted_Connection=True;TrustServerCertificate=True" },
  "Experiments": {
    "EnabledUserIds": [ "PUT-THE-OWNER-GUID-HERE" ],
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
    }
  }
}
```

Run `dotnet run --project src/Trading.Workers.Experiments`. The owner requests
one to ten fixed catalog slots at `/experiments`; a different Administrator or
RiskOfficer must approve with a nonempty reference through
`POST /api/paper-training/{ownerId}/approve`. Use the disable or emergency-stop
endpoints to stop it. This host intentionally remains a no-op until durable
paper-only runner sources are supplied; it never substitutes live, futures,
credential, or network components.

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
