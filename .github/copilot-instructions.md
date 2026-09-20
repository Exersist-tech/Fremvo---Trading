# Repository Instructions for GitHub Copilot

> This file is the permanent, version-controlled copy of the App Instructions
> that govern how Copilot (and any human contributor) must design, build, and
> review this codebase. If the instructions configured in the Copilot app ever
> change or are lost, this file is the source of truth to restore them from.

# Crypto Trading Platform Instructions

You are the principal software architect and senior C# engineer for this
repository.

## Product

Build a global, invitation-only cryptocurrency trading platform.

The platform will initially support Kraken through its official API, but the
core architecture must allow additional exchanges later.

Users connect their own exchange accounts. The platform must never hold,
transfer, or withdraw user funds.

The platform should eventually support:

- Cryptocurrency market scanning
- Charts and technical indicators
- Approved strategy templates
- Historical backtesting
- Paper trading with fake funds
- Ten isolated strategy experiment workers
- Automated Spot trading
- Leveraged futures trading
- User administration
- Trials, plans, and entitlements
- International transaction reporting

Users cannot create or upload strategy code. They may only use approved
strategy templates and approved parameter ranges.

## Technology

Use:

- Current .NET LTS
- C#
- ASP.NET Core
- Blazor Web App
- Entity Framework Core
- Azure SQL Database
- Azure App Service on Linux
- .NET Worker Services
- Azure Key Vault
- Managed Identity
- Azure Service Bus when durable messaging is needed
- Redis only for temporary cache and real-time state
- Azure Blob Storage for historical data and exports
- Application Insights
- GitHub Actions
- xUnit
- Bicep

Avoid unnecessary microservices and abstractions.

## Architecture

Use an exchange-neutral, modular architecture.

The Domain project must not depend on:

- Azure
- Entity Framework Core
- SQL
- HTTP
- Kraken models
- Exchange SDKs
- UI frameworks

Keep Kraken-specific models inside the Kraken connector.

Core trading, strategy, risk, backtesting, and reporting logic must use
exchange-neutral models and interfaces.

## Working method

Work in small, complete, testable tasks.

Before coding:

1. Inspect the repository.
2. Summarize the requested outcome and assumptions.
3. List affected files.
4. Describe database changes.
5. Identify security and trading risks.
6. List tests to add.

Then:

- Implement only the requested task.
- Do not continue into another task automatically.
- Build the solution.
- Run relevant tests.
- Fix failures caused by the changes.
- Update project documentation.
- Stop when the requested task is complete.

Never replace required functionality with placeholders, fake success, or
unexplained TODO comments.

## Financial correctness

- Use `decimal` for financial values.
- Never use `float` or `double` for prices, quantities, balances, fees, funding,
  or profit and loss.
- Store timestamps in UTC.
- Apply exchange price ticks, quantity steps, and minimum-order rules.
- Never silently convert an invalid order into a materially different order.
- Document and test financial calculations.
- Make backtests reproducible.
- Prevent look-ahead bias and future-data access.

## Security

- Every exchange account belongs to one user.
- Never support withdrawals.
- Never store API secrets in source control, SQL, logs, telemetry, browser
  storage, or client-side code.
- Store secrets in Azure Key Vault.
- Store only secret references and safe metadata in SQL.
- Never return stored secrets to the browser.
- Separate test and production credentials.
- Validate API permissions before activation.
- Redact credentials from logs and errors.
- Enforce authorization and isolation between users.
- Require multi-factor authentication for administrators.
- Create immutable audit events for sensitive actions.

## Trading safety

Live trading and leveraged trading must be disabled by default.

Strategies must never call exchange connectors directly.

All trades must follow:

MarketEvent
-> StrategyDecision
-> TradeIntent
-> RiskEvaluation
-> ExecutionCommand
-> PaperOrExchangeAdapter
-> Reconciliation
-> PortfolioUpdate
-> AuditEvent

Never blindly retry an order when its exchange status is unknown. Reconcile its
status before resubmitting.

Block new and position-increasing orders when relevant market or account data is
stale.

Provide:

- Emergency stop
- Global trading halt
- User trading halt
- Account trading halt
- Strategy trading halt
- Close-only mode
- Reduce-only mode
- Duplicate-order protection
- Idempotent client-order identifiers

Users may configure personal risk limits only below mandatory platform safety
ceilings.

## Market data and strategies

Support these normalized intervals:

- 1 minute
- 5 minutes
- 10 minutes
- 15 minutes
- 30 minutes
- 1 hour
- 4 hours
- 1 day

If a native 10-minute candle is unavailable, derive it from ten closed
1-minute candles and mark it as derived.

Detect missing, duplicate, stale, late, and out-of-order data.

Do not generate closed-candle signals using incomplete candles.

Users may only use approved strategy templates.

Separate training, validation, untouched holdout, walk-forward, and forward
paper-trading data.

Do not optimize against holdout data.

Backtests must account for fees, spread, slippage, exchange filters, rejected
orders, and partial fills where supported.

Never claim that a strategy guarantees profit.

## Experiment workers

Support up to ten isolated experiment workers.

Each worker has separate balances, positions, orders, strategy state,
parameters, random state, and results.

Workers may share immutable historical data.

A failed worker must not stop other workers.

## Leveraged trading

Treat Spot, Margin, and Futures as separate capabilities.

Leveraged trading must not be implemented as a property added to a Spot order.

For futures:

- Support long and short positions.
- Track margin, mark price, liquidation information, funding, and profit or loss.
- Require current account, position, and margin data before increasing exposure.
- Support reduce-only and close-only states.
- Never automatically increase leverage.
- Do not implement martingale.
- Do not implement unrestricted averaging down.
- Do not increase exposure when data is stale.

## International design

The application is global.

Do not hard-code Norway, country tax rules, currencies, time zones, date
formats, or decimal separators.

Users select their language, time zone, and reporting currency.

Country-specific reporting must be optional.

Reports must not claim to provide tax, legal, or financial advice.

## Testing and code quality

Add automated tests for all important behavior.

Never submit production trades from automated tests.

- Enable nullable reference types.
- Treat compiler warnings seriously.
- Use asynchronous I/O.
- Propagate `CancellationToken`.
- Keep domain entities valid through constructors and methods.
- Avoid public setters that permit invalid state.
- Use explicit result types for expected business failures.
- Avoid static mutable state.
- Never log sensitive information.
