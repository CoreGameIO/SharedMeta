using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Cluster-wide version policy grain. Single activation (key "global") shared across all silos.
    /// Used to enforce a minimum client version across the cluster without restarting servers.
    ///
    /// Typical admin workflow:
    ///   1. Deploy new server version.
    ///   2. Give players a migration window to update their clients.
    ///   3. Call SetMinClientVersionAsync to block old clients cluster-wide.
    /// </summary>
    public interface IVersionPolicyGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Returns the admin-set minimum client version, or null if no override is active.
        /// Null means "use the local MetaTransportOptions.MinClientVersion".
        /// </summary>
        Task<string?> GetMinClientVersionAsync();

        /// <summary>
        /// Sets the minimum client version enforced cluster-wide.
        /// Pass null to clear the override and fall back to static server config.
        /// </summary>
        Task SetMinClientVersionAsync(string? version);
    }
}
