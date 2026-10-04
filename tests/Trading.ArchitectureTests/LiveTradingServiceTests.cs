using Trading.Application.Execution;
using Trading.Application.Entitlements;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Portfolio;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Account;
using Trading.Exchanges.Abstractions.Execution;
using Trading.MarketData;
using Trading.Risk;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the only path in the platform that can move a user's real money.
/// </summary>
/// <remarks>
/// The tests that matter most here are the ones that assert a refusal. An
/// accepted order is easy to get right; what protects a user is that the
/// service declines to send one when ownership, stage, halts, price freshness,
/// duplicate protection or a risk ceiling says it must not.
/// </remarks>
public sealed class LiveTradingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Symbol = "XBTUSD";

    [Fact]
    public async Task OpenOrderReaderCannotQueryAnotherOwnersCredential()
    {
        var accountId = Guid.NewGuid();
        var gateway = new StubOpenOrderGateway();
        var account = new StubExecutionAccounts(new SpotExecutionAccount(
            accountId, OtherUserId, ExchangeKind.Kraken, TradingStage.Live,
            new ExchangeCredential("test-key", "c2VjcmV0")));
        var check = new LiveAccountOpenOrderCheck(gateway, account);

        var result = await check.ReadAsync(UserId, accountId, ExchangeKind.Kraken, CancellationToken.None);

        Assert.Equal(SpotOpenOrderState.Unavailable, result);
        Assert.Equal(0, gateway.Reads);
    }

    [Fact]
    public async Task OpenOrderReaderUsesOnlyTheSelectedAccount()
    {
        var accountId = Guid.NewGuid();
        var gateway = new StubOpenOrderGateway();
        var account = new StubExecutionAccounts(new SpotExecutionAccount(
            accountId, UserId, ExchangeKind.Kraken, TradingStage.Live,
            new ExchangeCredential("test-key", "c2VjcmV0")));
        var check = new LiveAccountOpenOrderCheck(gateway, account);

        var result = await check.ReadAsync(UserId, accountId, ExchangeKind.Kraken, CancellationToken.None);

        Assert.Equal(SpotOpenOrderState.Empty, result);
        Assert.Equal(accountId, account.LastRequestedId);
        Assert.Equal(1, gateway.Reads);
    }

    [Theory]
    [InlineData(SpotOpenOrderState.Present)]
    [InlineData(SpotOpenOrderState.Unavailable)]
    public async Task ExchangeOpenOrdersOrUnavailableEvidenceBlockBeforeReservation(SpotOpenOrderState state)
    {
        var harness = new Harness(TradingStage.Live);
        harness.OpenOrders.State = state;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "external-order");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        Assert.Equal(1, harness.OpenOrders.Reads);
    }

    [Theory]
    [InlineData(SpotOpenOrderState.Present)]
    [InlineData(SpotOpenOrderState.Unavailable)]
    public async Task ExchangeOpenOrdersAppearingAfterReservationRejectWithoutVenueContact(SpotOpenOrderState state)
    {
        var harness = new Harness(TradingStage.Live);
        harness.OpenOrders.OnRead = reads => reads == 1 ? SpotOpenOrderState.Empty : state;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "external-order-late");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Equal(2, harness.OpenOrders.Reads);
        Assert.Equal(OrderState.Rejected, Assert.Single(await harness.Orders.ListAsync(
            UserId, CancellationToken.None)).State);
        Assert.Contains(harness.Audit.Events, item => item.Action == "LiveOrderRejected");
    }

    [Fact]
    public async Task ExternalBaseHoldingsBlockNewLiveExposure()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.01m, 0.01m, 0m, "XBT")
        ];

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "external-holdings");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task BaseHoldingsChangedAfterReservationRejectTheOrder()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Funds.OnRead = read =>
        {
            if (read == 2)
                harness.Funds.Balances =
                [
                    new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
                    new ExchangeBalance("XBT", 0.001m, 0.001m, 0m, "XBT")
                ];
        };

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "external-holdings-late");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(OrderState.Rejected, result.Order?.State);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AnAcceptedOrderIsRecordedAsWorkingAndNotAsFilled()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-1");

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);

        // Acceptance is not a fill. If this ever becomes Filled, the portfolio
        // would show a position the exchange has not given the user.
        Assert.Equal(0m, result.Order!.FilledQuantity);
        Assert.Equal(TradingMode.Live, result.Order.Mode);
        Assert.Empty(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AGeneratedLiveClientOrderIdFitsTheDurableColumn()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, clientOrderId: null);

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);
        Assert.True(result.Order!.ClientOrderId.Length <= Order.MaximumClientOrderIdLength);
        Assert.True(Guid.TryParseExact(result.Order.ClientOrderId, "D", out _));
        Assert.Equal('4', result.Order.ClientOrderId[14]);
    }

    [Fact]
    public async Task ALiveOrderIsAlwaysALimitOrder()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-limit");

        // A market order on a thin book fills at whatever is there. A limit
        // that does not fill is a disappointment; a market order that fills
        // badly is a loss the user never agreed to.
        Assert.Equal(OrderType.Limit, result.Order!.Type);
        Assert.Equal(30000m, result.Order.Price);
    }

    [Fact]
    public async Task AnAccountBelongingToAnotherUserIsNotUsable()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            OtherUserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-2");

        // Reported as ineligible rather than forbidden, so this route cannot
        // be used to discover which account identifiers exist.
        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AUserOutsideTheLiveRolloutCohortCannotSubmit()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Options.AllowedUserIds = Array.Empty<Guid>();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-cohort");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task APreviouslyPromotedAccountCannotTradeAfterTheOperatorWithdrawsItsRoute()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Routes.Enabled = false;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-route-off");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task RouteWithdrawalAfterOrderPersistenceStillPreventsVenueSubmission()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Routes.DisableAfterFirstRead = true;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-route-withdrawn");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(2, harness.Routes.Reads);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Equal(OrderState.Rejected, Assert.Single(await harness.Orders.ListAsync(
            UserId, CancellationToken.None)).State);
        Assert.Contains(harness.Audit.Events, item => item.Action == "LiveOrderRejected");
    }

    [Fact]
    public async Task ARevokedLivePlanBlocksNewOrdersBeforeCreatingAnOrderOrCallingTheExchange()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Eligibility.Eligible = false;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-expired");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task APlanRevokedBetweenSizingAndSubmissionRecordsARejectedOrderWithoutExchangeContact()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Eligibility.ExpireAfterFirstRead = true;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-revoked-at-submit");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(2, harness.Eligibility.Reads);
        Assert.Equal(0, harness.Adapter.Calls);
        Assert.Equal(OrderState.Rejected, Assert.Single(await harness.Orders.ListAsync(
            UserId, CancellationToken.None)).State);
        Assert.Contains(harness.Audit.Events, item => item.Action == "LiveOrderRejected");
    }

    [Fact]
    public async Task AnAccountStillInPaperStageCannotSendARealOrder()
    {
        var harness = new Harness(TradingStage.Paper, accountConnected: false);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-3");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AHaltStopsALiveOrderBeforeTheExchangeIsContacted()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Halts.EngageEmergencyStop();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-4");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task StalePriceDataBlocksALiveOrder()
    {
        // The most recent settled candle is hours old, so nothing here is a
        // current price. Sizing an order against it would be guessing.
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddHours(-5), 30000m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-5");

        Assert.Equal(LiveTradeOutcome.PriceUnavailable, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task FuturePriceDataBlocksALiveOrderBeforePersistenceOrAdapterSubmission()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddMinutes(1), 30000m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-future-price");

        Assert.Equal(LiveTradeOutcome.PriceUnavailable, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AFormingCandleIsNeverUsedAsAReferencePrice()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles:
            [
                ClosedCandle(Now.AddMinutes(-1), 30000m),
                FormingCandle(Now.AddMinutes(1), 41000m),
            ]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-forming");

        Assert.Equal(30000m, result.Order!.Price);
    }

    [Fact]
    public async Task ARepeatedClientOrderIdIsRefusedRatherThanResubmitted()
    {
        var harness = new Harness(TradingStage.Live);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "same-id");
        var second = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "same-id");

        Assert.Equal(LiveTradeOutcome.Duplicate, second.Outcome);

        // One call, not two. A second submission under the same identifier is
        // how one intended position becomes two.
        Assert.Equal(1, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AnOrderAboveThePlatformNotionalCeilingIsBlocked()
    {
        var harness = new Harness(TradingStage.Live);

        // 30000 * 1 is far above the default ceiling.
        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 1m, "live-6");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountCannotTradeASymbolThatWasNeverApproved()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Account.SetProvingNotionalCeiling(1000m);

        // ProvingSymbols is empty by default, so nothing is approved. "Not
        // configured" must read as "nothing approved", never as "anything".
        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-7");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountWithNoCeilingCannotTrade()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Options.ProvingSymbols = [Symbol];
        harness.Account.ClearProvingNotionalCeiling();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-8");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountMayTradeAnApprovedSymbolWithinItsCeiling()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Options.ProvingSymbols = [Symbol];
        harness.Account.SetProvingNotionalCeiling(50m);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-9");

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task AnExchangeRefusalIsRecordedAsRejectedAndNotAsUnknown()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Adapter.Result = ExecutionOutcome.Rejected;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-10");

        Assert.Equal(LiveTradeOutcome.Rejected, result.Outcome);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(OrderState.Rejected, result.Order!.State);
        Assert.Empty(await harness.Reconciliations.ListUnresolvedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnansweredSubmissionIsFrozenForReconciliationRatherThanRetried()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Adapter.Result = ExecutionOutcome.Unknown;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-11");

        Assert.Equal(LiveTradeOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);

        // The order exists locally under the identifier the exchange may also
        // know, and a reconciliation record was opened. Neither the service nor
        // the caller may resubmit until that record is resolved.
        Assert.Single(await harness.Reconciliations.ListUnresolvedAsync(CancellationToken.None));
        Assert.NotNull(await harness.Orders.FindByClientOrderIdAsync("live-11", CancellationToken.None));
        Assert.Contains("Do not resubmit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrderIsStoredBeforeTheExchangeIsContacted()
    {
        var harness = new Harness(TradingStage.Live);
        Order? seenDuringCall = null;
        harness.Adapter.OnExecute = async () =>
            seenDuringCall = await harness.Orders.FindByClientOrderIdAsync("live-12", CancellationToken.None);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-12");

        // A crash between sending and recording would otherwise leave an order
        // at the exchange with no local handle by which to find it.
        Assert.NotNull(seenDuringCall);
    }

    [Fact]
    public async Task ALiveOrderIsNeverWrittenIntoThePaperBook()
    {
        var harness = new Harness(TradingStage.Live);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-13");
        var stored = await harness.Orders.ListAsync(UserId, CancellationToken.None);

        Assert.All(stored, order => Assert.Equal(TradingMode.Live, order.Mode));
    }

    [Fact]
    public async Task ZeroAndNegativeQuantitiesAreRefused()
    {
        var harness = new Harness(TradingStage.Live);

        var zero = await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0m, null);
        var negative = await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, -1m, null);

        Assert.Equal(LiveTradeOutcome.Invalid, zero.Outcome);
        Assert.Equal(LiveTradeOutcome.Invalid, negative.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AQuantityThatBreaksTheKrakenStepIsRefusedWithoutRounding()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.00105m, "live-step");

        Assert.Equal(LiveTradeOutcome.Invalid, result.Outcome);
        Assert.Contains("step", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AReferencePriceThatBreaksTheKrakenTickIsRefusedWithoutRounding()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddMinutes(-1), 30000.05m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-tick");

        Assert.Equal(LiveTradeOutcome.Invalid, result.Outcome);
        Assert.Contains("tick", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task ASpotSellCannotUseUnboundOrAnotherAccountsPosition()
    {
        var harness = new Harness(TradingStage.Live);
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.002m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: Guid.NewGuid()), CancellationToken.None);
        var otherAccountResult = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.001m, "wrong-account-sell");
        Assert.Equal(LiveTradeOutcome.Blocked, otherAccountResult.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);

        var legacy = new Harness(TradingStage.Live);
        await legacy.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.002m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live), CancellationToken.None);
        var unboundResult = await legacy.Service.SubmitAsync(
            UserId, legacy.AccountId, Symbol, OrderSide.Sell, 0.001m, "legacy-sell");
        Assert.Equal(LiveTradeOutcome.Blocked, unboundResult.Outcome);
        Assert.Equal(0, legacy.Adapter.Calls);
    }

    [Fact]
    public async Task SpotSellMustFitOwnedPositionAndWaitForUnknownOrders()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.002m, 0.002m, 0m, "XBT")
        ];
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.002m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId), CancellationToken.None);

        var tooLarge = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.003m, "oversell");
        Assert.Equal(LiveTradeOutcome.Blocked, tooLarge.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);

        var accepted = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.001m, "owned-sell");
        Assert.Equal(LiveTradeOutcome.Accepted, accepted.Outcome);
        Assert.Equal(1, harness.Adapter.Calls);

        var overlapping = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.001m, "overlapping-sell");
        Assert.Equal(LiveTradeOutcome.Blocked, overlapping.Outcome);
        Assert.Equal(1, harness.Adapter.Calls);
    }

    [Fact]
    public async Task ExistingAccountExposureCountsTowardMandatoryLiveCeiling()
    {
        var harness = new Harness(TradingStage.Live);
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.003m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId), CancellationToken.None);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "over-account-ceiling");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AddedQuantityMustFitTheMandatoryPositionSizeCeiling()
    {
        var harness = new Harness(TradingStage.Live, platformRiskLimits: new RiskLimitHierarchy(
            platformMaxExposure: 100m, platformMaxPositionSize: 0.002m));
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.0015m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId), CancellationToken.None);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "over-position-ceiling");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task PerOrderNotionalCeilingIsNotMistakenForTotalAccountCeiling()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Options.MaxOrderNotional = 25m;
        harness.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.001m, 0.001m, 0m, "XBT")
        ];
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.001m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId), CancellationToken.None);

        var withinLimit = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.0005m, "second-small-buy");
        Assert.Equal(LiveTradeOutcome.Accepted, withinLimit.Outcome);
        Assert.Equal(1, harness.Adapter.Calls);

        var tooLarge = new Harness(TradingStage.Live);
        tooLarge.Options.MaxOrderNotional = 25m;
        var blocked = await tooLarge.Service.SubmitAsync(
            UserId, tooLarge.AccountId, Symbol, OrderSide.Buy, 0.001m, "large-single-buy");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, blocked.Outcome);
        Assert.Equal(0, tooLarge.Adapter.Calls);
    }

    [Fact]
    public async Task AWorkingAccountOrderBlocksAnotherLiveBuyUntilReconciled()
    {
        var harness = new Harness(TradingStage.Live);
        var first = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "first-live-buy");
        var second = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "second-live-buy");

        Assert.Equal(LiveTradeOutcome.Accepted, first.Outcome);
        Assert.Equal(LiveTradeOutcome.Blocked, second.Outcome);
        Assert.Equal(1, harness.Adapter.Calls);
    }

    [Fact]
    public async Task ConcurrentInMemoryOrdersCannotReserveTheSameLiveAccount()
    {
        var orders = new InMemoryOrderRepository();
        var accountId = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 2).Select(_ => new Order(
            Guid.NewGuid(), UserId, Guid.NewGuid(), Symbol, OrderSide.Buy,
            OrderType.Limit, 0.001m, 30000m, Now, Guid.NewGuid().ToString("D"),
            mode: TradingMode.Live, exchangeAccountId: accountId)).ToArray();
        var outcomes = await Task.WhenAll(candidates.Select(candidate => Task.Run(async () =>
        {
            try
            {
                await orders.AddAsync(candidate, CancellationToken.None);
                return true;
            }
            catch (WorkingLiveOrderConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, outcomes.Count(accepted => accepted));
        Assert.Single(await orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task LiveAdmissionRequiresFreshAccountScopedUnreservedSpotFunds()
    {
        var insufficient = new Harness(TradingStage.Live);
        insufficient.Funds.Balances = [new ExchangeBalance("USD", 100m, 100m, 80m, "USD")];
        var held = await insufficient.Service.SubmitAsync(
            UserId, insufficient.AccountId, Symbol, OrderSide.Buy, 0.001m, "held-quote");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, held.Outcome);
        Assert.Equal(0, insufficient.Adapter.Calls);

        var creditOnly = new Harness(TradingStage.Live);
        creditOnly.Funds.Balances = [new ExchangeBalance("USD", 0m, 100m, 0m, "USD")];
        var borrowed = await creditOnly.Service.SubmitAsync(
            UserId, creditOnly.AccountId, Symbol, OrderSide.Buy, 0.001m, "credit-only");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, borrowed.Outcome);

        var stale = new Harness(TradingStage.Live);
        stale.Funds.ObservedAtUtc = Now.AddMinutes(-1);
        var late = await stale.Service.SubmitAsync(
            UserId, stale.AccountId, Symbol, OrderSide.Buy, 0.001m, "stale-account");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, late.Outcome);

        var missing = new Harness(TradingStage.Live);
        missing.Funds.Balances = [];
        var absent = await missing.Service.SubmitAsync(
            UserId, missing.AccountId, Symbol, OrderSide.Buy, 0.001m, "missing-quote");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, absent.Outcome);
    }

    [Fact]
    public async Task LiveSellRequiresTheExchangeToReportAvailableBaseAssets()
    {
        var harness = new Harness(TradingStage.Live);
        await harness.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.002m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId), CancellationToken.None);
        harness.Funds.Balances = [new ExchangeBalance("XBT", 0.002m, 0.0005m, 0.0015m, "XBT")];

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.001m, "reserved-base");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AdmissionRejectsFundsOrPositionsThatChangeBeforeExchangeContact()
    {
        var lostFunds = new Harness(TradingStage.Live);
        lostFunds.Funds.OnRead = read =>
        {
            if (read == 2)
                lostFunds.Funds.Balances = [];
        };
        var unavailable = await lostFunds.Service.SubmitAsync(
            UserId, lostFunds.AccountId, Symbol, OrderSide.Buy, 0.001m, "funds-changed");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, unavailable.Outcome);
        Assert.Equal(OrderState.Rejected, unavailable.Order?.State);
        Assert.Equal(0, lostFunds.Adapter.Calls);

        var changedPosition = new Harness(TradingStage.Live);
        var position = new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.001m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: changedPosition.AccountId);
        await changedPosition.Positions.AddAsync(position, CancellationToken.None);
        changedPosition.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.001m, 0.001m, 0m, "XBT")
        ];
        changedPosition.Funds.OnRead = read =>
        {
            if (read == 2)
                position.Increase(0.001m, 30000m);
        };
        var changed = await changedPosition.Service.SubmitAsync(
            UserId, changedPosition.AccountId, Symbol, OrderSide.Buy, 0.001m, "position-changed");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, changed.Outcome);
        Assert.Equal(OrderState.Rejected, changed.Order?.State);
        Assert.Equal(0, changedPosition.Adapter.Calls);
    }

    [Fact]
    public async Task UnknownAccountScopeOrDifferentQuoteExposureBlocksNewLiveOrders()
    {
        var unbound = new Harness(TradingStage.Live);
        await unbound.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            "SOLEUR", PositionDirection.DirectionLong, 1m, 100m, 100m, Now.AddMinutes(-5),
            mode: TradingMode.Live), CancellationToken.None);
        var legacyResult = await unbound.Service.SubmitAsync(
            UserId, unbound.AccountId, Symbol, OrderSide.Buy, 0.001m, "legacy-exposure");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, legacyResult.Outcome);

        var differentQuote = new Harness(TradingStage.Live);
        await differentQuote.Positions.AddAsync(new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            "SOLEUR", PositionDirection.DirectionLong, 1m, 100m, 100m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: differentQuote.AccountId), CancellationToken.None);
        var mixedResult = await differentQuote.Service.SubmitAsync(
            UserId, differentQuote.AccountId, Symbol, OrderSide.Buy, 0.001m, "mixed-exposure");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, mixedResult.Outcome);
        Assert.Equal(0, differentQuote.Adapter.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BoundSpotLongCanCloseAfterEntitlementExpiresInRestrictedModes(
        bool closeOnly, bool reduceOnly)
    {
        var harness = new Harness(TradingStage.Live);
        var position = await AddOwnedLongAsync(harness);
        harness.Eligibility.Eligible = false;
        harness.Halts.SetCloseOnly(UserId, closeOnly);
        harness.Halts.SetReduceOnly(UserId, reduceOnly);

        var ordinary = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Sell, 0.001m, "ordinary-expired");
        var result = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, position.Id, 0.001m, "closing-expired");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, ordinary.Outcome);
        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);
        Assert.Equal(OrderSide.Sell, result.Order!.Side);
        Assert.Equal(0m, result.Order.FilledQuantity);
        Assert.Equal(1, harness.Adapter.Calls);
        Assert.Equal(harness.AccountId, harness.Credentials.LastRequestedId);
        Assert.Equal(1, harness.Eligibility.Reads);
    }

    [Fact]
    public async Task CloseOnlyRouteDoesNotBypassEmergencyOrMarketHalt()
    {
        foreach (var halt in new Action<InMemoryTradingHaltState>[]
                 { state => state.EngageEmergencyStop(), state => state.HaltMarket(Symbol),
                   state => state.HaltUser(UserId), state => state.HaltStrategy(LiveTradingService.ManualLiveStrategyId) })
        {
            var harness = new Harness(TradingStage.Live);
            var position = await AddOwnedLongAsync(harness);
            harness.Eligibility.Eligible = false;
            halt(harness.Halts);

            var result = await harness.Service.ClosePositionAsync(
                UserId, harness.AccountId, position.Id, 0.001m, null);

            Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
            Assert.Equal(0, harness.Adapter.Calls);
            Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CloseOnlyRouteRejectsForeignUnboundOrOversizedPositions()
    {
        var harness = new Harness(TradingStage.Live);
        var owned = await AddOwnedLongAsync(harness);
        foreach (var id in new[] { Guid.NewGuid(), owned.Id })
        {
            var result = await harness.Service.ClosePositionAsync(UserId,
                id == owned.Id ? Guid.NewGuid() : harness.AccountId, id, 0.001m, null);
            Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        }
        var oversized = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, owned.Id, 0.003m, null);
        var otherOwner = await harness.Service.ClosePositionAsync(
            OtherUserId, harness.AccountId, owned.Id, 0.001m, null);
        var unbound = new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.001m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live);
        await harness.Positions.AddAsync(unbound, CancellationToken.None);
        var legacy = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, unbound.Id, 0.001m, null);
        Assert.Equal(LiveTradeOutcome.Blocked, oversized.Outcome);
        Assert.Equal(LiveTradeOutcome.Blocked, otherOwner.Outcome);
        Assert.Equal(LiveTradeOutcome.Blocked, legacy.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task CloseOnlyRouteRequiresFreshBaseFundsAndNoVenueOrders()
    {
        var harness = new Harness(TradingStage.Live);
        var position = await AddOwnedLongAsync(harness);
        harness.Funds.ObservedAtUtc = Now.AddMinutes(-1);
        var stale = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, position.Id, 0.001m, "stale-close");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, stale.Outcome);

        harness.Funds.ObservedAtUtc = Now;
        harness.OpenOrders.State = SpotOpenOrderState.Present;
        var working = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, position.Id, 0.001m, "venue-close");
        Assert.Equal(LiveTradeOutcome.Blocked, working.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task CloseOnlyRouteRejectsStalePriceAndOrderExceedingExchangeFilters()
    {
        var stale = new Harness(TradingStage.Live, candles: [ClosedCandle(Now.AddHours(-2), 30000m)]);
        var held = await AddOwnedLongAsync(stale);
        var missingPrice = await stale.Service.ClosePositionAsync(
            UserId, stale.AccountId, held.Id, 0.001m, "stale-price-close");
        Assert.Equal(LiveTradeOutcome.PriceUnavailable, missingPrice.Outcome);

        var filtered = new Harness(TradingStage.Live);
        var owned = await AddOwnedLongAsync(filtered);
        var wrongStep = await filtered.Service.ClosePositionAsync(
            UserId, filtered.AccountId, owned.Id, 0.00105m, "wrong-step-close");
        Assert.Equal(LiveTradeOutcome.Invalid, wrongStep.Outcome);
        Assert.Equal(0, filtered.Adapter.Calls);
    }

    [Fact]
    public async Task CloseOnlyRouteCannotOverlapWorkingOrUnknownAccountOrders()
    {
        var harness = new Harness(TradingStage.Live);
        var position = await AddOwnedLongAsync(harness);
        harness.Adapter.Result = ExecutionOutcome.Unknown;
        var first = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, position.Id, 0.001m, "unknown-close");
        var second = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId, position.Id, 0.001m, "another-close");

        Assert.Equal(LiveTradeOutcome.Unknown, first.Outcome);
        Assert.Equal(LiveTradeOutcome.Blocked, second.Outcome);
        Assert.Equal(1, harness.Adapter.Calls);
        Assert.Single(await harness.Reconciliations.ListUnresolvedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CloseOnlyRouteRechecksPositionCredentialsAndHaltAfterReservation()
    {
        var changed = new Harness(TradingStage.Live);
        var position = await AddOwnedLongAsync(changed);
        changed.Funds.OnRead = read =>
        {
            if (read == 2)
                position.Increase(0.001m, 30000m);
        };
        var blocked = await changed.Service.ClosePositionAsync(
            UserId, changed.AccountId, position.Id, 0.001m, "changed-close");
        Assert.Equal(LiveTradeOutcome.RiskBlocked, blocked.Outcome);
        Assert.Equal(OrderState.Rejected, blocked.Order?.State);
        Assert.Equal(0, changed.Adapter.Calls);

        var wrongCredential = new Harness(TradingStage.Live);
        var held = await AddOwnedLongAsync(wrongCredential);
        wrongCredential.Credentials.ReturnedOwner = OtherUserId;
        var refused = await wrongCredential.Service.ClosePositionAsync(
            UserId, wrongCredential.AccountId, held.Id, 0.001m, "wrong-credential");
        Assert.Equal(LiveTradeOutcome.AccountNotEligible, refused.Outcome);
        Assert.Equal(OrderState.Rejected, refused.Order?.State);
        Assert.Equal(0, wrongCredential.Adapter.Calls);

        var halted = new Harness(TradingStage.Live);
        var exit = await AddOwnedLongAsync(halted);
        halted.Funds.OnRead = read =>
        {
            if (read == 2)
                halted.Halts.EngageEmergencyStop();
        };
        var stopped = await halted.Service.ClosePositionAsync(
            UserId, halted.AccountId, exit.Id, 0.001m, "halted-close");
        Assert.Equal(LiveTradeOutcome.Blocked, stopped.Outcome);
        Assert.Equal(OrderState.Rejected, stopped.Order?.State);
        Assert.Equal(0, halted.Adapter.Calls);
    }

    [Fact]
    public async Task OtherVenueHoldingsBlockAdmissionEvenWhenSelectedPairReconciles()
    {
        var harness = new Harness(TradingStage.Live);
        await AddOwnedLongAsync(harness);
        harness.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.002m, 0.002m, 0m, "XBT"),
            new ExchangeBalance("ETH", 0.1m, 0.1m, 0m, "ETH")
        ];

        var entry = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "external-asset-entry");
        var exit = await harness.Service.ClosePositionAsync(
            UserId, harness.AccountId,
            (await harness.Positions.ListOpenAsync(UserId, CancellationToken.None)).Single().Id,
            0.001m, "external-asset-close");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, entry.Outcome);
        Assert.Equal(LiveTradeOutcome.RiskBlocked, exit.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    private static async Task<Position> AddOwnedLongAsync(Harness harness)
    {
        var position = new Position(Guid.NewGuid(), UserId, Guid.NewGuid(),
            Symbol, PositionDirection.DirectionLong, 0.002m, 30000m, 30000m, Now.AddMinutes(-5),
            mode: TradingMode.Live, exchangeAccountId: harness.AccountId);
        await harness.Positions.AddAsync(position, CancellationToken.None);
        harness.Funds.Balances =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0.002m, 0.002m, 0m, "XBT")
        ];
        return position;
    }

    private sealed class Harness
    {
        public Harness(
            TradingStage stage,
            bool accountConnected = true,
            IReadOnlyList<Candle>? candles = null,
            RiskLimitHierarchy? platformRiskLimits = null)
        {
            Orders = new InMemoryOrderRepository();
            Positions = new InMemoryPositionRepository();
            Reconciliations = new InMemoryOrderReconciliationRepository();
            Halts = new InMemoryTradingHaltState();
            Audit = new RecordingAudit();
            Adapter = new StubAdapter();
            Options = new LiveTradingOptions
            {
                AllowedUserIds = [UserId],
                PlatformRiskLimits = platformRiskLimits ?? new RiskLimitHierarchy(100m, 100m)
            };
            Pairs = new StubPairs();
            Eligibility = new MutableLiveEligibility();
            Routes = new MutableLiveRoutes();

            var time = new FixedTime(Now);

            Account = new ExchangeAccount(
                Guid.NewGuid(),
                UserId,
                ExchangeKind.Kraken,
                "Kraken",
                "exchange-credential/test",
                Now);

            if (accountConnected)
            {
                Account.MarkConnected(Now);
            }

            var current = TradingStage.Paper;
            while (current != stage && accountConnected)
            {
                current++;
                Account.Promote(current, Now);
            }

            Accounts = new StubAccounts(Account);
            Funds = new StubPortfolio(AccountId);
            OpenOrders = new StubOpenOrders();
            Credentials = new StubExecutionAccounts(new SpotExecutionAccount(
                AccountId, UserId, ExchangeKind.Kraken, stage,
                new ExchangeCredential("test-key", "c2VjcmV0")));

            Service = new LiveTradingService(
                new StubCandles(candles ??
                [
                    ClosedCandle(Now.AddMinutes(-2), 29900m),
                    ClosedCandle(Now.AddMinutes(-1), 30000m),
                ]),
                Pairs,
                Orders,
                Positions,
                Accounts,
                Adapter,
                new OrderReconciliationService(
                    Orders,
                    Reconciliations,
                    new UnavailableStatusQuery(),
                    Audit,
                    time,
                    new PassThroughLiveFillTransaction()),
                Halts,
                Audit,
                new RiskEngine(),
                Options,
                time,
                Eligibility,
                Routes,
                Funds,
                OpenOrders,
                Credentials);
        }

        public ExchangeAccount Account { get; }

        public Guid AccountId => Account.Id;

        public InMemoryOrderRepository Orders { get; }

        public InMemoryPositionRepository Positions { get; }

        public InMemoryOrderReconciliationRepository Reconciliations { get; }

        public InMemoryTradingHaltState Halts { get; }

        public RecordingAudit Audit { get; }

        public StubAdapter Adapter { get; }

        public StubAccounts Accounts { get; }
        public StubPortfolio Funds { get; }
        public StubOpenOrders OpenOrders { get; }
        public StubExecutionAccounts Credentials { get; }

        public LiveTradingOptions Options { get; }

        public StubPairs Pairs { get; }

        public LiveTradingService Service { get; }
        public MutableLiveEligibility Eligibility { get; }
        public MutableLiveRoutes Routes { get; }
    }

    private sealed class StubOpenOrders : ILiveAccountOpenOrderCheck
    {
        public SpotOpenOrderState State { get; set; } = SpotOpenOrderState.Empty;
        public Func<int, SpotOpenOrderState>? OnRead { get; set; }
        public int Reads { get; private set; }

        public Task<SpotOpenOrderState> ReadAsync(
            Guid userId, Guid accountId, ExchangeKind exchange, CancellationToken cancellationToken)
        {
            Assert.Equal(UserId, userId);
            Assert.NotEqual(Guid.Empty, accountId);
            Assert.Equal(ExchangeKind.Kraken, exchange);
            Reads++;
            return Task.FromResult(OnRead?.Invoke(Reads) ?? State);
        }
    }

    private sealed class StubExecutionAccounts(SpotExecutionAccount account) : ISpotExecutionAccountSource
    {
        public Guid LastRequestedId { get; private set; }
        public Guid? ReturnedOwner { get; set; }

        public Task<SpotExecutionAccount?> ResolveAsync(Guid exchangeAccountId, CancellationToken cancellationToken)
        {
            LastRequestedId = exchangeAccountId;
            return Task.FromResult<SpotExecutionAccount?>(ReturnedOwner is { } owner
                ? new SpotExecutionAccount(account.AccountId, owner, account.Exchange, account.Stage, account.Credential)
                : account);
        }
    }

    private sealed class StubOpenOrderGateway : ISpotOpenOrderGateway
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;
        public int Reads { get; private set; }

        public Task<SpotOpenOrderState> ReadAsync(
            ExchangeCredential credential, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(SpotOpenOrderState.Empty);
        }
    }

    private sealed class MutableLiveRoutes : ILiveExecutionRouteProvider
    {
        public bool Enabled { get; set; } = true;
        public bool DisableAfterFirstRead { get; set; }
        public int Reads { get; private set; }

        public bool HasRouteFor(ExchangeKind exchange)
        {
            Reads++;
            return Enabled && exchange == ExchangeKind.Kraken
                && (!DisableAfterFirstRead || Reads == 1);
        }
    }

    private sealed class MutableLiveEligibility : ILiveTradingEligibility
    {
        public bool Eligible { get; set; } = true;
        public bool ExpireAfterFirstRead { get; set; }
        public int Reads { get; private set; }

        public Task<bool> IsEligibleAsync(Guid ownerId, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Eligible && (!ExpireAfterFirstRead || Reads == 1));
        }
    }

    private sealed class StubAdapter : IExecutionAdapter
    {
        public ExecutionOutcome Result { get; set; } = ExecutionOutcome.Accepted;

        public int Calls { get; private set; }

        public Func<Task>? OnExecute { get; set; }

        public async Task<ExecutionResult> ExecuteAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Calls++;

            if (OnExecute is not null)
            {
                await OnExecute().ConfigureAwait(false);
            }

            return new ExecutionResult(
                command.Id,
                success: Result == ExecutionOutcome.Accepted,
                status: Result.ToString(),
                filledQuantity: 0m,
                averageFillPrice: 0m,
                fees: 0m,
                executedAtUtc: Now,
                failureReason: Result == ExecutionOutcome.Accepted ? null : "stubbed",
                outcome: Result);
        }
    }

    private sealed class StubAccounts : IExchangeAccountRepository
    {
        private readonly ExchangeAccount _account;

        public StubAccounts(ExchangeAccount account) => _account = account;

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExchangeAccount?>(_account.Id == id ? _account : null);

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                _account.UserId == userId ? new[] { _account } : Array.Empty<ExchangeAccount>());

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubPortfolio(Guid accountId) : IPortfolioQueryService
    {
        private int _reads;
        public DateTimeOffset ObservedAtUtc { get; set; } = Now;
        public Action<int>? OnRead { get; set; }
        public IReadOnlyCollection<ExchangeBalance> Balances { get; set; } =
        [
            new ExchangeBalance("USD", 1000m, 1000m, 0m, "USD"),
            new ExchangeBalance("XBT", 0m, 0m, 0m, "XBT")
        ];

        public Task<PortfolioAccountReading?> ReadAccountAsync(
            Guid userId, Guid requestedAccountId, CancellationToken cancellationToken = default)
        {
            OnRead?.Invoke(++_reads);
            return Task.FromResult<PortfolioAccountReading?>(userId == UserId && requestedAccountId == accountId
                ? new PortfolioAccountReading(accountId, "Kraken", ExchangeKind.Kraken,
                    ObservedAtUtc, Balances, null)
                : null);
        }

        public Task<IReadOnlyCollection<PortfolioAccountReading>> ReadAsync(
            Guid userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Live admission must read only the selected account.");
    }

    private sealed class UnavailableStatusQuery : Trading.Exchanges.Abstractions.Execution.IExchangeOrderStatusQuery
    {
        public Task<Trading.Exchanges.Abstractions.Execution.OrderStatusQueryResult> QueryByClientOrderIdAsync(
            string clientOrderId,
            string symbol,
            CancellationToken cancellationToken) =>
            Task.FromResult(Trading.Exchanges.Abstractions.Execution.OrderStatusQueryResult.Unavailable(
                "No exchange is reachable in this test."));
    }

    private sealed class StubCandles : IHistoricalCandleSource
    {
        private readonly IReadOnlyList<Candle> _candles;

        public StubCandles(IReadOnlyList<Candle> candles) => _candles = candles;

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_candles);
    }

    private sealed class StubPairs : ITradablePairSource
    {
        public IReadOnlyList<TradablePair> Values { get; set; } =
        [
            new TradablePair(
                Symbol,
                "XBT/USD",
                "XBT",
                "USD",
                isActive: true,
                minimumQuantity: 0.0001m,
                quantityStep: 0.0001m,
                priceTick: 0.1m)
        ];

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Values);
    }

    private sealed class RecordingAudit : IAuditEventWriter
    {
        public List<AuditEvent> Events { get; } = new();

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static Candle ClosedCandle(DateTimeOffset closeTime, decimal close) =>
        new(
            Symbol,
            CandleInterval.OneMinute,
            closeTime.AddMinutes(-1),
            closeTime,
            close,
            close,
            close,
            close,
            volume: 1m,
            isClosed: true,
            isDerived: false);

    private static Candle FormingCandle(DateTimeOffset closeTime, decimal close) =>
        new(
            Symbol,
            CandleInterval.OneMinute,
            closeTime.AddMinutes(-1),
            closeTime,
            close,
            close,
            close,
            close,
            volume: 1m,
            isClosed: false,
            isDerived: false);
}
