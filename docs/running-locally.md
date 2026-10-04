# Running locally

## Visual Studio

Open `Trading.sln` and press F5 or Ctrl+F5. The default **Paper training**
solution launch profile starts the web app on `https://localhost:5200`,
the public market-data/scanner host, and the experiment/protective-exit host.
The separate **Trading.Web only** profile is available for API/UI debugging
without automatic worker activity.
When the three hosts start together on a fresh development database, each
paper worker waits up to two minutes for the web bootstrap to finish creating
the complete configured SQL tables and columns. It does not create or repair tables
itself. If the schema remains unavailable, that host stops with an explicit
startup error; check the web bootstrap, connection string and database
permissions. Apply and review the baseline SQL migration before a fresh
deployed database is started.

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

From the repository root run `.\scripts\Start-PaperTraining.ps1` to build and
start all three processes together; stopping the command stops the worker
processes as well. `dotnet run --project src\Trading.Web --launch-profile https`
starts **only** the web process and cannot perform scans or advance admitted
workers. In deployed environments, configure and supervise the independent
worker services rather than spawning worker subprocesses from the web server.
On a device that prohibits PowerShell script execution, use the default Visual
Studio **Paper training** launch profile instead; do not weaken the device's
execution policy to run the convenience script.

Use the HTTPS address from the launch profile when signing in: the session
cookie is secure and an HTTP login does not establish an authenticated
subsequent request. Paper Start requires both host heartbeats and a recently
observed, safely closed native Kraken 1m or 5m forward-stream candle. After
Start, check the last completed scan's pair/evaluated/qualified/admitted
counts and the per-candidate explanations. A scan with evaluated candidates
but zero qualified/admitted workers is operating normally when required entry
checks do not pass; never relax the named triggers merely to force a trade.
Host heartbeats alone do not establish a working feed or a simulated fill.

## Linux App Service paper worker artifact

CI builds a single `trading-site-with-paper-workers` ZIP containing the web
app and two continuous WebJobs under `App_Data/jobs/continuous/market-data`
and `App_Data/jobs/continuous/experiments`. Each `run.sh` launches its
published .NET Worker Service and forwards shutdown signals. The build makes
the launchers executable before zipping; deploy the CI ZIP intact rather than
repacking it on Windows. Development-only appsettings are excluded from the
ZIP, and the Bicep deployment sets both .NET host environments to Production.
The jobs are configured as singletons, so scaling
the web site must not multiply scanner/exit loops. The Premium App Service
has Always On enabled. This packaging has **not** been deployed or
restart-tested against a production Azure environment.

Provision the Bicep template and publish the approved ZIP to its Linux web
site only after reviewing database schema and permissions. The Bicep
`paperWorkerHostsEnabled` switch defaults to `false`: both job processes may
start, but no scanner heartbeat, experiment heartbeat, or automatic paper
trade is enabled. An inert market-data host does not require an existing
SQL schema; enabling its public feed without a SQL connection fails startup
explicitly rather than silently running without persistence. After the
correct schema and feed/recovery supervision
exist, opt in explicitly; the switch enables public candle streaming,
experiment paper evaluation, and paper protective exits **together**.
It cannot enable live orders. Verify both continuous jobs report Running,
both independent SQL host heartbeats advance, the exact subscribed closed
candle boundaries advance across several intervals, a completed scan is
recorded, and a simulated entry and exit survive a job restart. The market
feed and protective position evidence must be checked separately from
heartbeats; stop new paper admissions and investigate if either goes stale.
`enforcePaperWorkerEntitlements` is a separate Bicep opt-in that defaults to
`false` and sets `Entitlements__PaperWorkerLimitsEnabled` consistently for
the web site and its two paper WebJobs. Local hosts also default to false;
set `Entitlements__PaperWorkerLimitsEnabled=true` for **all three** hosts
only after audited owner/plan assignments exist and an operator approves
the rollout. An MFA-verified administrator can now use the Accounts page to
create paper-only plans and look up owners by exact email, then assign or
revoke with a new six-digit authenticator code for **each** change. A code
used for sign-in or a previous change cannot be reused; wait for the next
authenticator step. Expired trials still occupy their assignment slot:
use **Extend paper trial** with a fresh code to set a later future UTC expiry
on the same paper-only assignment. This preserves its owner and plan; a
suspended owner or an unbounded, revoked, or live-eligible assignment cannot
use this operation. Revoke the old assignment before granting a different
plan. Renewal writes an immutable audit event and never enables live trading.
Administrators may search owner emails by a prefix of at least three
characters, then select a result for the existing exact-owner controls.
Owner search and invitation history page in stable, 50-item batches; neither
returns invitation codes.

The same admin Accounts section can issue and revoke a single-use invitation.
The code is shown only when issued and must be delivered privately. Registration
requires both the code and the assigned recipient email (case-insensitively);
a wrong-email attempt does not consume it. Invitations issued before the
recipient-binding migration have no recipient and cannot be redeemed; revoke
and reissue them. The invitation-code migration replaces previously issued
ASCII codes with SHA-256 digests, preserving email-bound invitations; non-ASCII
legacy codes are deactivated and must be reissued. Only the issuance response
contains the raw code; it cannot be recovered from SQL or re-displayed later.
The digest migration is one-way and cannot be rolled back to plaintext codes.
Revocation and concurrent registration are serialized by a
conditional SQL redemption update. The opt-in development seed's known demo
invitation is restricted to `new@fremvo.local`. Administrators cannot assign
live/futures-eligible plans through this paper-plan interface.

The owner lookup also permits MFA-verified suspension/reactivation of
non-admin users, requiring a reason. Suspension invalidates their login and
blocks new paper entries even if the plan-limit switch is off; open simulated
positions remain reserved for protective exits, so keep monitoring them.
With the paper-worker entitlement gate enabled, owners with missing, revoked
or expired assignments also admit zero new workers, and a plan allows at most
its `MaxExperimentWorkers` new paper reservations.
The signed-in owner's Accounts view reads its own plan, effective paper
capacity, and trial expiry; it does not expose other owners' assignments or
imply that plan eligibility enables a live trading route.
Activation and every exposure-increasing simulated entry also enforce the
limit; previously reserved slots and open positions remain for separate
protective exits/reconciliation. Never enable the flag for only one host or
treat an eligible plan as authorization for live or futures orders.
When `APPLICATIONINSIGHTS_CONNECTION_STRING` is supplied, both worker hosts
emit unsampled warning/error telemetry and information-level traces for
successful SQL host-heartbeat and accepted forward-candle proof writes, plus
each completed experiment tick (including ticks with no active workers).
Completed owner scans also emit a safe Information summary with counts; no
scan summary means the scanner did not persist a completed scan for that owner,
even if both hosts are reporting heartbeats.
The web host also registers Application Insights when configured, with
unsampled live-order warning telemetry for the two live anomaly rules. SQL
command text collection is disabled; never attach credential values to
application logs or telemetry.
The Bicep template declares six paper-only alerts (enabled only with
`paperWorkerHostsEnabled`): a missing scanner or experiment heartbeat, missing
forward-feed proof (both within ten minutes), a blocked/faulted protective
exit, a market-data loop or scanner failure, an absent completed experiment
tick despite host heartbeats, and any logged faulted worker or owner pool.
The tick alert detects a stopped worker loop even if a separate heartbeat
continues; the fault rule uses explicit nonzero-fault tick telemetry. A tick
with zero workers does not prove active workers are advancing. Retry
logs retain only the
exception type, never its message or stack trace. The
deployment-supplied action group must be configured and each rule's ingestion,
evaluation, delivery, and restart behavior tested before relying on them;
these alerts are not a substitute for checking exact pair/position freshness.
CI **produces an artifact, not a deployment**; there is no automatic
production migration, tested alert delivery, restart drill, or live-readiness
claim. Follow the [incident recovery runbook](disaster-recovery.md) before
restoring or restarting a deployed paper/live environment.
Production administrator authentication requires a password and a fresh
six-digit TOTP code verified against a SHA-256, 30-second secret in Azure Key
Vault. Configure the administrator account's MFA flag only after provisioning
an independently generated 32-byte (or longer) uppercase, unpadded Base32
secret in Key Vault as
`admin-mfa-<user-id-without-hyphens>`. Enroll the administrator's authenticator
with SHA-256, six digits and a 30-second period using a secure operator-managed
channel; never put the secret or enrollment QR code in SQL, source control,
logs, browser storage, or shell history. The web identity needs Key Vault
`get` access; without a correctly enrolled secret administrator login fails
closed (a missing or unavailable Key Vault secret returns HTTP 503 without
issuing a session). This is operator provisioning, not a self-service enrollment or
recovery flow. Validate clock synchronization and Key Vault access before
allowing production administrator operations.

