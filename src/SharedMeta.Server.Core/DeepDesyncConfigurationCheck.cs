using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Reports the one deep-desync configuration that cannot work: the silo asks for detection
    /// through <see cref="EntityGrainOptions.DeepDesyncEnabled"/> while no service in the build
    /// carries <c>[MetaServiceImpl(..., DeepDesync = true)]</c>.
    /// <para>
    /// The runtime switch only turns on the half that computes CRCs. Comparing them — and raising
    /// <c>IDesyncDiagnostics.OnPatchDesync</c> — is generated code, emitted per service from the
    /// compile-time attribute. With the attribute nowhere in the build there is nothing to compare
    /// against, so the switch produces silence, and from the game's side silence is
    /// indistinguishable from "no divergence happened".
    /// </para>
    /// <para>
    /// Logged rather than thrown: a diagnostics setting should not take a live silo down. The
    /// count comes from the generator, which knows at compile time how many services opted in.
    /// </para>
    /// </summary>
    public sealed class DeepDesyncConfigurationCheck : IHostedService
    {
        private const string NothingToCompareAgainst =
            """
            EntityGrainOptions.DeepDesyncEnabled is true, but no service in this build declares
            [MetaServiceImpl(..., DeepDesync = true)]. The switch enables CRC computation on the
            server, while the comparison that raises IDesyncDiagnostics.OnPatchDesync is generated
            per service from that attribute - so nothing will ever be reported. Add the attribute
            to the services you want covered, or drop the switch.
            """;

        private readonly int _servicesWithDeepDesync;
        private readonly IOptions<EntityGrainOptions> _options;
        private readonly ILogger<DeepDesyncConfigurationCheck>? _logger;

        public DeepDesyncConfigurationCheck(
            int servicesWithDeepDesync,
            IOptions<EntityGrainOptions> options,
            ILogger<DeepDesyncConfigurationCheck>? logger = null)
        {
            _servicesWithDeepDesync = servicesWithDeepDesync;
            _options = options;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.Value.DeepDesyncEnabled is not true) return Task.CompletedTask;
            if (_servicesWithDeepDesync > 0) return Task.CompletedTask;

            _logger?.LogError(NothingToCompareAgainst);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
