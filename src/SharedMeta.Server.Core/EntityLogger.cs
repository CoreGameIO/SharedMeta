using System;
using Microsoft.Extensions.Logging;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Grain logger handed to meta methods as <c>Context.Log</c>: prefixes the rendered message
    /// with the entity id. State passes through untouched, so structured properties survive.
    /// </summary>
    internal sealed class EntityLogger : ILogger
    {
        private readonly ILogger _inner;
        private readonly string _prefix;

        public EntityLogger(ILogger inner, string entityId)
        {
            _inner = inner;
            _prefix = "[" + entityId + "] ";
        }

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!_inner.IsEnabled(logLevel)) return;
            var prefix = _prefix;
            _inner.Log(logLevel, eventId, state, exception, (s, e) => prefix + formatter(s, e));
        }
    }
}