To grant a new Administrator, an operator must separately provision that
user's Key Vault secret under the same `admin-mfa-<user-id-without-hyphens>`
name and deliver authenticator enrollment privately. The signed-in candidate
uses **Accounts → Administrator MFA verification** to prove possession of a
fresh code. Within five minutes an existing Administrator looks up that
active user in Accounts, supplies an audited reason and their own new,
unused TOTP to approve the role. An account without a password, user proof,
or operator-provisioned secret cannot be promoted. Existing candidate
sessions are revoked by role validation; the person signs in again with
password and a new MFA code. The application does not create or display MFA
secrets or replace an out-of-band recovery process. A verified Administrator
may remove another Administrator's role with a fresh unused code and audited
reason, but cannot remove their own role or the last active administrator.
Old sessions become invalid immediately. Revoke the former administrator's
MFA secret in Key Vault separately; removing the role does not delete the
secret. Newly promoted Administrators require MFA even in Development; only
the fixed, local demo Administrator retains the documented development
password-only exception. Do not share an authenticator code with another
administrator.

Every verified code is atomically redeemed with an immutable audit event;
the SQL primary key prevents a code's time step being reused across web
instances. The cookie is checked against that redemption and the current
account on each request; password-only and old administrator cookies are
rejected outside Development. Login has a per-process, per-remote-IP
ten-attempt-per-minute limit; apply a **distributed** rate limit at the
deployment edge as well, and configure trusted proxy forwarding carefully.
Existing user cookies are also invalidated
when their account is suspended, removed, loses its password, or changes
role or email; the authoritative account is checked on each request.
Standard-user paper sessions remain available.
Do not deploy relying on the development demo administrator for operations.
These WebJobs share the web site's managed identity and resources, so a
future isolated worker deployment is required for separate secret access
and independent capacity.

## SQL schema deployment

`Trading.Infrastructure.Data\Migrations` contains six EF Core SQL migrations:
the initial schema, administrator MFA redemption, Plan/Entitlement,
email-bound invitations, one-way invitation-code digests, and nullable live
position account binding. Existing live rows remain unbound and require
reconciliation before further same-pair fill accounting. Together they
provision a **new, empty** SQL database, including the EF migration history
table. The additive and data-conversion
migrations also apply to a database already managed by the preceding EF
migrations after operator review. This does not import, reconcile, or preserve a
database previously created by `EnsureCreated`. Never run this initial
migration against an existing non-empty database: review its SQL against the
target, back up existing data, and design a separately reviewed, non-destructive
baseline/upgrade path for that database. Do not delete production data to make
the baseline apply.

Install the matching EF Core 8 CLI (`dotnet tool install --global dotnet-ef
--version 8.0.8`) if it is not already available. Set
`ConnectionStrings__TradingDb` in the deployment environment from its secure
configuration (use Microsoft Entra/managed identity for Azure SQL, not a
password in source control or shell history). From the repository root, review
the generated script before executing it on an empty target:

```powershell
dotnet ef migrations script 0 HashInvitationCodes --idempotent `
  --project src\Trading.Infrastructure.Data\Trading.Infrastructure.Data.csproj `
  --startup-project src\Trading.Infrastructure.Data\Trading.Infrastructure.Data.csproj
```

Apply the reviewed script with a dedicated schema-deployment principal.
Application and worker identities should not be granted schema-write
privileges; neither web nor worker startup automatically runs migrations.
The opt-in `TradingMigrationIntegrationTests` verifies creation and
idempotence on a disposable LocalDB database using
`TRADING_SQL_INTEGRATION_SERVER=(localdb)\MSSQLLocalDB`. Before the fifth
migration on an existing installation, verify a recoverable backup: it
irreversibly replaces plaintext invitation codes with their digests.

To assess an existing database created with `EnsureCreated`, first back it
up and have a DBA review the actual schema and migration versions. Provision
a **separate, disposable** SQL reference database with all checked-in
migrations using the migration command above; do not point the CLI at a
production database as its reference. Set `TRADING_SCHEMA_TARGET` and
`TRADING_SCHEMA_REFERENCE` to distinct SQL connection strings through secure
process configuration (not command-line arguments or source control), then
run `dotnet run --project src\Trading.Tools.Schema -c Release`. The CLI needs
metadata visibility (`VIEW DEFINITION`) on both databases, but writes to
neither. It rejects migration-history tables, missing reference migrations,
and differences in tables, columns, indexes, keys, constraints or triggers;
it never prints connection strings. A match establishes **catalog parity at
one instant only**, not an adopted migration or a verified backup. If the
target differs, stop and design data-preserving upgrades for its exact
schema. For a matching target, stop application and worker writes and have
the DBA independently verify the backup, migration snapshot, SQL permissions
and parity immediately before reviewing any explicit baseline-history
script. **Never copy history rows from another database or run the initial
migration against the populated target.** No production adoption script is
executed by the application, workers, or this CLI.

Plans and entitlements are now persisted in SQL with unique plan codes and a
filtered unique index permitting only one active assignment per user.
Creating a plan, assigning an entitlement, or revoking one through the
repository requires an active administrator with MFA enrollment and writes a
same-transaction audit event. The Accounts console additionally requires a
fresh, single-use administrator TOTP for mutations. Trial expiry is checked
in UTC at lookup time, and an expired assignment remains active in storage
until renewed or explicitly revoked before a replacement. Paper-worker
capacity is enforced when the operator enables the same rollout setting in
the web and both worker hosts. **Eligibility is not permission to place real
orders**: the separately gated Spot live path also requires an eligible
owner plan and its other safeguards; Futures remains disabled. Do not
assign production plans or run the new migration on an unmigrated populated
`EnsureCreated` database without the reviewed adoption procedure above.

## Trading workspace

