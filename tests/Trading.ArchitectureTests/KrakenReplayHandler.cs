using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace Trading.ArchitectureTests;

/// <summary>
/// One recorded exchange answer, or a deliberate failure, to be replayed.
/// </summary>
public sealed class ReplayStep
{
    private ReplayStep(HttpStatusCode status, string? body, Exception? failure, TimeSpan delay)
    {
        Status = status;
        Body = body;
        Failure = failure;
        Delay = delay;
    }

    public HttpStatusCode Status { get; }

    public string? Body { get; }

    public Exception? Failure { get; }

    public TimeSpan Delay { get; }

    /// <summary>A recorded successful HTTP answer.</summary>
    public static ReplayStep Response(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);

        return new ReplayStep(status, body, failure: null, TimeSpan.Zero);
    }

    /// <summary>
    /// A request that never produced an answer. This is the case the whole
    /// reconciliation path exists for, so it must be reproducible on demand.
    /// </summary>
    public static ReplayStep Timeout() =>
        new(HttpStatusCode.OK, body: null, new TaskCanceledException("The request timed out."), TimeSpan.Zero);

    /// <summary>A transport failure with no answer.</summary>
    public static ReplayStep TransportFailure(string message = "The connection was reset.") =>
        new(HttpStatusCode.OK, body: null, new HttpRequestException(message), TimeSpan.Zero);
}

/// <summary>
/// Something a test asserted was sent to the exchange.
/// </summary>
public sealed record RecordedRequest(string Path, string Body, IReadOnlyDictionary<string, string> Form)
{
    public string? Field(string name) => Form.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Replays recorded Kraken answers to the real connector.
/// </summary>
/// <remarks>
/// <para>
/// Kraken publishes no public spot sandbox, so "exercise the connector before
/// it touches real money" cannot mean a test environment at the venue. This
/// harness is the substitute: the production connector runs unmodified against
/// responses captured from Kraken's documented shapes.
/// </para>
/// <para>
/// Two properties make it safe rather than merely convenient. An unscripted
/// path throws instead of falling through, so a test can never reach the real
/// api.kraken.com. And a script that runs out of steps throws instead of
/// repeating its last answer, so a retry loop cannot be validated against an
/// answer nobody wrote down.
/// </para>
/// <para>
/// It lives in the test assembly on purpose. A component that can fabricate an
/// exchange answer must not be shippable inside the connector.
/// </para>
/// </remarks>
public sealed class KrakenReplayHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<ReplayStep>> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>
    /// Every request the connector made, in order, with its form fields parsed
    /// out so a test can assert what was actually sent.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public int RequestCount => _requests.Count;

    public KrakenReplayHandler Script(string path, params ReplayStep[] steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Length == 0)
        {
            throw new ArgumentException("A script needs at least one step.", nameof(steps));
        }

        if (!_scripts.TryGetValue(path, out var queue))
        {
            queue = new Queue<ReplayStep>();
            _scripts[path] = queue;
        }

        foreach (var step in steps)
        {
            queue.Enqueue(step);
        }

        return this;
    }

    public IReadOnlyList<RecordedRequest> RequestsTo(string path) =>
        Requests.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        _requests.Enqueue(new RecordedRequest(path, body, ParseForm(body)));

        if (!_scripts.TryGetValue(path, out var queue))
        {
            throw new InvalidOperationException(
                $"The connector called '{path}', which no test scripted. "
                + "Unscripted calls are refused so a test can never reach the real exchange.");
        }

        if (queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"The connector called '{path}' more times than the test scripted. "
                + "Repeating the last answer would let a retry loop pass against evidence nobody recorded.");
        }

        var step = queue.Dequeue();

        if (step.Delay > TimeSpan.Zero)
        {
            await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
        }

        if (step.Failure is not null)
        {
            throw step.Failure;
        }

        return new HttpResponseMessage(step.Status)
        {
            Content = new StringContent(step.Body ?? string.Empty, Encoding.UTF8, "application/json")
        };
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body))
        {
            return form;
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                form[Uri.UnescapeDataString(pair)] = string.Empty;
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            form[name] = value;
        }

        return form;
    }
}

/// <summary>
/// Kraken answers captured from the documented response shapes, kept in one
/// place so a test states which situation it reproduces rather than restating
/// JSON.
/// </summary>
internal static class KrakenResponses
{
    public const string AddOrderPath = "/0/private/AddOrder";
    public const string QueryOrdersPath = "/0/private/QueryOrders";
    public const string CancelOrderPath = "/0/private/CancelOrder";
    public const string TradesHistoryPath = "/0/private/TradesHistory";

    public static string OrderAccepted(string exchangeOrderId = "OUF4EM-FRGI2-MQMWZD") =>
        "{\"error\":[],\"result\":{\"descr\":{\"order\":\"buy 0.25000000 XBTUSD @ limit 50000.0\"},"
        + "\"txid\":[\"" + exchangeOrderId + "\"]}}";

    public static string OrderValidated() =>
        "{\"error\":[],\"result\":{\"descr\":{\"order\":\"buy 0.25000000 XBTUSD @ limit 50000.0\"}}}";

    public static string Error(params string[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var joined = string.Join(",", errors.Select(e => "\"" + e + "\""));
        return "{\"error\":[" + joined + "],\"result\":{}}";
    }

    /// <summary>An order Kraken knows about, in the given state.</summary>
    public static string OrderFound(
        string exchangeOrderId,
        string status,
        decimal executedVolume,
        decimal volume,
        string clientOrderId = "fremvo-abc123") =>
        "{\"error\":[],\"result\":{\"" + exchangeOrderId + "\":{"
        + "\"status\":\"" + status + "\","
        + "\"cl_ord_id\":\"" + clientOrderId + "\","
        + "\"vol\":\"" + Fmt(volume) + "\",\"vol_exec\":\"" + Fmt(executedVolume) + "\","
        + "\"descr\":{\"pair\":\"XBTUSD\",\"type\":\"buy\",\"ordertype\":\"limit\",\"price\":\"50000.0\"},"
        + "\"opentm\":1700000000.1234,\"cost\":\"0.0\",\"fee\":\"0.0\"}}}";

    /// <summary>
    /// Kraken answered and knows no such order. This is the only answer that
    /// proves an order is absent.
    /// </summary>
    public static string NoSuchOrder() => "{\"error\":[],\"result\":{}}";

    public static string Cancelled(int count = 1) =>
        "{\"error\":[],\"result\":{\"count\":" + count.ToString(CultureInfo.InvariantCulture) + "}}";

    private static string Fmt(decimal value) =>
        value.ToString("0.00000000", CultureInfo.InvariantCulture);
}
