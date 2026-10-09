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
                ReportIncomplete(serviceName, methodName);
            }
        }

        /// <summary>
        /// <see cref="ValueTask"/> overload — a service method may be declared with either
        /// awaitable, and the replay path must not have to allocate a <see cref="Task"/> just to
        /// check completion.
        /// </summary>
        public static void EnsureSyncCompletion(ValueTask task, string serviceName, string methodName)
        {
            if (!task.IsCompleted)
            {
                ReportIncomplete(serviceName, methodName);
            }
        }

        /// <summary>
        /// <see cref="ValueTask{TResult}"/> overload. Needed explicitly: <c>Task&lt;T&gt;</c>
        /// inherits <see cref="Task"/> and binds to the overload above, but
        /// <c>ValueTask&lt;T&gt;</c> is an unrelated struct — without this, a service method
        /// declared <c>ValueTask&lt;T&gt;</c> fails to compile in the generated replay path.
        /// The result is discarded; replay re-runs the body for its state effect, and the
        /// return value is compared against the server's separately.
        /// </summary>
        public static void EnsureSyncCompletion<T>(ValueTask<T> task, string serviceName, string methodName)
        {
            if (!task.IsCompleted)
            {
                ReportIncomplete(serviceName, methodName);
            }
        }

        private static void ReportIncomplete(string serviceName, string methodName)
        {
            MetaLog.Error(
                $"[Broadcast] {serviceName}.{methodName} returned an incomplete awaitable. " +
                "Broadcast replay methods must complete synchronously. " +
                "Check for async subscribe or network calls in the replay path.");
        }
    }
}
