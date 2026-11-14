using System;
using Microsoft.Extensions.Logging;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger; // UnityEngine.ILogger also exists

/// <summary>
/// ILogger that routes SharedMeta logs to the Unity console. Compiles against both the
/// SharedMeta logging shim and the real Microsoft.Extensions.Logging (HAS_MEL).
/// </summary>
public class UnityMetaLogger : ILogger
{
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        var message = "[SharedMeta] " + formatter(state, exception);
        if (exception != null) message += "\n" + exception;
        switch (logLevel)
        {
            case LogLevel.Critical:
            case LogLevel.Error:
                Debug.LogError(message);
                break;
            case LogLevel.Warning:
                Debug.LogWarning(message);
                break;
            default:
                Debug.Log(message);
                break;
        }
    }
}
