using System.Threading.Tasks;
using Orleans;

namespace SharedMeta.Orleans.Config
{
    /// <summary>
    /// One grain per <c>(configType.FullName, MetaConfigVersion)</c> pair — holds the
    /// serialized bytes for that single version. Grain key format:
    /// <c>"{configType.FullName}::{Major}.{Minor}.{Patch}"</c>.
    ///
    /// <para>Per-(type, version) addressing keeps writes contention-free and lets the
    /// cluster lazily activate only the versions that are actually queried, instead of
    /// loading every published version of a type into a single grain. Listing the
    /// available versions for a type is the job of <see cref="IConfigDirectoryGrain"/>,
    /// not this grain.</para>
    /// </summary>
    public interface IConfigStoreGrain : IGrainWithStringKey
    {
        /// <summary>Read the stored bytes, or null when the version was never published / has been cleared.</summary>
        Task<byte[]?> GetAsync();

        /// <summary>Overwrite the stored bytes. New versions and republishes both go through this method.</summary>
        Task SetAsync(byte[] bytes);

        /// <summary>Drop the stored bytes — subsequent <see cref="GetAsync"/> returns null.</summary>
        Task ClearAsync();
    }
}
