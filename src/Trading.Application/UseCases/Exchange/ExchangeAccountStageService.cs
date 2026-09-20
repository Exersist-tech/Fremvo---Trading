using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Exchanges.Abstractions;

namespace Trading.Application.UseCases.Exchange;

/// <summary>
/// The outcome of changing how far an account is cleared to trade. Every
/// refusal is an expected business outcome and is returned rather than thrown.
/// </summary>
public enum TradingStageChangeOutcome
{
    Changed = 0,

    /// <summary>No such account, or it belongs to another user.</summary>
    AccountNotFound = 1,

    /// <summary>The account is not connected and validated, so it cannot advance.</summary>
    AccountNotReady = 2,

    /// <summary>Stages cannot be skipped and cannot be advanced more than one step.</summary>
    InvalidTransition = 3,

    /// <summary>
    /// There is no execution route to this exchange, so no order could reach a
    /// venue even if the account were promoted.
    /// </summary>
    LiveRouteUnavailable = 4
}

public sealed record TradingStageChangeResult(
    TradingStageChangeOutcome Outcome,
    TradingStage Stage,
    string Message)
{
    public bool IsSuccess => Outcome == TradingStageChangeOutcome.Changed;
}

public interface IExchangeAccountStageService
{
    Task<TradingStageChangeResult> PromoteAsync(
        Guid userId,
        Guid accountId,
        TradingStage target,
        CancellationToken cancellationToken = default);

    Task<TradingStageChangeResult> ReturnToPaperAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Moves an exchange account along the paper to proving to live ladder.
/// </summary>
/// <remarks>
/// <para>
/// Promotion is the only way real money can ever be at risk, so it is a
/// deliberate, audited, one-step-at-a-time change rather than a display toggle.
/// The platform authorises it; the user requests it.
/// </para>
/// <para>
/// Returning to paper is never refused. A control that withdraws risk must not
/// be blocked by the same conditions that block taking risk on, or an account
/// could become stuck in a live stage precisely when something is wrong.
/// </para>
/// </remarks>
public sealed class ExchangeAccountStageService : IExchangeAccountStageService
{
    private readonly IExchangeAccountRepository _accounts;
    private readonly ILiveExecutionRouteProvider _routes;
    private readonly IAuditEventWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public ExchangeAccountStageService(
        IExchangeAccountRepository accounts,
        ILiveExecutionRouteProvider routes,
        IAuditEventWriter auditWriter,
        TimeProvider timeProvider)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<TradingStageChangeResult> PromoteAsync(
        Guid userId,
        Guid accountId,
        TradingStage target,
        CancellationToken cancellationToken = default)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new TradingStageChangeResult(
                TradingStageChangeOutcome.AccountNotFound,
                TradingStage.Paper,
                "No such exchange account.");
        }

        if (!account.CanTrade)
        {
            return new TradingStageChangeResult(
                TradingStageChangeOutcome.AccountNotReady,
                account.Stage,
                "This account is not connected and validated, so it cannot be promoted. Reconnect it first.");
        }

        if (target != account.Stage + 1)
        {
            return new TradingStageChangeResult(
                TradingStageChangeOutcome.InvalidTransition,
                account.Stage,
                "Stages are advanced one at a time and cannot be skipped.");
        }

        // The decisive check. Promotion out of paper is only meaningful if an
        // order could actually reach the venue, and nothing in this deployment
        // can send one yet. Refusing here rather than in the user interface
        // means the ladder cannot be climbed by calling the API directly.
        if (!_routes.HasRouteFor(account.ExchangeKind))
        {
            return new TradingStageChangeResult(
                TradingStageChangeOutcome.LiveRouteUnavailable,
                account.Stage,
                $"Real trading on {account.ExchangeKind} is not available: this deployment has no execution route to that exchange, so no order could reach it. Paper trading is unaffected.");
        }

        var now = _timeProvider.GetUtcNow();
        account.Promote(target, now);
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(userId, account, "ExchangeAccount.Promoted", now, cancellationToken)
            .ConfigureAwait(false);

        return new TradingStageChangeResult(
            TradingStageChangeOutcome.Changed,
            account.Stage,
            $"This account is now at the {account.Stage} stage.");
    }

    public async Task<TradingStageChangeResult> ReturnToPaperAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return new TradingStageChangeResult(
                TradingStageChangeOutcome.AccountNotFound,
                TradingStage.Paper,
                "No such exchange account.");
        }

        var now = _timeProvider.GetUtcNow();
        account.ReturnToPaper(now);
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(userId, account, "ExchangeAccount.ReturnedToPaper", now, cancellationToken)
            .ConfigureAwait(false);

        return new TradingStageChangeResult(
            TradingStageChangeOutcome.Changed,
            account.Stage,
            "This account is back to paper trading. No order can reach the exchange.");
    }

    /// <summary>
    /// Looks the account up within the owner's own accounts, so an identifier
    /// belonging to another user reads as "not found" rather than as a
    /// permission error that would confirm the account exists.
    /// </summary>
    private async Task<ExchangeAccount?> FindOwnedAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var owned = await _accounts.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        return owned.FirstOrDefault(account => account.Id == accountId);
    }

    private Task WriteAuditAsync(
        Guid userId,
        ExchangeAccount account,
        string action,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        _auditWriter.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                userId,
                action,
                targetType: "ExchangeAccount",
                targetId: account.Id.ToString("D"),
                occurredAtUtc: now,
                // The stage is the whole point of the event and carries no
                // credential material.
                before: null,
                after: $"stage={account.Stage}; exchange={account.ExchangeKind}",
                correlationId: null),
            cancellationToken);
}