Accounts includes an owner-only display/reporting preference form backed by
the existing User locale, time-zone and reporting-currency columns. Choose a
supported locale, IANA time zone or `UTC`, and a three-letter ISO currency.
Workspace numbers and timestamps then use that locale and zone. The saved
currency does **not** convert displayed Spot or simulated amounts, and English
interface text remains English. A missing preference read displays an explicit
browser-formatting warning without blocking the trade view. Changing settings
writes an immutable owner audit event; no database migration is needed.
History can request an owner-only **paper worker transaction report** for a
completed UTC interval of at most 366 days. The report replays each included
worker's earlier simulated fills to establish fee-inclusive average cost
before valuing sales within the interval; proceeds, fees, and realized P&L
remain in each pair's native quote currency. The reporting-currency total is
available only when every included pair uses that currency; mixed currencies
are never converted or summed. Money is sent as exact invariant decimal
strings, not browser floating-point numbers. Requests with more than 500
interval entries or 20,000 prior entries for those workers must be narrowed
or investigated; any unresolved owner paper execution preceding the end of
the interval blocks the report. Successful report generation is audited
without recording the individual trade values in the audit event. This is
informational simulated activity, not a tax, legal, or financial statement.
Overview and History show positive paper P&L in green with an explicit `+`,
negative P&L in red with `-`, and missing values without either color.
History's paper-report values retain their exact native decimal text.
Worker cards in Trade and Overview show the weighted execution price of BUY
fills still held in the open position, followed by signed open P&L when
fresh closed-candle valuation is available. A flat worker with recorded fills
shows its last BUY and SELL prices and signed realized P&L. Idle slots do not
show invented prices or P&L. Scan timestamps are omitted from each worker card;
the Trade panel warns if the scanner is overdue, and the selected worker's
folded evidence retains the scan time. Fee-inclusive cost basis is not shown
on the cards because it is not an execution price. For currently assigned
workers, History also shows up to ten recent recorded fills per slot with
side, quantity, execution price, fee and time.
When a worker is flat and completed or failed, the closed-worker summary
shows quantity-weighted BUY and SELL fill prices, signed net P&L, and
opening/closing times. Net P&L still includes trading fees even though the
headline views omit fee-inclusive cost basis. These are simulated paper fills,
not strategy targets or live Kraken execution prices. The recent-fill section
is not a complete historical ledger; request the paper transaction report for
an owner-scoped interval when complete fill history is needed.
The History form can also save a private, immutable JSON copy in Blob Storage.
Set `Reporting:ContainerUri` to the HTTPS URI of a private existing Blob
container and grant the web app managed identity **Storage Blob Data
Contributor** on that container; no connection string or SAS is stored in the
app. The Bicep template provisions a separate `paper-reports` container with
public access disabled and this scoped role. Without configuration, preview
reports still work and the export request returns HTTP 503 (not a fake saved
report). Blob objects are create-only and organized by owner/report ID.
The owner-scoped SQL audit records the exact content hash, UTC interval,
currency and byte count; the app checks the hash before every download.
History lists the latest 50 private exports. Saved exports and successful
downloads are separately audited, and each authenticated download bypasses
browser caching. A failed audit write after Blob upload can leave an orphaned
object, but it cannot be downloaded through this API without the owner's
matching audit record. Investigate and clean up orphans using supervised
storage operations, never by exposing the container publicly. Blob retention
and purge policy must be set by the operator; exports contain sensitive
financial history. No new SQL table or migration is required for these
paper-only exports. The optional `GB` format is a separately registered
presentation example with `dd/MM/yyyy` dates in the user's selected IANA
time zone (including DST); it retains UTC timestamps and native-currency
amounts in the general report. It is **not** a UK tax return and applies no
country tax rules. Unknown format codes are rejected; the format is chosen
per request and does not change or silently overwrite the user's saved
reporting preferences. FX conversion and live fills are not provided.

After signing in, use `/workspace/trade`, `/workspace/portfolio`,
`/workspace/history`, `/workspace/overview`, `/workspace/strategies`, and
`/workspace/accounts`.
The root URL and default post-sign-in destination open `/workspace/trade`;
legacy pages such as `/chart` remain available at their existing URLs.
All workspace pages use the available screen width without the legacy
1440px content cap. Trade uses the full desktop height with an enlarged,
flexible chart between worker and ticket sidebars; Portfolio, History,
Overview, Worker strategies, and Accounts also use the full width and scroll vertically when
their content requires it. Trade adapts to smaller screens by stacking its
worker, chart, and ticket panels.
The workspace loads a changed `workspace.js` module on the next section
navigation. Reload an already-open browser tab after a CSS change or when
Visual Studio restarts the web process.
Automatic worker
trading is separate from the manual ticket: after you start the paper scanner,
eligible approved signals can create simulated trades while both the scanner
and experiment worker processes are running. Workers remain reserved until
their paper position is flat and reconciled.

