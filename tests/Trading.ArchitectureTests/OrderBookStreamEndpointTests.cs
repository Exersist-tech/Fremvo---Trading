extern alias WebApp;

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
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

    private sealed class OrderBookWebApplicationFactory(PausedOrderBookSource source)
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
