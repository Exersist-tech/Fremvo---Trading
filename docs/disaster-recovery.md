# Paper and Spot incident recovery

This runbook is for an authorized operator, not an automatic restart procedure.
No Azure recovery drill or production restore has been performed. The SQL
database is supplied from outside the repository's Bicep template; verify its
point-in-time retention and restore permissions before enabling paper hosts or
real trading. The Blob account is **Standard_LRS**: its 30-day soft-delete
policies and versioning protect against accidental deletion, not a regional
outage. No cross-region RPO or RTO is established.

## Contain and preserve

1. Record the UTC incident start, environment, latest completed scan boundary,
   last accepted forward candle, both persisted host heartbeats, outstanding
   execution claims, and Kraken order identifiers. Do not put API credentials
   or payloads containing them into incident notes, logs, or SQL.
2. Block new exposure first. Engage the platform emergency halt if the
   administrator surface is reachable; independently disable the operator
   Kraken live-execution flag and paper-worker host flags if a control plane
   failure prevents the halt. Keep the web/status surface available if safe.
   Do not assume a process stop cancels orders already at Kraken.
3. At Kraken, inspect every live order of unknown status and any open real
   position using the venue's own controls. Do not resubmit, infer closure
   from the absence of an open order, or rely on restored SQL as proof of an
   exchange fill. Never enable Futures to recover a Spot position.
4. Preserve the original SQL database, application telemetry, and existing
   Blob versions; do not run `EnsureCreated`, reset migration history, or
   overwrite the original database during investigation.

## Restore into isolation

1. With paper hosts and live submissions **off**, have the DBA restore Azure
   SQL point-in-time to a *new* database at the verified UTC recovery point.
   Record that point and any known data-loss interval. Stop if no recoverable
   backup exists; the application has no application-level SQL replica.
2. Compare migration history to all checked-in migrations (currently five),
   then run the read-only schema comparison described in
   [SQL schema deployment](running-locally.md#sql-schema-deployment) against a
   disposable reference database. Never apply an initial migration to a
   populated restore. Recheck owner isolation, user/plan assignments,
   entitlement expiries, audit events, candle continuity, worker activations,
   worker balances and positions, paper execution claims, and pending live
   reconciliation records. Record discrepancies rather than inventing fills.
3. Verify Key Vault access using the restored application's managed identity
   and safe secret references only. If credentials or references were lost,
   leave all trading disabled and rotate/revalidate them through the normal
   account flow; never paste keys into a connection string or restore script.
4. Verify the private archive and paper-report containers. Recover deleted
   blobs or containers only from the configured soft-delete/version history
   within the available retention period. An owner report whose SQL audit
   points to missing or changed Blob content must remain unavailable; the
   download endpoint checks its hash and size. Blob and SQL recovery points
   are independent, so reconcile orphaned reports before offering exports.

## Resume under supervision

1. Reconcile every `Claimed` or `Unknown` paper execution and every unknown
   live order with its durable pipeline/audit evidence and, for real orders,
   Kraken's order history. Confirm exactly one result per client order ID.
   Leave owners with unresolved claims frozen. Do not retry those commands.
2. Restore the web host with live and paper-worker flags off. Confirm login,
   MFA-protected administration, Key Vault, SQL, and report access without
   creating an order. Bring back market-data and experiment hosts in that
   order after schema checks; verify both persisted heartbeats, a fresh
   accepted closed-candle proof, completed owner scan boundaries, and
   protective-exit supervision. A heartbeat alone is insufficient.
3. Only after verifying restored balances/positions and current feed quality,
   allow **paper** activation by owner under the existing entitlement and
   halt controls. Re-enable Spot live trading separately through its existing
   gated operator process after manual Kraken reconciliation. Futures stays
   disabled. Capture before/after UTC observations and the operator decision.

## First drill (required; not yet performed)

In an isolated Azure environment with disposable, paper-only owner data,
execute a real SQL point-in-time restore to a new database, delete and recover
a disposable private Blob and container, stop/restart both worker hosts, and
prove an unresolved execution remains frozen. Verify no production credentials
or real orders were used. Record the UTC start/end, actual recovery point,
observed data-loss interval and downtime, schema comparison, report hash
checks, feed/scan/protection evidence, alert delivery, and reviewer sign-off.
Do not mark disaster recovery ready until that evidence exists; a LocalDB
migration test or successful compilation is not a restore drill.