In Trade, click a worker slot to inspect its approved rule, required
timeframes, simulated balances/position, recent scanner observations with
consensus counts and rejection reasons, recorded pre-risk decisions, and
actual simulated fills. If a slot has an admitted pair, selecting it opens
that pair at its signal interval on the chart (including 15-minute or daily
signals). The worker list shows the latest completed scan time above each
pair and colors realized/unrealized gains and losses separately. Decision
markers carry their recorded reason; fills use separate markers. Selecting a
worker displays its strategy-specific overlay guide and enables the relevant
EMA/RSI/MACD/Bollinger panes. Current EMA/RSI/MACD lines and three-swing pivots are visual context,
not a historical replay of the full multi-timeframe decision or a guarantee of
an order. The three-swing channel overlay is shown only for a selected
three-swing worker. Donchian workers additionally draw prior closed
20/55/100-candle channel highs and lows at their signal interval; compression
and session workers draw the prior 20-candle channel. Gapped or unclosed candle
windows produce no line. These lines are recalculated visual context rather
than persisted numeric decision inputs, and off-chart higher-timeframe gates
remain described by the worker's recorded evidence. The Worker strategies
page exposes all eleven approved strategy templates and persists bounded rule settings for each, including
strategies not currently assigned to a worker, alongside the per-slot
assignments. Three-swing channel divergence can be assigned to worker slot 3
like any other approved template; while the slot is awaiting admission, its
monitor entry shows **Scanning** with that slot's seed, not a simulated fill.
Reassigning a slot does not replace an existing worker or its open position;
the selected template applies to its next eligible admission. These settings
are stored in the existing owner-scoped
paper-training activation JSON payload; no schema migration or API-secret
storage is involved. The Worker strategies rule editor explains each control's input, how raising
or lowering it changes future paper entries or exits, and whether a fixed
approval bound prevents edits. Selectable protection models have readable
labels; saved legacy models remain visible for review but cannot authorize new
paper admissions. Periods count closed candles in the relevant timeframe.
Weight groups must retain their required total and ordered lookbacks/zones
must remain valid; changing a setting cannot bypass platform risk gates.
Settings can be saved while the scanner is active and
paper positions are open; a competing scan causes a conflict rather than
silently overwriting either change, so reload and retry.
Existing workers and their positions keep the strategy-parameter snapshot they
were admitted with; new settings apply when a slot next becomes available.
If persisted family settings and a worker assignment disagree or cannot be
read, the editor shows a review error and refuses the whole save; it does not
choose one copy or overwrite either with defaults.
Newly admitted paper slots pin rule version 5 for the
regime ensemble, EMA, Donchian, Bollinger, RSI, MACD, compression and
relative-strength pullback; cross-sectional momentum and
three-swing divergence use version 4, and session breakout uses version 3. Older reserved
slots keep their original rule version across a restart. Relative-strength
version 5 requires a one-hour confirmation closed strictly after its four-hour
setup; the worker's paper decision and sizing use that confirmed hour.
The version-5 regime ensemble pins selected components at version 4:
it uses an independent ensemble four-hour structural plan, **not** a
standalone family's v5 stop, and does not silently adopt a newer component.
Existing saved relative-strength settings are
flagged for review in Worker strategies before new version-5 admissions; the
owner selects both the daily excess breadth ranking and four-hour structural
paper protection after stopping the scanner. New version-5 paper entries then
use a stop below the configured closed four-hour swing plus ATR buffer and an
estimated target at the prior channel high; entries without enough gross
reward relative to stop distance are rejected. The closed one-hour
confirmation supplies the entry reference; this simulated entry price is not
an executable next-market quote. Existing open workers retain their recorded
numeric stop/target and are not repriced when settings change.
New Donchian version-5 paper positions freeze a stop below the prior breakout
channel low plus the saved ATR buffer and estimate a gross risk-multiple
target. Pre-existing Donchian settings require the owner to choose "prior
break range" in Worker strategies before new slots can be admitted; older
reserved trades retain their earlier numeric ATR protection. This is a
closed-candle paper hypothesis, not an exchange-native stop or validated
profit estimate.
New Bollinger version-5 paper positions freeze a buffered stop beneath
the closed excursion/re-entry low and an estimated target at the signal's
configured middle band. The saved minimum gross reward/risk is checked again
at the later paper price. Previously saved nonempty Bollinger settings must
be reviewed to select "excursion mid-band" before new slots are admitted;
existing positions retain their numeric protection and original exit rule.
This is not measured profitability; costs and forward validation remain open.
New RSI pullback version-5 paper positions freeze the signal's configured
closed swing low with an ATR buffer as their stop and estimate a saved
risk-multiple target. The later closed one-minute paper reference price must
still meet the saved minimum gross reward/risk. Existing nonempty RSI settings
without the new plan-model field require owner review to select "pullback
swing" before new entries. Existing reserved trades keep their own numeric
stop/target and version-4 invalidation; automated live trading is unaffected.
New MACD acceleration version-5 paper positions freeze the crossing signal's
configured closed swing low with an ATR buffer as their stop and estimate a
risk-multiple target. A later safe closed one-minute paper price must retain
the saved minimum gross reward/risk. Existing nonempty MACD settings without
the new plan-model field require owner review to select "cross swing" before
new entries. Earlier reserved version-4 positions retain their recorded
ATR protection and indicator invalidation. New positions also invalidate on
a later closed signal breaking the frozen opening swing. This does not
establish a profitable strategy or account for executable spread and fees.
New EMA continuation version-5 paper positions independently verify the
closed pullback and price resumption against durable signal candles. They
freeze a buffered pullback swing stop and estimated risk-multiple target.
The later closed one-minute reference price must retain the saved minimum
gross reward/risk. Existing nonempty EMA settings without the new plan-model
field require owner review to select "pullback swing" before new admissions;
older reserved positions retain their original numeric ATR stop and EMA exit.
New positions also exit if a later closed signal breaks the frozen swing.
Neither the simulated reference price nor these levels establish net edge.
New compression breakout version-5 paper positions re-read the closed
prior channel and bounded-volume break from the scanner's durable candles.
They freeze a stop below the **prior** range low with a saved ATR buffer and
an estimated risk-multiple target. The later closed one-minute price must
still offer the saved minimum gross reward/risk. Previously saved nonempty
settings without the new plan model require owner review to choose "prior
range" before new admissions; reserved version-4 positions retain their
recorded ATR stop/target and frozen-boundary invalidation. This is not a
claim of profitable execution after fees and spread.
New cross-sectional momentum version-4 paper admissions pin the complete
eligible-universe symbol list; the worker requires that exact list and replays
each member's 320 safe closed daily candles at the decision boundary. A
prior closed daily swing low with a saved ATR buffer freezes the paper stop,
and the saved risk multiple sets an estimated target. A later safe closed
one-minute reference price is rejected if the remaining gross reward/risk
falls below the saved minimum. Previously saved nonempty momentum settings
without `momentumPlanModel` require owner review to select "daily swing"
before new admissions. Existing version-3 positions keep their numeric
protection; version-4 positions additionally invalidate on a later closed
daily break of the frozen swing or saved trend EMA. This is not a measured
cost-aware backtest or evidence of a profitable rotation strategy.
New three-swing divergence version-4 paper admissions independently replay
the configured bullish pivot/RSI, 5-minute channel, 1-hour and 4-hour
context, and enabled reversal/MACD checks from 320 safe closed candles on
each timeframe. The confirmed third swing/reversal low plus a saved ATR
buffer freezes the stop; a saved risk multiple estimates the target. A later
one-minute paper price must preserve the saved minimum gross reward/risk.
Previously saved nonempty settings without `threeSwingPlanModel` require
explicit owner review to select "confirmed pivot" before new admissions.
Version-4 positions also invalidate on a later closed 5-minute break of the
frozen opening channel low or (when selected) MACD reversal, independent of
numeric stop and target. Older reserved version-3 positions keep their
original numeric protection. Bearish divergence never opens a Spot short.
The estimates are not executable quotes or a demonstrated profitable edge.
New regime ensemble version-5 paper admissions require a pinned and replayed
v4 component signal at the same closed four-hour boundary as the ensemble.
The ensemble freezes **its own**, explicitly labeled stop beneath the prior
closed four-hour swing plus a saved four-hour ATR buffer, and estimates a
gross target at the saved risk multiple. This is not the selected component's
stop: only the selected decision identity is carried into the plan evidence.
The first later safe closed one-minute paper price must retain the saved
minimum gross reward/risk. Nonempty legacy ensemble settings without
`regimePlanModel` need owner review to choose "four-hour swing" before new
admissions. A later closed four-hour break of the opening swing invalidates
new positions; existing v4 positions keep their original numeric protection.
This does not qualify the ensemble or any selected component for live trading.
The editor covers approved indicator periods, entry thresholds, regime/session
filters, ranking controls, and maximum holding intervals. Mandatory platform
risk ceilings, stale-data checks, paper-only execution, exchange filters,
idempotency, and protective-exit controls cannot be weakened through the editor.
Version-4 positions apply the saved EMA, Donchian exit-channel, Bollinger,
RSI/long-exit level, MACD and compression exit-channel choices to new paper
invalidations. Compression positions freeze their pre-break boundary at
admission; changing settings after entry never redefines an open position's
exit. These versioned invalidations require an attested opening close and a
later closed signal candle; missing provenance is reported rather than
silently treated as an invalidation decision. Numeric stops and targets
remain independent. Invalid persisted holding settings require review
instead of reverting to a default timeout. Earlier reserved positions retain
their previous invalidation rules.
Cross-sectional scoring weights and relative-strength scoring weights are
validated to sum to one before they can be saved.
The five-check entry rules require four or five confirmations; named setup
and entry triggers cannot be replaced by unrelated positive votes. Previously
saved three-confirmation values are shown for review, not silently rounded:
select four or five before saving. If a saved strategy document cannot be
read or contains unsupported fields, the editor refuses to overwrite it.
The strategy audit found a corrected volatility-penalty direction; this is a
mechanical safety fix, not proof of a profitable momentum strategy. Assets with
equal ranking measurements now receive equal ranks rather than a symbol-based
advantage. The
relative-strength ranking and strategy-version baseline still require
research before any claim of outperformance or live eligibility.
New consensus decisions
store the directional status of
each of their five approved checks in the reason when it fits the durable
512-character record; older decisions retain their original summary only.
Normal Stop stops new admissions and opening/addition worker ticks but keeps
the one-minute market-data subscription and protective-exit schedule running
for an owner's open fake positions until they are flat, even if an individual
worker is paused or failed. A failed worker remains failed after its protective
sell; its closed result still appears in paper history. It does not liquidate
positions merely because Stop was pressed; emergency stop remains a separate
control. Emergency stop also stops new admissions and exposure-increasing
worker ticks but retains one-minute subscriptions and the close-only
protective schedule for open paper positions. Once flat, those subscriptions
and owner protection ticks cease. Inspect open positions and protection
status before shutting down
the market-data or experiment host. A recorded candidate never
becomes a fill merely because it was admitted, and no live trading is enabled.
For new sized paper entries, leave both the market-data and experiment hosts
running after admission: an exact, safe, closed one-minute candle **after**
the strategy signal is required before the worker sizes or simulates an
entry. Until then the reserved worker waits; it expires unfilled after five
minutes, or sooner if its plan becomes unsafe. Sizing and the simulated
pipeline use that successor's close, while the decision and frozen stop and
target remain pinned to the original signal. This later closed price is a
paper reference, not proof of an available exchange quote or a profitable
fill. Do not interpret an unfilled admission or a host heartbeat as evidence
that a trading strategy is working.
The scanner host needs outbound access to Kraken's public `AssetPairs`
endpoint: it pins an active pair's published tick, quantity step, minimum
quantity and `costmin` to the reserved paper slot. If that reference data
is missing or the later candle closes off-tick, the experiment host does
not simulate an entry. It neither contacts Kraken nor uses an API key.

