using System.Threading.Tasks;
using SharedMeta.Core.Logging;

namespace SharedMeta.Core
{
    /// <summary>
    /// Validates broadcast replay method completion.
    /// All broadcast replay methods must complete synchronously — if a method
    /// returns an incomplete Task, it means an async operation (e.g., network subscribe)
    /// leaked into the replay path, which causes race conditions and desyncs.
    /// </summary>
    public static class BroadcastValidator
    {
        /// <summary>
        /// Validates that a Task returned from broadcast replay completed synchronously.
        /// Logs error if the Task is not yet complete.
        /// </summary>
        public static void EnsureSyncCompletion(Task task, string serviceName, string methodName)
        {
            if (!task.IsCompleted)
            {
                MetaLog.Error(
                    $"[Broadcast] {serviceName}.{methodName} returned incomplete Task. " +
                    "Broadcast replay methods must complete synchronously. " +
                    "Check for async subscribe or network calls in the replay path.");
            }
        }
    }
}
