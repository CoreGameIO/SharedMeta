using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.28.0+ Generator-emitted catalog of every <c>[MetaConfig]</c> type in the project.
    /// Replaces the runtime <c>IConfigByteSource.Configs</c> list. Framework code (admin grain,
    /// bootstrap hosted service, downloader endpoint) consumes the catalog through this
    /// type-agnostic interface; the generated implementation knows the actual config types at
    /// compile time and dispatches via <see cref="IConfigCatalogHandler"/>.
    ///
    /// <para>Why this exists: <c>SharedMeta.Orleans</c> ships as a NuGet for arbitrary projects
    /// and cannot reference any project's config types directly. The generated
    /// <c>GeneratedConfigCatalog</c> closes this gap — it lives in the project assembly,
    /// references concrete types via <c>typeof(MyConfig)</c> literals, and lets framework
    /// callers invoke per-type code paths through callbacks without reflection.</para>
    /// </summary>
    public interface IConfigCatalog
    {
        /// <summary>
        /// Catalog entries (name + display name pairs) for every <c>[MetaConfig]</c> type the
        /// generator discovered, in declaration order. Used by the admin grain for simple
        /// listing operations that don't need typed dispatch (e.g. <c>ListConfigNamesAsync</c>).
        /// </summary>
        IReadOnlyList<ConfigCatalogEntry> Entries { get; }

        /// <summary>
        /// Invoke <paramref name="handler"/> for every catalog entry, in declaration order, with
        /// the concrete <c>TConfig</c> type parameter set. Each <c>HandleAsync&lt;TConfig&gt;</c>
        /// call is awaited before the next runs — the dispatch is sequential.
        /// </summary>
        Task ForEachAsync(IConfigCatalogHandler handler, CancellationToken cancellationToken = default);

        /// <summary>
        /// Look up <paramref name="name"/> (matched against both <see cref="ConfigCatalogEntry.FullName"/>
        /// and <see cref="ConfigCatalogEntry.DisplayName"/>) and invoke the handler with the matched
        /// <c>TConfig</c>. Returns <c>true</c> when a match was found and dispatched, <c>false</c>
        /// when no config carries that name.
        /// </summary>
        Task<bool> TryDispatchAsync(string name, IConfigCatalogHandler handler, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 0.28.0+ Visitor closed over <c>TConfig</c> at the compile-time call site emitted by
    /// <c>GeneratedConfigCatalog</c>. Implementers see each <c>[MetaConfig]</c> type once,
    /// with its concrete generic parameter, and can use typed APIs
    /// (<c>new TConfig()</c>, <c>IMetaSerializer.PackForExternalUsage&lt;TConfig&gt;</c>,
    /// <c>IConfigRegistry.ListVersionsAsync&lt;TConfig&gt;</c>) without reflection.
    /// </summary>
    public interface IConfigCatalogHandler
    {
        Task HandleAsync<TConfig>(string fullName, string displayName, CancellationToken cancellationToken) where TConfig : class;
    }

    /// <summary>
    /// 0.28.0+ Catalog row — typed-list of every <c>[MetaConfig]</c> the generator discovered.
    /// Names are the stable wire identifiers used by admin tooling (Orleans grain calls,
    /// download URLs); the catalog also exposes <c>OwnerStateType</c> so consumers can resolve
    /// the state → config relationship for download-URL stamping.
    /// </summary>
    public sealed class ConfigCatalogEntry
    {
        /// <summary>Stable identifier — defaults to <see cref="System.Type.FullName"/>.</summary>
        public required string FullName { get; init; }

        /// <summary>Short display name — defaults to <see cref="System.Type.Name"/>. Used in admin UIs.</summary>
        public required string DisplayName { get; init; }

        /// <summary>State type that owns this config (for download URL stamping). Optional.</summary>
        public Type? OwnerStateType { get; init; }
    }
}