Paper pipeline market events, decisions, intents, risk evaluations, commands,
and portfolio updates are written as immutable, owner-scoped `PaperPipeline.*`
records in the existing SQL audit table. These records survive an experiment
host restart, but a claimed/unknown order is deliberately **not** resubmitted
automatically. Investigate its durable execution association, worker ledger,
and audit correlation before manually reconciling it. The risk halt controls
are audited in SQL and read by the web and experiment execution paths on
each trade. An emergency halt blocks new exposure while a verified
position-reducing protective sell still passes the halt gate. Closed-candle
freshness and duplicate protection remain mandatory for exits. An isolated
LocalDB integration test (`TRADING_SQL_INTEGRATION_SERVER=(localdb)\MSSQLLocalDB`)
checks concurrent claims, owner isolation, pipeline rows, heartbeats, and
cross-context halt reads, and removes its uniquely named test database.
Production SQL deployment, a real multi-host restart drill, and market-data
continuity telemetry are still required
before claiming unattended operational or live readiness.

For a worker showing `RequiresReconciliation`, keep it frozen and investigate
using **read-only** queries against the owner's database (substitute the
actual owner and worker IDs). Status `0` is Claimed and `3` is Unknown:

```sql
DECLARE @Owner uniqueidentifier = '00000000-0000-0000-0000-000000000000';
DECLARE @Worker uniqueidentifier = '00000000-0000-0000-0000-000000000000';
SELECT CorrelationId, Status, ExecutionCommandId, Detail, Symbol, AsOfUtc
FROM ExperimentPaperExecutionAssociations
WHERE UserId = @Owner AND WorkerId = @Worker AND Status IN (0, 3);
SELECT Id, Symbol, Direction, Quantity, ExecutionPrice, Fee, OccurredAtUtc
FROM ExperimentPaperTradingLedgerEntries
WHERE UserId = @Owner AND WorkerId = @Worker
ORDER BY OccurredAtUtc, Id;
SELECT Action, TargetId, OccurredAtUtc, After, CorrelationId
FROM AuditEvents
WHERE ActorUserId = @Owner AND CorrelationId IN
    (SELECT CorrelationId FROM ExperimentPaperExecutionAssociations
     WHERE UserId = @Owner AND WorkerId = @Worker AND Status IN (0, 3))
ORDER BY OccurredAtUtc, Id;
```

Compare the command, pipeline update, execution audit, ledger entries and
replayed worker position. A recorded command alone does **not** prove a fill;
a pipeline update alone does not prove the worker ledger was saved. If the
evidence does not prove both the simulated execution outcome and the exact
worker balance/position, keep the freeze and escalate; absence of a fill
record never proves that the adapter did not produce one. **Do not** change
association status or worker balances with SQL, resubmit the decision, or
restart repeatedly in an attempt to clear it. The original `Claimed` or
`Unknown` outcome cannot be rewritten.

For an affirmative, fully recorded **paper fill only**, a production-MFA-verified
administrator can perform a supervised recovery. First stop the experiment
host (including every instance); verify it has actually exited and supervise
any open paper positions while protective exits are unavailable. A stale SQL
heartbeat is required but is **not** proof the process stopped. Investigate
the owner/worker's `/api/experiments` unresolved correlation and the read-only
records above, then send an authenticated JSON POST to
`/api/paper-training/{ownerId}/workers/{workerId}/reconcile-filled` with
`{"correlationId":"paper-...","experimentHostStopped":true}`. Never set
`experimentHostStopped` while an instance may still advance the worker.
The server serializes recovery on that worker and requires exactly one
matching paper command, filled execution, portfolio update, worker's latest
trade (including replayed balances), and trade success audit. An open position
also requires an eligible worker and approved protective plan. A 409 keeps
the worker frozen; 404 means the owner/worker/correlation no longer has an
unresolved claim. Only after an HTTP 200 and checking the immutable
`PaperExecution.FilledReconciled` audit record should the experiment host be
restarted and its feed, protective exits, and paper-worker state monitored.
The old claim retains its first outcome and cannot submit the same decision
again. Missing or contradictory evidence is **not** resolved by this route;
it requires separate investigation, not a manual balance adjustment. The
former public audit-write endpoint is no longer available, and listing audit
records requires an administrator session.
A bounded isolated LocalDB smoke launch returned HTTP 302 from the web app
and recorded both host heartbeats while all three processes were running.
That confirms startup, not a qualifying scan, an order, a synchronized
market-data stream, or sustained protection.

The Trade sidebar displays the last **durably completed** five-minute scan
boundary from the activation ledger, or explicitly says when no scan has been
recorded. `Scanning` means a configured idle slot, **not** a process health
check. The scanner and experiment hosts write independent one-minute SQL
heartbeats; Trade labels either heartbeat missing/stale after two minutes.
The scanner first attempts each five-minute boundary 30 seconds after it
closes, giving Kraken public REST history time to settle; this grace does
not prove history is complete. If history is still unavailable, the exact
closed-boundary checks reject it rather than substituting a forming bar.
Trade shows the first scan as pending until the following five-minute
boundary plus grace; after a completed scan it warns if two further boundaries
plus grace pass without a newer completed one. A stale host heartbeat still
warns immediately, independent of scan timing.
Heartbeats prove only that the hosts are alive, not that the market-data stream is
synchronized, a worker submitted a trade, or protective exits succeeded.
A completed scan also reports eligible, evaluated, qualified, admitted,
and durable-evidence-blocked counts; zero admitted does not mean the worker
host is broken or that Kraken produced a qualifying signal. If the time does
not advance after the next closed five-minute boundary,
check the market-data host and its logs. If an admitted slot says
`WaitingForWorker`, check the experiment host. Both processes start when using
the default Visual Studio profile or the PowerShell command above, but a
completed historical scan alone does not prove either service is still alive.
Starting a scanner uses public Kraken data and fake funds; it does not require
private API keys or a validated trade-enabled exchange account. Server-side
paper prerequisites still apply; Start refuses to activate if either host heartbeat is
missing, more than two minutes old, or in the future. It also requires an
accepted, safe, native closed one- or five-minute candle observed from the
forward public WebSocket within ten minutes of both its close and the current
request. Historical REST backfill cannot satisfy that gate. Wait for the
market-data stream to produce a completed candle, then retry. The proof is
stored at most once per minute as an immutable `PaperHost.ForwardCandle` audit
event with only the public symbol, interval, and UTC close time; no schema
migration is needed.
One recent candle does not prove continuity for every pair, protective-exit
health, or a fill; per-order freshness checks still apply. The Trade worker
panel shows a compact `Feed observed` / `Feed unverified` badge from the
authenticated readiness query, separate from host heartbeats and scan time.
This gate never registers a live order route. Live worker activation is not
implemented.
The opt-in LocalDB endpoint test (`TRADING_SQL_INTEGRATION_SERVER`) verifies
the authenticated paper Start flow without any exchange account, including
missing, partial, and complete host-heartbeat states and forward-feed proof.

