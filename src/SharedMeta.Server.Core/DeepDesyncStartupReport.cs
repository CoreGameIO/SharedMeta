using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// States, once at startup, what deep desync detection will actually do on this silo.
    /// <para>
    /// Two independent facts decide that, and neither is visible from the other's side: the build
    /// decides which services carry the client-side comparison
    /// (<c>[MetaServiceImpl(..., DeepDesync = true)]</c>), the configuration decides who the
    /// analysis is switched on for. Either one alone looks like working diagnostics while reporting
    /// nothing, and from the game's side that silence is indistinguishable from "no divergence
    /// happened".
    /// </para>
    /// </summary>
    public sealed class DeepDesyncStartupReport : IHostedService
    {
        private readonly string[] _servicesWithDeepDesync;
        private readonly IOptions<EntityGrainOptions>? _options;
        private readonly ILogger<DeepDesyncStartupReport>? _logger;

        public DeepDesyncStartupReport(
            string[] servicesWithDeepDesync,
            IOptions<EntityGrainOptions>? options,
            ILogger<DeepDesyncStartupReport>? logger = null)
        {
            _servicesWithDeepDesync = servicesWithDeepDesync ?? Array.Empty<string>();
            _options = options;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var mode = _options?.Value.DeepDesyncMode ?? DeepDesyncMode.PerPlayer;

            if (_servicesWithDeepDesync.Length == 0)
            {
                // Forced is an explicit "switch it on for everyone", and here it cannot be honoured
                // by anything. PerPlayer with no capable service is just a build that doesn't use
                // the feature — the default for most projects, and not worth a line at startup.
                if (mode == DeepDesyncMode.Forced)
                {
                    _logger?.LogError(
                        "Deep desync is Forced, but no service in this build declares " +
                        "[MetaServiceImpl(..., DeepDesync = true)]. The server will compute patch CRCs " +
                        "that no client can compare, so nothing will ever be reported. Add the attribute " +
                        "to the services you want covered, or set DeepDesyncMode.Off.");
                }

                return Task.CompletedTask;
            }

            _logger?.LogInformation(
                "Deep desync mode {Mode}; services able to report: {Services}",
                mode, string.Join(", ", _servicesWithDeepDesync));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
