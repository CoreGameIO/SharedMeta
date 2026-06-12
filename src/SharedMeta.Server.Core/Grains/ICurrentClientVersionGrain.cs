using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// 0.27.0+ Cluster-wide grain that owns the "current client app version" — the value
    /// returned by <c>IConfigVersionResolver.CurrentClientVersion</c> on every silo. Single
    /// activation, key 0.
    ///
    /// <para>
    /// Sibling of <see cref="IVersionPolicyGrain"/>: Min/Max are rejection gates; Current is
    /// the default-for-internal-callers and Global-entity baseline. Both are runtime-mutable
    /// so admin can stage a release (deploy server first, flip Current when clients start
    /// rolling out) without a silo restart.
    /// </para>
    ///
    /// <para>Bootstrap: at first activation, <c>DefaultClientVersionService</c> seeds the
    /// state from <c>ClientVersionOptions.Current</c> (appsettings <c>"ClientVersion:Current"</c>).
    /// Once persisted, the grain value wins on subsequent silo starts.</para>
    /// </summary>
    public interface ICurrentClientVersionGrain : IGrainWithIntegerKey
    {
        /// <summary>Returns the cluster-wide current version, or <c>null</c> when never set.</summary>
        Task<string?> GetAsync();

        /// <summary>Sets the cluster-wide current version. <paramref name="changedBy"/> is informational (logged).</summary>
        Task SetAsync(string version, string changedBy);
    }
}