The market-pair picker displays the full active-pair list when its search is
empty and filters immediately as each character is typed.
The Trade chart stacks an RSI 14 pane above a Kraken-colored candle chart with
volume, and a MACD 12/26/9 pane below it. RSI and MACD are enabled by default;
the top-line **Indicators** dropdown independently toggles the panes and
EMA 20/50 or Bollinger Bands beside Draw channel and Draw line.
The selected pair's public Kraken trade-triggered ticker feeds a fine dotted
**Last trade** reference line across the price plot, ending in a
symbol/price badge on the right-hand price scale. A recent trade is red
when below the latest closed candle's price, green when at or above it.
Only the currently selected pair is requested. Alongside the trade stream,
the browser requests that pair's public Kraken best bid/ask every three
seconds. A more recent midpoint is labeled **MID BBO**, not a trade or an
executable fill price; the received timestamp is the time the quote was
retrieved, not a venue trade timestamp. New trade updates and quote responses
cannot replace a newer price from the other source. When the venue or network
cannot supply a fresh quote or trade, the guide remains gray as **Last quote**
or **Last known** trade; a newer closed candle takes priority and is labeled
**Closed 5m** (or the selected interval). A snapshot is shown as **Snapshot**,
never as a live trade. The status shows the source's timestamp, marks fallback
prices **not live**, and reports quote refresh failures. In a historically panned
or price-zoomed view, an up/down arrow on the edge badge means the actual
price is outside the visible scale; the chart's pan and zoom do not move.
Kraken's catalogue can use a compact trading identifier such as `0GEUR` while
publishing `0G/EUR` as the market stream name. Public price requests use the
catalogue's slash-form name; worker assignments and paper fills keep their
original trading identifier. Pairs without a valid stream name show unavailable
rather than repeatedly requesting an invalid symbol.
Hovering within the candle-price plot adds a separate dashed cursor guide and
the price under the pointer on the right-hand scale, using the currently
visible zoomed and panned price axis. The hover badge moves clear of the
live-price badge when they meet. Hovering the volume area, price scale, or
indicator panes does not claim a cursor price. The cursor guide is a visual
readout only; it never supplies prices to workers, orders, or valuation.
Selecting a worker shows its recorded simulated BUY fills as green up-arrows
and SELL fills as red down-arrows at their execution prices. Hover directly
over an arrow for the worker, side, quantity, fill price, pair, and execution
time. The larger arrows sit slightly below BUY fills and above SELL fills;
a short connector marks the exact execution level without covering the candle.
The current open position's first BUY remains available even if it is
older than the ten recent fills shown in worker evidence. When a fill falls
outside the loaded candles, a chart-edge arrow is explicitly labeled
**Before visible candles** or **After latest candle** in its tooltip; the
edge is not represented as the original execution time on the candle axis.
These are paper executions, not pre-risk strategy decisions or live fills.
For a selected worker with an open position and a matching approved paper
plan, the chart also draws its recorded protective stop and, if one exists,
its **estimated exit target** as dashed levels with prices. No target is
inferred when the saved plan has none. The estimated exit is neither a placed
order nor a guarantee of execution, and it is removed when the position is
flat or lacks an eligible plan.
With no valid trade, snapshot, or closed candle there is no trustworthy
price to draw. The guide is redrawn every second and is never
used for worker signals, paper fills, live orders, or P&L. The chart refreshes
venue candles every 15 seconds while visible, but candle intervals remain
5m/15m/1h/4h/1d and strategies still require closed candles. The separate
one-minute closed-candle feed remains responsible for paper execution and
protective evaluation, not this visual line.
The selected worker's strategy and execution evidence stays folded when
choosing a worker; expand it deliberately to inspect the details. The strategy
chart guide remains in its own collapsed disclosure within that evidence,
below the chart rather than above the RSI pane.
The chart
and indicator panes have more height and the sidebars leave more horizontal
chart space. Scrolling over the
main plot zooms the candle history around the cursor; Shift+scroll or horizontal
scroll pans it. Dragging over the plot pans both time and price, while dragging
or scrolling over the right price scale zooms vertically around the value under
the pointer. Each indicator pane scroll-zooms its vertical scale; dragging its
plot pans that indicator vertically and pans time in sync with the candle
chart; dragging downward moves the plotted data downward. Dragging the
indicator's right value scale zooms vertically around the
pointer. Indicator zoom does not change the price scale, and price zoom does
not change RSI or MACD. Axis bounds keep a portion of the plotted data in view
instead of allowing a pan to lose the series entirely. Zoom settings are not
silently reduced if the first candle load contains fewer bars than requested.
Zooming out stops at the candle capacity of the current canvas without
unexpectedly panning into older data; dragging still pans at that limit.
Switching pair or
timeframe resets vertical scales to fit the new data; Latest returns to the
newest candles and Reset restores the default chart view. Hovering RSI shows
the candle time, close price, and RSI value; MACD shows its line, signal, and
histogram values. The vertical crosshair remains aligned across all three
panes. All chart preferences, including the selected pair, timeframe, indicator
toggles, scales, and chart zoom/pan, are remembered for the signed-in user when
moving between workspace tabs. Running worker rows are highlighted in green
and remain visually distinct from the blue selected-worker highlight. The worker and ticket
sidebars are narrower on desktop to give the chart more width, and recent paper
activity is available as a tab in the manual ticket rather than taking chart
height. User-drawn trend lines and parallel channels are also restored when
returning to Trade. Activate **Draw channel**,
click two points along one edge, then a third to set the parallel edge offset.
**Draw line** uses two clicks. Both drawings stay tied to UTC time and price as
you switch between 5-minute, 1-hour, and 4-hour views while the Trade view is
open. **Clear** removes drawings for the selected market. Hovering shows
candle time, OHLC, volume, enabled indicator values, and any decision/fill at
that candle. The chart plots the latest three-swing pivots, the strategy's
rolling 50-candle 5m range, pre-risk worker decisions with their recorded
strategy reasons, exact worker fills, and open-position entry/stop/target
levels. Decision markers are not fills: separate execution markers show what
actually filled.

The Trade ticket includes a live, public Kraken Spot order book with the top
ten bid and ask levels. Its same-origin WebSocket stream subscribes to Kraken's
public v2 book channel and checks each snapshot/update against Kraken's CRC32
checksum before showing it. It uses no exchange API keys, is read-only, and is
not an order, fill, or execution-price guarantee. A lost or invalid feed is
marked disconnected and reconnected with backoff; an explicitly rejected
subscription stops reconnecting until a pair is selected again. Displayed
depth is cleared when synchronization is lost. Closing or changing the
browser's order-book stream cancels its upstream Kraken subscription without
waiting for the next market update; this does not affect scanner or worker
market-data subscriptions. If the browser reports that the relay could not
connect, first confirm the web host is running at the page's origin. The route
accepts HTTP/1.1 WebSocket `GET` and HTTP/2 extended `CONNECT`; accepting only
`GET` makes a direct HTTP/1.1 probe work while browsers negotiating HTTP/2
fail their handshake. That message alone does not establish a Kraken feed
failure. A separate HTTP 503 from three-swing evidence is a reported public
candle-source failure, not an order-book handshake or a valid signal; inspect
its response message and the host's upstream connectivity.

Portfolio displays live Kraken balances and fake-fund paper positions in
separate, labeled sections. Balance and position tables right-align exact
decimal values, show freshness/stale status, and scroll horizontally on narrow
screens rather than squeezing columns into unreadable text. Kraken `BalanceEx`
requires Funds Query permission and reports spot trade holds and its
available-for-trading calculation; other restrictions may still apply. A
manual live order (disabled by default) must freshly read the selected owned
account's balance and prove sufficient unreserved non-credit base or quote
assets before exchange submission. An unavailable or stale balance blocks
the order rather than using an earlier portfolio reading. Live submission also
requires Kraken's `Orders and trades - Query open orders & trades` API key
permission: a read-only `OpenOrders` call must explicitly return an empty book
before reservation and again before venue contact. Missing permission, an
unavailable response, or any open order (including one placed directly at
Kraken) blocks. The requested pair's base balance must also exactly match
recorded live long exposure on the selected account before and after
reservation; an untracked holding or sell blocks. This does not reconcile
other assets held externally.

The authenticated live Orders view and the web host's minute-cadence,
read-only reconciliation worker refresh exchange fills before attempting
owner-scoped reconciliation of unknown live orders. The worker runs even when
the live order route is disabled, so an existing account can still recover.
An exchange status reporting a fill does not resolve a frozen order until
complete trade evidence has updated the position and order together.
An incomplete query, missing fill, or contradictory absent-order reply leaves
the order frozen: never resubmit it on the strength of a status message alone.

