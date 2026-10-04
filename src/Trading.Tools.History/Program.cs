using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Backtesting;
using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Backtesting;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.Tools.History;

internal static class Program
{
    private static readonly JsonSerializerOptions s_replayJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("replay", StringComparison.OrdinalIgnoreCase))
            return await ReplayAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0].Equals("universe", StringComparison.OrdinalIgnoreCase))
            return await UniverseAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0].Equals("verify-universe", StringComparison.OrdinalIgnoreCase))
            return await VerifyUniverseAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0].Equals("verify-series", StringComparison.OrdinalIgnoreCase))
            return await VerifySeriesAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0].Equals("verify-context", StringComparison.OrdinalIgnoreCase))
            return await VerifyContextAsync(args).ConfigureAwait(false);
        if (args.Length > 0 && args[0].Equals("replay-scan", StringComparison.OrdinalIgnoreCase))
            return await ReplayScanAsync(args).ConfigureAwait(false);

        if (args.Length != 3 || !TryInterval(args[2], out var interval)
            || string.IsNullOrWhiteSpace(args[1])
            || !IsMatchingArchiveFileName(args[0], args[1], interval))
        {
            await Console.Error.WriteLineAsync("Usage: Trading.Tools.History <trusted PAIR_MINUTES.csv path> <matching symbol> <1m|5m|15m|30m|1h|4h|1d>").ConfigureAwait(false);
            return 2;
        }

        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync("A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var file = new FileStream(args[0], FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var fileLifetime = file.ConfigureAwait(false);
            using var reader = new StreamReader(file, Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: true);
            var importedAtUtc = DateTimeOffset.UtcNow;
            var archive = await KrakenOhlcvtArchiveReader.ReadAsync(
                reader, args[1], interval, importedAtUtc, cancellation.Token).ConfigureAwait(false);

            var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString).Options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var credential = new DefaultAzureCredential();
            var payloads = new AzureBlobHistoricalCandlePayloadStore(
                new BlobContainerClient(containerUri, credential));
            var service = new HistoricalArchiveIngestionService(
                payloads, new EfHistoricalDatasetRepository(database));

            foreach (var segment in archive.Segments)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var result = await service.ImportSegmentAsync(
                    segment, "kraken-ohlcvt-csv", archive.NormalizedCsvFingerprint,
                    importedAtUtc, cancellation.Token).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"{result.Outcome}: {result.Dataset.VersionIdentity} {result.Dataset.Symbol} {result.Dataset.Interval} {result.Dataset.CandleCount} closed candles").ConfigureAwait(false);
            }

            await Console.Out.WriteLineAsync($"Archive checked: {archive.CandleCount} candles in {archive.Segments.Count} contiguous runs; {archive.Gaps.Count} no-trade gaps. Source identity {archive.NormalizedCsvFingerprint}.").ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> ReplayAsync(string[] args)
    {
        if (args.Length is < 10 or > 12
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !TryInterval(args[4], out var signalInterval)
            || !TrySignalClose(args[5], out var from)
            || !TrySignalClose(args[6], out var to)
            || string.IsNullOrWhiteSpace(args[1])
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[7]))
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History replay <family> <version> <symbol> <signal interval> <from UTC yyyy-MM-ddTHH:mm:ssZ> <to UTC yyyy-MM-ddTHH:mm:ssZ> <settings.json path> <one immutable dataset version per approved timeframe>").ConfigureAwait(false);
            return 2;
        }

        var required = ApprovedConsensusStrategyProfiles.RequiredIntervals(args[1], signalInterval);
        if (args.Length - 8 != required.Count)
        {
            await Console.Error.WriteLineAsync(
                $"This profile requires {required.Count} dataset identities for {string.Join(", ", required)}.").ConfigureAwait(false);
            return 2;
        }
        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync("A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            if (new FileInfo(args[7]).Length > 64 * 1024)
                throw new InvalidDataException("Saved strategy settings exceed the research command's size limit.");
            var parameters = await File.ReadAllTextAsync(args[7], cancellation.Token).ConfigureAwait(false);
            var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString).Options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var manifests = new EfHistoricalDatasetRepository(database);
            var payloads = new AzureBlobHistoricalCandlePayloadStore(
                new BlobContainerClient(containerUri, new DefaultAzureCredential()));
            var series = new List<HistoricalPaperReplaySeries>(required.Count);
            foreach (var identity in args.Skip(8))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var manifest = await manifests.GetAsync(identity, cancellation.Token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The selected historical dataset manifest is not registered.");
                if (!string.Equals(manifest.Symbol, args[3], StringComparison.Ordinal)
                    || !required.Any(role => (int)role == IntervalMinutes(manifest.Interval)))
                    throw new ArgumentException("A selected dataset does not match the requested symbol or approved timeframe.");
                var candles = await payloads.ReadAsync(manifest, cancellation.Token).ConfigureAwait(false);
                series.Add(new HistoricalPaperReplaySeries(manifest, candles));
            }

            var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
            var result = new HistoricalPaperSignalReplay(strategies).Run(
                new HistoricalPaperSignalReplayRequest(
                    args[1], version, args[3], signalInterval, parameters, from, to, series),
                cancellation.Token);
            var json = JsonSerializer.Serialize(result, s_replayJsonOptions);
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> UniverseAsync(string[] args)
    {
        if (args.Length != 3
            || !Guid.TryParseExact(args[1], "D", out var ownerId)
            || ownerId == Guid.Empty
            || !TrySignalClose(args[2], out var boundary)
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0)
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History universe <owner GUID> <UTC five-minute boundary yyyy-MM-ddTHH:mm:ssZ>").ConfigureAwait(false);
            return 2;
        }
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TradingDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console.Error.WriteLineAsync("A SQL connection string must be configured for read-only universe evidence.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString).Options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var snapshot = await new EfPaperScanUniverseSnapshotRepository(database)
                .GetAsync(ownerId, boundary, cancellation.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("No completed scanner universe snapshot exists for that owner and boundary.");
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                EvidenceScope = "observed-scanner-candidates-only",
                Snapshot = snapshot
            }, s_replayJsonOptions)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> VerifyUniverseAsync(string[] args)
    {
        if (args.Length is < 4 or > 53
            || !Guid.TryParseExact(args[1], "D", out var ownerId)
            || ownerId == Guid.Empty
            || !TrySignalClose(args[2], out var boundary)
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0)
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History verify-universe <owner GUID> <UTC five-minute boundary yyyy-MM-ddTHH:mm:ssZ> <one immutable daily dataset version per recorded member>").ConfigureAwait(false);
            return 2;
        }
        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync(
                "A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString).Options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var snapshot = await new EfPaperScanUniverseSnapshotRepository(database)
                .GetAsync(ownerId, boundary, cancellation.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("No completed scanner universe snapshot exists for that owner and boundary.");
            if (snapshot.Members.Count != args.Length - 3)
                throw new ArgumentException(
                    "Supply exactly one immutable daily dataset identity for every observed member.");

            var repository = new EfHistoricalDatasetRepository(database);
            var manifests = new List<HistoricalDataset>(snapshot.Members.Count);
            foreach (var identity in args.Skip(3))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                manifests.Add(await repository.GetAsync(identity, cancellation.Token).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("A selected daily archive manifest is not registered."));
            }
            HistoricalPaperUniverseEvidence.Preflight(snapshot, manifests, cancellation.Token);

            var payloads = new AzureBlobHistoricalCandlePayloadStore(
                new BlobContainerClient(containerUri, new DefaultAzureCredential()));
            var archives = new List<HistoricalPaperReplaySeries>(manifests.Count);
            foreach (var manifest in manifests)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var candles = await payloads.ReadAsync(manifest, cancellation.Token).ConfigureAwait(false);
                archives.Add(new HistoricalPaperReplaySeries(manifest, candles));
            }
            var windows = HistoricalPaperUniverseEvidence.Verify(snapshot, archives, cancellation.Token);
            var selected = manifests.ToDictionary(item => item.Symbol, StringComparer.Ordinal);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                EvidenceScope = "retrospective-observed-scanner-daily-input-parity-only",
                snapshot.OwnerId,
                snapshot.SignalBoundaryUtc,
                snapshot.ObservedAtUtc,
                SnapshotFingerprint = snapshot.Fingerprint,
                DailyArchives = snapshot.Members.Select(member => new
                {
                    member.Symbol,
                    Manifest = selected[member.Symbol],
                    WindowAsOfUtc = windows[member.Symbol].AsOfUtc,
                    WindowFingerprint = HistoricalCandleFingerprint.Compute(windows[member.Symbol].Candles)
                }).ToArray()
            }, s_replayJsonOptions)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> VerifySeriesAsync(string[] args)
    {
        if (args.Length != 6
            || !Guid.TryParseExact(args[1], "D", out var ownerId)
            || ownerId == Guid.Empty
            || !TrySignalClose(args[2], out var boundary)
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0
            || string.IsNullOrWhiteSpace(args[3])
            || !TryInterval(args[4], out var interval))
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History verify-series <owner GUID> <UTC five-minute boundary yyyy-MM-ddTHH:mm:ssZ> <recorded symbol> <1m|5m|15m|30m|1h|4h|1d> <immutable dataset version>").ConfigureAwait(false);
            return 2;
        }
        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync(
                "A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connectionString).Options);
            await using var databaseLifetime = database.ConfigureAwait(false);
            var snapshot = await new EfPaperScanUniverseSnapshotRepository(database)
                .GetAsync(ownerId, boundary, cancellation.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("No completed scanner universe snapshot exists for that owner and boundary.");
            var manifest = await new EfHistoricalDatasetRepository(database)
                .GetAsync(args[5], cancellation.Token).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The selected historical dataset manifest is not registered.");
            HistoricalPaperUniverseEvidence.PreflightSeries(
                snapshot, manifest, args[3], interval, cancellation.Token);
            var payloads = new AzureBlobHistoricalCandlePayloadStore(
                new BlobContainerClient(containerUri, new DefaultAzureCredential()));
            var candles = await payloads.ReadAsync(manifest, cancellation.Token).ConfigureAwait(false);
            var window = HistoricalPaperUniverseEvidence.VerifySeries(snapshot,
                new HistoricalPaperReplaySeries(manifest, candles), args[3], interval,
                cancellation.Token);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                EvidenceScope = "retrospective-observed-scanner-single-timeframe-input-parity-only",
                snapshot.OwnerId,
                snapshot.SignalBoundaryUtc,
                snapshot.ObservedAtUtc,
                SnapshotFingerprint = snapshot.Fingerprint,
                Manifest = manifest,
                WindowAsOfUtc = window.AsOfUtc,
                WindowFingerprint = HistoricalCandleFingerprint.Compute(window.Candles)
            }, s_replayJsonOptions)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> VerifyContextAsync(string[] args)
    {
        if (args.Length != 4
            || !Guid.TryParseExact(args[1], "D", out var ownerId)
            || ownerId == Guid.Empty
            || !TrySignalClose(args[2], out var boundary)
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0
            || string.IsNullOrWhiteSpace(args[3]))
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History verify-context <owner GUID> <UTC five-minute boundary yyyy-MM-ddTHH:mm:ssZ> <JSON array of immutable dataset versions path>").ConfigureAwait(false);
            return 2;
        }
        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync(
                "A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var (snapshot, archives) = await LoadContextAsync(ownerId, boundary, args[3],
                containerUri, connectionString, cancellation.Token).ConfigureAwait(false);
            var windows = HistoricalPaperUniverseEvidence.VerifyContext(snapshot, archives, cancellation.Token);
            var recorded = snapshot.SeriesEvidence!;
            var manifests = archives.Select(item => item.Dataset).ToArray();
            var byIdentity = manifests.ToDictionary(item => item.VersionIdentity, StringComparer.Ordinal);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
            {
                EvidenceScope = "retrospective-observed-scanner-all-recorded-input-parity-only",
                snapshot.OwnerId,
                snapshot.SignalBoundaryUtc,
                snapshot.ObservedAtUtc,
                SnapshotFingerprint = snapshot.Fingerprint,
                RecordedRoles = recorded.Count,
                RoleArchives = recorded.Select(role =>
                {
                    var window = windows[(role.Symbol, role.Interval)];
                    return new
                    {
                        role.Symbol,
                        role.Interval,
                        Manifest = byIdentity[window.DatasetProvenance],
                        WindowAsOfUtc = window.AsOfUtc,
                        WindowFingerprint = HistoricalCandleFingerprint.Compute(window.Candles)
                    };
                }).ToArray()
            }, s_replayJsonOptions)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<int> ReplayScanAsync(string[] args)
    {
        if (args.Length != 6
            || !Guid.TryParseExact(args[1], "D", out var ownerId)
            || ownerId == Guid.Empty
            || !TrySignalClose(args[2], out var boundary)
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[4])
            || string.IsNullOrWhiteSpace(args[5]))
        {
            await Console.Error.WriteLineAsync(
                "Usage: Trading.Tools.History replay-scan <owner GUID> <UTC five-minute boundary yyyy-MM-ddTHH:mm:ssZ> <universe-dependent family> <recorded symbol> <JSON array of immutable dataset versions path>").ConfigureAwait(false);
            return 2;
        }
        var containerUri = ArchiveContainer(out var connectionString);
        if (containerUri is null)
        {
            await Console.Error.WriteLineAsync(
                "A SQL connection string and an HTTPS Azure Blob container URI without credentials must be configured.").ConfigureAwait(false);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.CancelAfter(TimeSpan.Zero);
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var replay = new HistoricalObservedScanReplay(
                ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
            var (snapshot, archives) = await LoadContextAsync(ownerId, boundary, args[5],
                containerUri, connectionString, cancellation.Token,
                snapshot => replay.Preflight(snapshot, args[3], args[4])).ConfigureAwait(false);
            var result = replay.Run(
                new(snapshot, archives, args[3], args[4]), cancellation.Token);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                result, s_replayJsonOptions)).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static async Task<(PaperScanUniverseSnapshot Snapshot, IReadOnlyList<HistoricalPaperReplaySeries> Archives)>
        LoadContextAsync(Guid ownerId, DateTimeOffset boundary, string selectionPath,
            Uri containerUri, string connectionString, CancellationToken cancellationToken,
            Action<PaperScanUniverseSnapshot>? preflight = null)
    {
        if (new FileInfo(selectionPath).Length > 64 * 1024)
            throw new InvalidDataException("The scanner manifest selection exceeds 64 KiB.");
        var identities = JsonSerializer.Deserialize<string[]>(
            await File.ReadAllTextAsync(selectionPath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidDataException("The scanner manifest selection must be a JSON array.");
        if (identities.Length is < 1 or > 400)
            throw new ArgumentException("Supply at most 400 recorded scanner timeframe identities.");

        var database = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connectionString).Options);
        await using var databaseLifetime = database.ConfigureAwait(false);
        var snapshot = await new EfPaperScanUniverseSnapshotRepository(database)
            .GetAsync(ownerId, boundary, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("No completed scanner universe snapshot exists for that owner and boundary.");
        preflight?.Invoke(snapshot);
        if (snapshot.SeriesEvidence is not { Count: > 0 } recorded
            || identities.Length != recorded.Count)
            throw new ArgumentException(
                "Supply one immutable archive identity per recorded version-2 timeframe.");
        var repository = new EfHistoricalDatasetRepository(database);
        var manifests = new List<HistoricalDataset>(identities.Length);
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifests.Add(await repository.GetAsync(identity, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("A selected scanner timeframe manifest is not registered."));
        }
        HistoricalPaperUniverseEvidence.PreflightContext(snapshot, manifests, cancellationToken);

        var payloads = new AzureBlobHistoricalCandlePayloadStore(
            new BlobContainerClient(containerUri, new DefaultAzureCredential()));
        var archives = new List<HistoricalPaperReplaySeries>(manifests.Count);
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            archives.Add(new HistoricalPaperReplaySeries(manifest,
                await payloads.ReadAsync(manifest, cancellationToken).ConfigureAwait(false)));
        }
        return (snapshot, archives);
    }

    private static Uri? ArchiveContainer(out string connectionString)
    {
        connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__TradingDb") ?? string.Empty;
        var container = Environment.GetEnvironmentVariable("HistoricalArchives__ContainerUri");
        if (!string.IsNullOrWhiteSpace(connectionString)
            && Uri.TryCreate(container, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && parsed.UserInfo.Length == 0
            && parsed.Query.Length == 0
            && parsed.Fragment.Length == 0
            && !parsed.AbsolutePath.Trim('/').Contains('/', StringComparison.Ordinal)
            && parsed.AbsolutePath.Trim('/').Length > 0)
        {
            return parsed;
        }

        return null;
    }

    private static bool TrySignalClose(string text, out DateTimeOffset close) =>
        DateTimeOffset.TryParseExact(text, "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out close)
        && close.Offset == TimeSpan.Zero;

    private static int IntervalMinutes(string interval) => interval switch
    {
        "1M" => 1,
        "5M" => 5,
        "10M" => 10,
        "15M" => 15,
        "30M" => 30,
        "1H" => 60,
        "4H" => 240,
        "1D" => 1440,
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };

    private static bool TryInterval(string text, out CandleInterval interval)
    {
        interval = text.Trim().ToUpperInvariant() switch
        {
            "1M" => CandleInterval.OneMinute,
            "5M" => CandleInterval.FiveMinutes,
            "15M" => CandleInterval.FifteenMinutes,
            "30M" => CandleInterval.ThirtyMinutes,
            "1H" => CandleInterval.OneHour,
            "4H" => CandleInterval.FourHours,
            "1D" => CandleInterval.OneDay,
            _ => CandleInterval.None
        };
        return interval != CandleInterval.None;
    }

    private static bool IsMatchingArchiveFileName(string filePath, string symbol, CandleInterval interval)
    {
        if (!Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || symbol.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '/')))
            return false;

        var pairName = symbol.Replace("/", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var expected = $"{pairName}_{(int)interval}";
        return Path.GetFileNameWithoutExtension(filePath).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
