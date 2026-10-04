extern alias WebApp;

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class OrderBookStreamEndpointTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("CONNECT")]
    public async Task StreamRouteAcceptsWebSocketMethodsButRejectsOrdinaryRequests(string method)
    {
        await using var factory = new OrderBookWebApplicationFactory(new PausedOrderBookSource(false));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method),
            "/api/marketdata/orderbook/stream?symbol=SOL%2FUSD");
        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WebSocketRequired", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingClientCancelsUpstreamEvenWithoutFurtherBookUpdates(bool initialSnapshot)
    {
        var source = new PausedOrderBookSource(initialSnapshot);
        await using var factory = new OrderBookWebApplicationFactory(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/marketdata/orderbook/stream?symbol=SOL%2FUSD"),
            timeout.Token);

        await source.Started.Task.WaitAsync(timeout.Token);
        if (initialSnapshot)
        {
            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        }

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Leaving order book", timeout.Token);
        await source.Stopped.Task.WaitAsync(timeout.Token);
        var closed = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1]), timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, closed.MessageType);
    }

    [Theory]
    [InlineData("Kraken rejected the public order-book subscription.", false)]
    [InlineData("Kraken order-book checksum mismatch; the book is no longer synchronized.", true)]
    public async Task SourceFailureMarksSubscriptionRejectionAsTerminal(string message, bool retryable)
    {
        await using var factory = new OrderBookWebApplicationFactory(new FailingOrderBookSource(message));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri("ws://localhost/api/marketdata/orderbook/stream?symbol=SOL%2FUSD"),
            timeout.Token);

        using var snapshot = JsonDocument.Parse(await ReceiveTextAsync(socket, timeout.Token));
        Assert.Equal("SOL/USD", snapshot.RootElement.GetProperty("symbol").GetString());
        using var error = JsonDocument.Parse(await ReceiveTextAsync(socket, timeout.Token));

        Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
        Assert.Equal(retryable, error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(WebSocketMessageType.Close,
            (await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), timeout.Token)).MessageType);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private sealed class OrderBookWebApplicationFactory(IStreamingOrderBookSource source)
        : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Development:SeedDemoData", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStreamingOrderBookSource>();
                services.AddSingleton<IStreamingOrderBookSource>(source);
            });
        }
    }

    private sealed class FailingOrderBookSource(string message) : IStreamingOrderBookSource
    {
        public async IAsyncEnumerable<OrderBookSnapshot> StreamAsync(
            string symbol, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new OrderBookSnapshot(symbol, DateTimeOffset.UtcNow,
                [new OrderBookLevel(100m, 2m)], [new OrderBookLevel(101m, 3m)], 0, true);
            throw new MarketDataSourceException(message);
        }
    }

    private sealed class PausedOrderBookSource(bool initialSnapshot) : IStreamingOrderBookSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<OrderBookSnapshot> StreamAsync(
            string symbol, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                Started.TrySetResult();
                if (initialSnapshot)
                    yield return new OrderBookSnapshot(symbol, DateTimeOffset.UtcNow,
                        [new OrderBookLevel(100m, 2m)], [new OrderBookLevel(101m, 3m)], 0, true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    Stopped.TrySetResult();
            }
        }
    }
}
