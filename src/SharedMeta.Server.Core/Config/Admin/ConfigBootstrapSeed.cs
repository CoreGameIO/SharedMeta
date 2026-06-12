using System;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Cold-start seed returned by <see cref="IConfigBootstrapper.LoadAsync"/>.
    /// Carries the bytes the framework should publish into <see cref="IConfigRegistry"/>
    /// when the type has no version yet, plus the audit metadata that lands in
    /// <see cref="IConfigMetadataGrain"/>.
    /// </summary>
    public sealed class ConfigBootstrapSeed
    {
        /// <summary>Version under which to publish the bytes.</summary>
        public MetaConfigVersion Version { get; init; }

        /// <summary>Serialized config bytes — must match the project's <see cref="IMetaSerializer"/> wire format.</summary>
        public byte[] Bytes { get; init; } = Array.Empty<byte>();

        /// <summary>Free-form origin label recorded on audit. Typical: <c>"bootstrap"</c>.</summary>
        public string Origin { get; init; } = "bootstrap";

        /// <summary>Who triggered the publish. Typical: <c>"bootstrap"</c> or the build/CI id.</summary>
        public string PublishedBy { get; init; } = "bootstrap";

        /// <summary>Optional audit note (manifest version, git sha, etc.).</summary>
        public string? Notes { get; init; }
    }
}
