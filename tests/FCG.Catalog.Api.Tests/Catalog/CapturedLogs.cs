using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void Dispose() { }
    private sealed class Capture(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
