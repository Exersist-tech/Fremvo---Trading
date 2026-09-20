namespace Trading.Web;

internal static class PaperOrderEndpointLog
{
    private static readonly Action<ILogger, Exception?> s_persistenceFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4101, "PaperOrderPersistenceFailure"),
            "Paper-order persistence failed. The client must refresh its paper book before retrying.");

    public static void PersistenceFailure(ILogger logger, Exception exception) =>
        s_persistenceFailure(logger, exception);
}