The workspace is paper-first: its Spot ticket submits simulated market orders
only when you explicitly submit it, and Spot Sell is limited to reducing an
existing long. Unsupported
Limit and Trigger orders are disabled rather than simulated with different
semantics. Optional stop-loss and take-profit prices are validated and stored
with paper positions; they trigger only when the protective evaluator runs.
The three-swing evidence view uses closed 5-minute candles with 1-hour and
4-hour context, and shows confirmed pivot, RSI, MACD, and channel evidence.
The Kraken account page stores credentials server-side and validates
permissions, but connecting an account does not enable live orders. Futures
execution remains separate and unavailable in this deployment.

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
keys, or authorization headers. The WebSocket closes a forming candle only
when a later interval arrives. The worker also checks authoritative closed
REST history at each subscription refresh (once per completed boundary per
symbol/interval), so a quiet pair need not wait for its next trade to
persist a closed candle. Unsafe, incomplete, gapped, or conflicting history
is not silently repaired or treated as a valid trading signal.
If `MarketDataStreaming:Enabled` is false (the non-development default),
the market-data host does not run its continuous scanner or report a scanner
heartbeat; paper Start therefore refuses to claim it is ready. Configure
the public feed before starting paper training, even when no pairs have
yet been admitted.
Kraken REST uses `XBT/USD` and `XDG/EUR` for the BTC and DOGE pairs, but the
public WebSocket v2 channels accept only `BTC/USD` and `DOGE/EUR`. The connector
maps these names at its boundaries; older worker/position symbols remain
stable. The worker-host default logging suppresses EF's per-command
Information messages while retaining warnings and errors. A rejected public
OHLC subscription raises an explicit market-data error rather than leaving
the stream silently waiting for candles. Kraken v2 also permits only one OHLC
interval per symbol **per connection**: the connector uses a separate socket
for each subscribed interval and merges closed-candle updates through a
bounded channel. Multiple intervals on one socket trigger a rejection and
reconnect loop, not additional timeframe coverage.

## Offline Kraken archive import for research

The separate `Trading.Tools.History` command accepts a **trusted operator**
copy of one official Kraken OHLCVT `PAIR_MINUTES.csv`, its matching pair
symbol, and its native interval. A filename/pair/interval mismatch fails
before import; this check does not authenticate a forged file.
It does not accept user-uploaded strategy code, use exchange API keys, place
trades, or start a paper worker. Verify the source file's pair, interval, and
provenance independently before using it; a content hash does not prove that
the file came from Kraken. The file must contain at most 100,000 completed
candles; split larger verified archives into non-overlapping batches. A
missing no-trade interval creates two separate contiguous datasets with an
explicit gap report, not a fabricated bar.

Provision the Bicep `research-history` private Blob container, grant the
**importer's** Microsoft Entra identity Storage Blob Data Contributor on that
container, and apply the reviewed SQL schema separately. The web app does
not receive Blob write permission from this deployment. Set
`HistoricalArchives__ContainerUri` to the Bicep
`historicalArchiveContainerUri` output and
`ConnectionStrings__TradingDb` through a secure environment; use Azure SQL
Entra/managed-identity authentication and do not put passwords, storage
keys, SAS tokens, or private Kraken credentials in commands or source files.
For example, after the two variables have been configured:

```
dotnet run --project src\Trading.Tools.History -- "C:\data\XBTEUR_5.csv" "XBT/EUR" 5m
```

The tool parses the entire file before writing, stores each verified candle
run in a content-addressed Blob with create-only conditions, reads it back,
then publishes its immutable SQL manifest. Retries verify existing bytes
before reporting a duplicate. A failure partway through a multi-run file
can leave earlier *valid* segments imported or an unreferenced Blob; it
never declares the whole file complete. Re-run the identical file and
investigate any error. A stored manifest is not evidence of point-in-time
market membership, measured bid/ask/spread, funding, faithful worker replay,
or trading qualification.

For offline **signal-only** review, select one registered immutable dataset
version per timeframe from the import output. Use an approved family, its
pinned strategy version and saved JSON parameters, and aligned, completed UTC
signal boundaries. For example, the EMA 5m profile requires independently
contiguous 1h regime, 5m signal, and 1m execution archives:

```powershell
dotnet run --project src\Trading.Tools.History -- replay platform.ema-trend-continuation 5 XBT/EUR 5m 2026-07-01T12:00:00Z 2026-07-01T12:05:00Z "C:\data\ema-settings.json" $hourDatasetVersion $fiveMinuteDatasetVersion $oneMinuteDatasetVersion
```

The command needs SQL read access and Storage Blob Data Reader on the private
container (the importer separately requires write access). It reads only the
selected manifests and their fingerprint-verified payloads; an inconsistent
SQL manifest identity fails closed. It prints JSON
with family/version, normalized parameters, source dataset identities and
immutable manifests (including source labels, import times and ranges), and
each signal boundary's five-check analysis or explicit missing-warm-up block.
Each role needs 320 closed candles through its own last completed boundary.
The request allows at most 2,000 signal boundaries and 100,000 candles per
archive; split no-trade runs cannot be silently joined. Cross-sectional
momentum, relative-strength rotation, and regime ensemble cannot be replayed
from one pair without immutable point-in-time universe evidence. A
reconstructed signal says nothing about historically available scanner data,
later executable price, fills, worker/exit state, measured costs, realized
performance, or trading qualification. No worker, paper order, or live order
is started by this command.

For a completed scanner boundary recorded **after** this snapshot feature
was deployed, an operator with SQL read access can inspect the owner-scoped
selected-candidate snapshot without Blob access:

```powershell
dotnet run --project src\Trading.Tools.History -- universe $ownerGuid 2026-10-03T12:00:00Z
```

The output includes an `observed-scanner-candidates-only` scope, the time
candidate discovery and timeframe loading completed, pinned selection
policy, and ordered candidates with liquidity and exchange filters. A
member's daily-window fingerprint is present only when the scanner loaded
320 completed daily candles; otherwise
its evidence is explicitly missing. New version-2 snapshots also list every
loaded member/timeframe's 320-candle fingerprint, each approved strategy's
version and normalized saved settings, and whether that family was evaluated.
Invalid settings are flagged with an empty parameter payload rather than
copied into the audit record or replaced with valid-looking defaults.
Earlier version-1 snapshots lack these additional fields and cannot be
upgraded retrospectively. The corresponding scan activation and
snapshot are committed together; a missing snapshot is an error, not an
empty universe. Earlier scans cannot be reconstructed from current Kraken
pairs or the activation's bounded 500 observations. The timestamp is **after**
the candle boundary; these snapshots do not by themselves prove historical
catalogue completeness, venue-source authenticity, all members' daily
archives, or an executable trade. Grant only the operator's identity the
appropriate SQL read permission; the owner GUID filters the result but is
not an authorization boundary for an operator with direct database access.
The command never uses private Kraken credentials or starts a worker.

To compare **all** members of one observed scanner boundary with imported
daily archives, supply exactly one registered `1D` dataset identity per
recorded member (in any order). First inspect `universe` to find the exact
member list; do not substitute today's pair list for an older scan:

```powershell
dotnet run --project src\Trading.Tools.History -- verify-universe $ownerGuid 2026-10-03T12:00:00Z $xbtDailyDatasetVersion $ethDailyDatasetVersion
```

The example assumes the recorded universe has exactly those two members.
The operator needs SQL read access and Storage Blob Data Reader on the private
container. The command checks snapshot scope, daily evidence, manifest
membership, role, date coverage, per-archive 100,000-candle limit, and
200,000-candle aggregate limit **before downloading any Blob**. It then
validates every complete payload against its immutable manifest and compares
each member's exact 320 closed daily candles at the signal boundary with the
scanner's recorded fingerprint. Later archive candles cannot enter that
window; missing observations, gaps, duplicates, substitution, and missing
members fail rather than producing a partial success. Output lists the
snapshot fingerprint, observation time, matched manifests and window
fingerprints with `retrospective-observed-scanner-daily-input-parity-only`
scope. This comparison can be performed on archives imported after the
scan; it does **not** prove those archives were available at scan time,
authenticate Kraken as their source, establish a complete historical
exchange catalogue, replay the three rotation/ensemble strategies or
workers, measure costs or fills, or qualify a strategy. It does not start
a paper or live trade.

