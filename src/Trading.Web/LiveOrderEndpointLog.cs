namespace Trading.Web;

internal static class LiveOrderEndpointLog
{
    private static readonly Action<ILogger, Exception?> s_persistenceFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4201, "LiveOrderPersistenceFailure"),
            "Live-order persistence failed. The order outcome must be treated as unknown and reconciled before retry.");

    public static void PersistenceFailure(ILogger logger, Exception exception) =>
        s_persistenceFailure(logger, exception);
}
