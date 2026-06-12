namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ When the framework's <c>ConfigBootstrapHostedService</c> should call the
    /// project-supplied loader during cold-start seed.
    /// </summary>
    public enum ConfigSeedStrategy
    {
        /// <summary>
        /// Call the loader only when <see cref="IConfigRegistry.ListVersionsAsync"/> returns
        /// no versions for this type — the registry is the post-bootstrap source of truth.
        /// Typical prod: image-baked bytes seed the registry once; later admin uploads add
        /// versions; subsequent restarts hit the same persisted registry and skip the loader.
        /// </summary>
        LoadIfEmpty,

        /// <summary>
        /// Default. Call the loader, get <c>(version, bytes)</c>. Publish only if that exact
        /// version isn't already in the registry. Typical rollout: image baked v1.0.4, server
        /// restart finds v1.0.4 already published (e.g. from a previous run) → skip; image
        /// baked v1.0.5 → publish as new version. Admin-uploaded versions in between coexist
        /// — loader doesn't touch them.
        /// </summary>
        LoadIfNew,

        /// <summary>
        /// Always call the loader and run <c>PublishIfChangedAsync</c> — same-content republish
        /// is a no-op, drift overwrites silently. Typical dev: content-derived versions (YAML
        /// mtime → patch) mean every YAML edit creates a fresh version; this strategy
        /// guarantees the registry always reflects the on-disk content without manual bumps.
        /// </summary>
        LoadAlways,
    }
}
