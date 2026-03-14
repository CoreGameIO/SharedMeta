using System;
using SharedMeta.Core.Logging;
using UnityEngine;

/// <summary>
/// IMetaLogger implementation that routes SharedMeta logs to Unity console.
/// </summary>
public class UnityMetaLogger : IMetaLogger
{
    public bool IsEnabled(MetaLogLevel level) => true;

    public void Log(MetaLogLevel level, string message)
    {
        switch (level)
        {
            case MetaLogLevel.Error:
                Debug.LogError($"[SharedMeta] {message}");
                break;
            case MetaLogLevel.Warning:
                Debug.LogWarning($"[SharedMeta] {message}");
                break;
            default:
                Debug.Log($"[SharedMeta] {message}");
                break;
        }
    }

    public void Log(MetaLogLevel level, string message, Exception exception)
    {
        switch (level)
        {
            case MetaLogLevel.Error:
                Debug.LogError($"[SharedMeta] {message}\n{exception}");
                break;
            case MetaLogLevel.Warning:
                Debug.LogWarning($"[SharedMeta] {message}\n{exception}");
                break;
            default:
                Debug.Log($"[SharedMeta] {message}\n{exception}");
                break;
        }
    }
}