For a **version-2** snapshot, compare one selected member's other recorded
regime, signal, or execution timeframe to a matching immutable archive:

```powershell
dotnet run --project src\Trading.Tools.History -- verify-series $ownerGuid 2026-10-03T12:00:00Z XBT/EUR 1h $xbtHourlyDatasetVersion
```

The tool fails if this exact member/timeframe was not loaded at that scan,
including on older version-1 snapshots. Before Blob access it checks the
owner-scoped snapshot, manifest symbol/interval, closed-boundary coverage,
contiguous-manifest count, and the 100,000-candle limit. It verifies the
complete Blob payload, cuts off later candles, and compares the 320 closed
candles with the recorded role fingerprint. Its JSON scope is
`retrospective-observed-scanner-single-timeframe-input-parity-only`. Repeat
for every role needed by a research profile; verifying one role or the
daily universe does not prove that **all** strategy inputs matched, that
the observed inputs were available before the signal, or that a trade
would have filled. No strategy is promoted or started by either command.

To verify **all timeframes the scanner recorded** at one version-2 boundary,
create a local JSON array of their imported immutable dataset version IDs
(one string per `SeriesEvidence` member/timeframe, in any order, with no
account credentials), then run:

```powershell
dotnet run --project src\Trading.Tools.History -- verify-context $ownerGuid 2026-10-03T12:00:00Z "C:\data\scan-datasets.json"
```

The selection file is capped at 64 KiB and 400 IDs. The command rejects
missing, duplicate, or extra archive roles and preflights their symbol,
interval, coverage, individual 100,000-candle and aggregate 200,000-candle
limits **before** any Blob read. Every payload is then validated against its
manifest and cut to the scanner's closed window; a mismatched fingerprint
aborts the entire check with no partial success output. Older snapshots
cannot pass. The output's
`retrospective-observed-scanner-all-recorded-input-parity-only` scope means
all *recorded* inputs match, **not** that every timeframe required by a
particular strategy was actually available, that all Kraken pairs were
eligible, or that a strategy decision or execution has been replayed.
An absent timeframe remains absent; no archive can fill it in later.

For **signal-only** reconstruction of cross-sectional momentum rotation,
relative-strength pullback rotation, or the regime-switching ensemble at
one recorded version-2 scan, reuse the same complete archive selection:

```powershell
dotnet run --project src\Trading.Tools.History -- replay-scan $ownerGuid 2026-10-03T00:00:00Z platform.cross-sectional-momentum-rotation XBT/EUR "C:\data\scan-datasets.json"
```

The example assumes that exact completed daily scan and pair exist. Relative
strength version 5 instead runs at a closed hour **after** its four-hour
setup, not on the four-hour boundary; the other families follow their
approved signal cadence. The command requires the family and pair to have
been eligible for evaluation, all scanner-recorded inputs to match verified
immutable archives, a complete daily universe and the candidate's required
timeframes. It refuses invalid/unpinned settings and any registry version
drift. It reuses the scanner's own cross-sectional, relative-strength, or
ensemble evaluator with the recorded family and component settings. JSON
includes the pinned parameters, matched input manifests and fingerprints,
and the five-check analysis or veto under
`observed-boundary-scanner-signal-only` scope. This does **not** replay the
entire scanner ranking/admission pipeline, historical worker fills or exits,
an executable quote, measured spread/fees, or a profitable result. Because
the observation is recorded after the boundary and archive imports can be
later still, this is not independent proof of prior Kraken catalogue
membership or real-time source availability. No paper or live order is
created.

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
final 180 candles. To keep work bounded, candidate construction uses at most the
ten most liquid eligible pairs and balances its 250-candidate ceiling across
them: each pair receives every approved strategy once before any pair receives
additional timeframe variants. Selection permits at most one active worker per
strategy and chooses an unused pair first, so the pool uses ten different pairs
when available and falls back to pair reuse only when fewer are eligible.
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
The signal candle is read first; supporting candles are then cut off at that
signal's actual close (at the confirmed execution close for relative-strength
pullback version 5). A later execution candle must not change the evidence
for an already recorded signal or fault a worker holding a paper position.
Existing failed workers do not resume automatically after a code update:
inspect their saved plan, open position, unresolved paper execution claims,
and protective-exit health. Do not reset a failed worker or resubmit its
opening decision to make it appear healthy.
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
entry, latest safe closed one-minute price (falling back to the closed five-minute
price), market value, unrealized and realized profit
and loss, trade count, and additions used versus the worker's immutable
per-position cap. Each of the ten most recent simulated ledger entries is displayed
on its own detail row beneath its worker, including time, direction, quantity, fill
price, fee, current average entry, and latest price. The monitor is owner-scoped
and read-only. An open position with no matching approved protective plan is
marked `Unprotected` in red; no automatic protective exit can manage it.
`RequiresReconciliation` likewise blocks new orders and shows an unresolved
execution outcome. Stop new entries and investigate either warning promptly.

The default Visual Studio **Paper training** profile starts `Trading.Web`,
`Trading.Workers.MarketData`, and `Trading.Workers.Experiments` together.
Starting only `Trading.Web` refuses new paper activation because the scanner
and experiment host heartbeats are missing. Existing admissions can still
show `WaitingForWorker` if the experiment host subsequently stops.

Open `https://localhost:5200/experiments` after both hosts start. Historical
OHLC comes from Kraken's public endpoint; the paper workers never need private
API credentials. A separately connected Kraken account must still have no
withdrawal permission and is not made live-trade eligible by paper Start.

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
not a secret or production authorization mechanism. The rollout cohort alone
does not authorize an entry: a currently effective live-eligible owner plan,
an eligible account and fresh exchange evidence are also required. The
administration UI deliberately creates only paper plans; do not treat its
plan-assignment screen as a way to enable live trading.
The connected key also needs Kraken's **Orders and trades — Query closed
orders & trades** permission for live fill synchronization. A status reporting
executed quantity is no longer sufficient to claim a position: the app must
read every matching trade-history cursor page, reconcile exact cumulative
quantities and observed execution prices, and commit order, position and audit
changes together. Missing trade permission, incomplete history or an
unmatched fill leaves the live book stale rather than pricing a fill at the
order limit. New live positions are bound to the account that placed the
order; legacy positions without account identity remain unbound and block
new live admission. Do not treat these guards as production-ready
account-wide reconciliation.
Before a proving order, keep the selected Kraken account isolated to its
quote currency and the traded base asset: any other nonzero balance now
blocks admission, including a close through the app. Existing outside
holdings must be handled at Kraken, not deleted or silently attributed to a
platform position. These restrictions do not establish
fee-inclusive cost basis or verify all venue activity between the last
read-only check and submission.

A separate authenticated `POST /api/live/positions/{positionId}/close` route
accepts `exchangeAccountId`, `quantity` and an optional `clientOrderId`.
It sends only a reducing Spot sell for that owner's verified, account-bound
long position. An expired or revoked live-eligible plan, or close-only /
reduce-only mode, does not prevent this safety exit, but the operator cohort,
exchange route, halts, current balances and positions, empty venue open-order
book, price, filters and platform limits still apply. Existing orders must be
reconciled before another close is sent. HTTP 202 means the outcome is
unknown: **do not resubmit**. HTTP 200 reports exchange acceptance, not a
fill. If any safety evidence is unavailable, manage the position directly at
Kraken. No automatic live-exit worker is implied by this route.

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
This exists only for disposable development data and must never be used in a
deployed environment. `EnsureCreated` does not add migration history, so a
database it produced cannot be treated as an empty baseline migration target.
