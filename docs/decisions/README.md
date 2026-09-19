# Architecture Decision Records

This folder holds Architecture Decision Records (ADRs) for the platform.
Each significant, hard-to-reverse decision (exchange abstraction shape,
data-store choice, isolation model for experiment workers, gating strategy
for live/leveraged trading, etc.) gets its own ADR so future contributors
understand *why*, not just *what*.

## Format

Create one file per decision: `docs/decisions/NNNN-short-title.md`, using
sequential zero-padded numbers (`0001-`, `0002-`, ...). Use this template:

```markdown
# NNNN. Title

Date: YYYY-MM-DD
Status: Proposed | Accepted | Superseded by NNNN

## Context

What forces/constraints led to this decision (product, technical,
regulatory, or safety-related).

## Decision

What was decided.

## Consequences

What becomes easier or harder as a result; any follow-up work created.

## Alternatives considered

Other options and why they were not chosen.
```

## Index

No ADRs have been recorded yet. The first ADRs are expected to cover
decisions already implied by `docs/architecture.md` and
`docs/product-scope.md`, for example:

- Modular monolith vs. microservices for the initial architecture.
- Exchange-neutral port design for `Trading.Exchanges.Abstractions`.
- Redis-for-ephemeral-state vs. Azure SQL-for-system-of-record split.
- Isolation model for the 10 experiment workers (in-process isolation vs.
  separate processes/containers per worker).
- Gating mechanism for enabling live and leveraged trading.

Add an entry to this index as each ADR is written:

| # | Title | Status |
|---|---|---|
| — | (none yet) | — |
