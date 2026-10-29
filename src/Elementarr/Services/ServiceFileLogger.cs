namespace Elementarr.Services;

public sealed class ServiceFileLogger(string directory) : ILoggerProvider
{
    private readonly object gate = new();

    /// <summary>Creates a small file logger for the Windows service without requiring Windows Event Log permissions.</summary>
    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    /// <summary>Releases the provider, which keeps no open log handles between messages.</summary>
    public void Dispose() { }

    /// <summary>Writes bounded rotating private logs and prevents a logging failure from crashing the service.</summary>
    private void Write(string category, LogLevel level, string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "service.log");
                if (File.Exists(file) && new FileInfo(file).Length > 5 * 1024 * 1024) File.Move(file, file + ".previous", true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} [{level}] {category}: {message}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* The host's console provider remains available. */ }
        }
    }

    private sealed class Writer(ServiceFileLogger provider, string category) : ILogger
    {
        /// <summary>Does not retain request scopes or request credentials in the service log.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Records operational messages while omitting trace/debug payloads.</summary>
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        /// <summary>Formats the normal application message and a sanitized exception type.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, null) + (exception is null ? "" : " (" + exception.GetType().Name + ")")); }
    }
}
