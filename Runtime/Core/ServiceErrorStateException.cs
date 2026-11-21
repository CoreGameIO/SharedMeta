using System;

namespace SharedMeta.Core
{
    /// <summary>
    /// Thrown when a method is called on a service that is in an error state.
    /// The service enters error state when a shared method throws during execution.
    /// Call ClearError() on the API client to resume normal operation.
    /// </summary>
    public class ServiceErrorStateException : InvalidOperationException
    {
        public string ServiceName { get; }
        public Exception OriginalException { get; }

        public ServiceErrorStateException(string serviceName, Exception originalException)
            : base($"Service '{serviceName}' is in error state due to a previous exception: {originalException.Message}. Call ClearError() to resume.", originalException)
        {
            ServiceName = serviceName;
            OriginalException = originalException;
        }
    }
}
