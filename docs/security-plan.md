# Security Plan

## 1. Principles

- Least privilege everywhere: exchange API keys, service identities,
  database roles, and human roles all get the minimum access required.
- Defense in depth: no single control (e.g. "we validated no withdraw
  permission") is trusted alone; combine API validation, storage isolation,
  and monitoring.
- No secret ever touches source control, logs, telemetry, browser storage,
  or client-side code, at any layer.
- Assume compromise is possible; design so a compromised web process cannot
  read raw exchange secrets, and a compromised worker cannot withdraw funds
  (withdrawal is never implemented at all).

## 2. Secret handling

- Exchange API key/secret pairs are stored **only** in Azure Key Vault.
  Azure SQL stores a `SecretReference` (vault name, secret name, version) —
  never the secret value.
- Access to Key Vault is via **Managed Identity**; no shared access keys or
  connection strings containing secrets are used from application code.
- Secrets are fetched by the connector at call time and never persisted to
  disk, cache (Redis), or logs. Redis must never contain raw API secrets.
- Proving-stage and production (Live) credentials are
  stored in **separate Key Vaults per environment**; a proving identity has
  no access to the Live vault and vice versa.
- On connecting an exchange account, the platform calls Kraken to confirm
  the key's actual granted permissions and rejects/flags any key that has
  withdrawal permission enabled, prompting the user to reissue a
  trade+read-only key.
- Logging middleware and exception handlers apply a redaction filter for
  known secret-shaped fields (API key, secret, signature, Authorization
  headers) before anything reaches Application Insights.

## 3. AuthN/AuthZ

- Invitation-only registration; invitation codes are single-use or capped,
  expire, and are themselves audited (issuance and redemption).
- Authentication via ASP.NET Core Identity (or equivalent), with password
  policy, optional TOTP-based MFA for all users, **mandatory** MFA for any
  `Administrator` role.
- Authorization is claims/role-based; every request handler enforces that a
  `User` can only access their own `ExchangeAccount`, `ExperimentWorker`,
  `Order`, `Position`, and report data — enforced at the query/repository
  layer (not only UI), with tests asserting cross-user isolation.
- `SupportAgent` access to a user's account is read-only, time-boxed, and
  itself generates an `AuditEvent` naming which agent viewed which user's
  data and when.
- `SystemService` identities (used by worker services) authenticate via
  Managed Identity/service credentials, never shared human accounts.

## 4. Data protection

- Transport: TLS everywhere (App Service enforced HTTPS, SQL enforced TLS,
  Service Bus/Redis over TLS).
- At rest: Azure SQL TDE, Blob Storage encryption at rest, Key Vault
  encryption — all default Azure-managed unless a later requirement needs
  customer-managed keys.
- PII minimization: only what is needed for identity, invitations, and
  reporting; no unnecessary retention of raw exchange data beyond what
  backtesting/reporting requires.

## 5. Auditing

- `AuditEvent` rows are immutable (insert-only, no update/delete path in
  the application) for: login, MFA changes, invitation issuance/use,
  exchange account connect/disconnect, secret rotation, role changes, halt
  switch changes, live-trading enablement, plan/entitlement changes,
  support access to user data.
- Every stage of the trading pipeline (`MarketEvent` → … → `AuditEvent`)
  carries a correlation ID enabling full reconstruction of why an order was
  placed, modified, or rejected.

## 6. Threat model (STRIDE-oriented, key scenarios)

| Threat | Scenario | Mitigation |
|---|---|---|
| Spoofing | Attacker impersonates a user to trade on their exchange account | Strong auth, MFA, session security, per-request authorization checks |
| Spoofing | Compromised invitation code used for unauthorized signup | Single-use/expiring codes, issuance audit, rate limiting |
| Tampering | Attacker modifies a `TradeIntent` or risk limits in transit/storage | TLS, parameterized queries/EF Core, integrity via RowVersion/concurrency checks, no direct client-writable risk tables |
| Tampering | Malicious/compromised worker submits unauthorized orders | Every order requires a `RiskEvaluation` server-side; workers can't bypass the pipeline; idempotent client order IDs prevent silent duplication |
| Repudiation | User denies enabling live trading or disputes a halt | Immutable `AuditEvent` log with actor, timestamp, correlation id |
| Information disclosure | Exchange secrets leaked via logs/telemetry/UI | Key Vault only, redaction filters, secrets never returned to browser, code review + tests asserting no secret serialization |
| Information disclosure | Cross-user data leakage (User A sees User B's account/orders) | Authorization enforced at repository/query layer + integration tests per role |
| Denial of service | Flood of orders/requests exhausts exchange rate limits or platform resources | Per-account/per-strategy order rate limits, Redis-backed rate counters, Service Bus backpressure/dead-lettering |
| Elevation of privilege | Regular user gains Administrator capability | Role checks server-side only, MFA required for admin actions, no client-trusted role claims |
| Financial risk | Stale data causes an over-leveraged or bad-price order | `StalenessPolicy` blocks new/increasing orders on stale data; reduce-only/close-only still allowed to de-risk |
| Financial risk | Duplicate order submitted after timeout/retry | Idempotent `ClientOrderId` + `DuplicateOrderGuard` + mandatory reconciliation before resubmission |
| Financial risk | Runaway strategy loss | Per-scope `RiskLimit`s, daily loss limits, halt switches (global/user/account/strategy), emergency stop |
| Supply chain | Compromised NuGet/npm dependency | Dependency review in CI (e.g. `dotnet list package --vulnerable`), pinned versions, Dependabot/renovate later |

## 7. Secure development lifecycle

- Nullable reference types enabled; compiler warnings treated as build
  issues to fix, not ignore.
- Architecture tests assert Domain purity (no Azure/EF/HTTP/Kraken/UI
  references) so a future PR cannot accidentally leak secrets or
  dependencies into the pure layers.
- Code review checklist includes: no secrets in code/config, no
  float/double in money paths, authorization present on every new endpoint,
  and audit events added for new sensitive actions.
- `/security-review`-style checks are run before enabling any new live
  trading capability.

## 8. Operational security

- Environment separation: Dev/Test hold no Spot trading credential and use recorded responses or the Kraken Futures demo environment only; Production is
  the only environment ever configured with Live credentials, and Live
  trading remains disabled by default even there until explicitly enabled
  per user/account/strategy.
- Secret rotation procedure documented per exchange account; rotation
  updates `SecretReference` without any code change.
- Incident response: an `Emergency stop` (global halt) is reachable by an
  Administrator/RiskOfficer within one action and independent of the
  primary web UI's overall health (e.g., also triggerable via a worker-
  level admin path) so a partial outage doesn't prevent halting trading.
