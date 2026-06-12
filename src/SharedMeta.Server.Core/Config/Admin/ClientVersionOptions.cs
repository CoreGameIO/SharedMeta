namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Bootstrap values for the framework's client-version subsystem. Binds to the
    /// <c>"ClientVersion"</c> section of host configuration when <c>ConfigureConfigs</c> is
    /// invoked. Runtime overrides — set via the admin grain — take precedence over these
    /// startup values; the values here are only consulted when the corresponding cluster
    /// grain is empty (fresh stand) or as a fallback baseline mirrored into
    /// <c>MetaTransportOptions</c>.
    ///
    /// <para>Roles per field:</para>
    /// <list type="bullet">
    /// <item><see cref="Current"/> — default app version assumed for server-internal callers
    ///       and Global entities. Bootstraps <c>ICurrentClientVersionGrain</c> on first start.</item>
    /// <item><see cref="Min"/> / <see cref="Max"/> — startup baseline for
    ///       <c>MetaTransportOptions.MinClientVersion</c> / <c>MaxClientVersion</c>; admin
    ///       grain overrides remain in effect across restarts.</item>
    /// <item><see cref="Server"/> — written once into <c>MetaTransportOptions.ServerVersion</c>;
    ///       not runtime-managed (a "new server version" is a redeploy event).</item>
    /// </list>
    /// </summary>
    public sealed class ClientVersionOptions
    {
        public const string SectionName = "ClientVersion";

        public string Current { get; set; } = "0.1.0";
        public string? Min { get; set; }
        public string? Max { get; set; }
        public string? Server { get; set; }
    }
}
