using System.Threading.Tasks;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.28.0+ Typed call-site sugar over <see cref="IConfigAdminGrain"/>. Admin projects that
    /// already have a ProjectReference on the meta assembly (PerfectWar.Admin, SpeechClash.Admin,
    /// any in-solution tool) get <c>typeof(TConfig).FullName</c> → wire string conversion for
    /// free at the call site:
    /// <code>
    /// var bytes = await admin.DownloadAsync&lt;SessionConfig&gt;(version);
    /// await admin.UploadAsync&lt;SessionConfig&gt;(version, bytes, "edit", "alice", "rollback");
    /// </code>
    /// <para>
    /// The wire protocol stays string-based (Orleans serialization, cross-process). These
    /// helpers only shift the <c>typeof(...).FullName!</c> call from the admin site into the
    /// extension itself — zero overhead, full compile-time type safety in the caller's code.
    /// </para>
    /// </summary>
    public static class ConfigAdminGrainExtensions
    {
        public static Task<ConfigOverview?> GetConfigAsync<TConfig>(this IConfigAdminGrain grain) where TConfig : class
            => grain.GetConfigAsync(typeof(TConfig).FullName!);

        public static Task<byte[]> DownloadAsync<TConfig>(this IConfigAdminGrain grain, string version) where TConfig : class
            => grain.DownloadAsync(typeof(TConfig).FullName!, version);

        public static Task<ConfigOverview> UploadAsync<TConfig>(
            this IConfigAdminGrain grain,
            string version, byte[] bytes, string origin, string publishedBy, string? notes = null, bool failOnDrift = false)
            where TConfig : class
            => grain.UploadAsync(typeof(TConfig).FullName!, version, bytes, origin, publishedBy, notes, failOnDrift);

        public static Task<bool> UnpublishAsync<TConfig>(this IConfigAdminGrain grain, string version, string deletedBy) where TConfig : class
            => grain.UnpublishAsync(typeof(TConfig).FullName!, version, deletedBy);
    }
}
