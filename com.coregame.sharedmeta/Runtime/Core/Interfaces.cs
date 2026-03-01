using System;
using System.Threading.Tasks; // Added for Task

namespace SharedMeta.Core
{
    /// <summary>
    /// Marker interface for shared state entities (e.g., Inventory, Profile).
    /// </summary>
    public interface ISharedState
    {
    }

    /// <summary>
    /// Provides methods for sending requests to meta services.
    /// </summary>
    public interface IMetaProvider
    {
        Task<TResponse> SendAsync<TRequest, TResponse>(string serviceName, string methodName, TRequest request);
        Task SendVoidAsync<TRequest>(string serviceName, string methodName, TRequest request);
        
        // Byte[]-based overloads for sequential serialization
        Task<TResponse> SendAsync<TResponse>(string serviceName, string methodName, byte[] argsBytes);
        Task SendVoidAsync(string serviceName, string methodName, byte[] argsBytes);
    }

    /// <summary>
    /// Marker interface for services that contain shared business logic.
    /// </summary>
    public interface IMetaService
    {
    }
}
