using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.1+ Pure in-memory <see cref="IConfigBootstrapper"/>. Defaults come from the
    /// type itself: <see cref="Activator.CreateInstance"/> + <see cref="IMetaSerializer.PackForExternalUsage"/>.
    /// No filesystem reads or writes — works in read-only Docker images and on first cold-start
    /// before any seed file exists.
    /// <para>
    /// Pair with <see cref="ConfigsOptions.UseDefaultInstances"/>. Suitable for projects that
    /// keep config DTOs as the source of truth (defaults live in C# field initializers); switch
    /// to <see cref="DirectoryConfigBootstrapper"/> when an external content pipeline starts
    /// producing the .bin files.
    /// </para>
    /// <para>
    /// All registered <c>[MetaConfig]</c> types share one version (passed at construction).
    /// Per-type version dispatch can be done by composing two bootstrappers in project code
    /// if needed.
    /// </para>
    /// </summary>
    public sealed class DefaultInstanceConfigBootstrapper : IConfigBootstrapper
    {
        private readonly MetaConfigVersion _version;
        private readonly IMetaSerializer _serializer;
        private readonly ILogger<DefaultInstanceConfigBootstrapper> _logger;

        public DefaultInstanceConfigBootstrapper(
            MetaConfigVersion version,
            IMetaSerializer serializer,
            ILogger<DefaultInstanceConfigBootstrapper>? logger = null)
        {
            _version = version;
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? NullLogger<DefaultInstanceConfigBootstrapper>.Instance;
        }

        public Task<MetaConfigVersion?> GetVersionAsync(Type configType, CancellationToken cancellationToken)
            => Task.FromResult<MetaConfigVersion?>(_version);

        public Task<ConfigBootstrapBytes?> GetBytesAsync(Type configType, MetaConfigVersion version, CancellationToken cancellationToken)
        {
            object instance;
            try
            {
                instance = Activator.CreateInstance(configType)
                    ?? throw new InvalidOperationException($"Activator returned null for {configType.FullName}.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DefaultInstanceConfigBootstrapper: skip {Type} — needs a public parameterless ctor",
                    configType.FullName);
                return Task.FromResult<ConfigBootstrapBytes?>(null);
            }

            byte[] bytes;
            try
            {
                bytes = PackViaReflection(_serializer, configType, instance);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "DefaultInstanceConfigBootstrapper: serializer failed to pack {Type}",
                    configType.FullName);
                return Task.FromResult<ConfigBootstrapBytes?>(null);
            }

            _logger.LogInformation(
                "DefaultInstanceConfigBootstrapper: {Type} v{Version} ({Size} B) ← Activator.CreateInstance",
                configType.Name, version, bytes.Length);

            return Task.FromResult<ConfigBootstrapBytes?>(new ConfigBootstrapBytes
            {
                Bytes = bytes,
                Origin = "default-instance",
                PublishedBy = "bootstrap",
            });
        }

        /// <summary>
        /// Reflective dispatch over <c>IMetaSerializer.PackForExternalUsage&lt;T&gt;(T)</c> — caller
        /// has only an open <see cref="Type"/>, so we close the generic at runtime.
        /// </summary>
        private static byte[] PackViaReflection(IMetaSerializer serializer, Type configType, object instance)
        {
            var method = typeof(IMetaSerializer)
                .GetMethod(nameof(IMetaSerializer.PackForExternalUsage))!
                .MakeGenericMethod(configType);
            return (byte[])method.Invoke(serializer, new[] { instance })!;
        }
    }
}
