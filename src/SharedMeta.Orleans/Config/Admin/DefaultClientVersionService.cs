using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config.Admin;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Default <see cref="IConfigVersionResolver"/> backed by
    /// <see cref="ICurrentClientVersionGrain"/>. Bootstraps from
    /// <see cref="ClientVersionOptions"/> (appsettings <c>"ClientVersion"</c> section) on
    /// fresh stands, then keeps a per-silo cached copy refreshed via background poll so
    /// admin-driven changes propagate cluster-wide without a restart.
    ///
    /// <para>Wiring:</para>
    /// <list type="bullet">
    /// <item>Auto-registered by <c>ConfigureConfigs(...)</c> via <c>TryAddSingleton</c> — projects
    ///       that supply their own <see cref="IConfigVersionResolver"/> impl pre-empt this default.</item>
    /// <item>Mirrors <see cref="ClientVersionOptions.Min"/> / <see cref="ClientVersionOptions.Max"/> /
    ///       <see cref="ClientVersionOptions.Server"/> into <see cref="MetaTransportOptions"/> at startup.
    ///       Runtime Min/Max overrides flow through the existing
    ///       <see cref="IVersionPolicyGrain"/> → <see cref="ClientVersionPolicy"/> path; this service
    ///       only owns the bootstrap mirror plus the Current value.</item>
    /// </list>
    ///
    /// <para>Concurrency: the cached current value is exposed via a volatile field, so
    /// <see cref="CurrentClientVersion"/> is safe to read from any thread while the poll
    /// task rewrites it.</para>
    /// </summary>
    public sealed class DefaultClientVersionService : IConfigVersionResolver, IHostedService, IAsyncDisposable
    {
        /// <summary>How often the poll task pulls the grain state. Default: 30 seconds.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

        private readonly IGrainFactory _grains;
        private readonly MetaTransportOptions _transport;
        private readonly ClientVersionOptions _bootstrap;
        private readonly ILogger<DefaultClientVersionService> _logger;

        private volatile string _current;
        private Timer? _timer;

        public DefaultClientVersionService(
            IGrainFactory grains,
            MetaTransportOptions transport,
            IOptions<ClientVersionOptions> options,
            ILogger<DefaultClientVersionService>? logger = null)
        {
            _grains = grains;
            _transport = transport;
            _bootstrap = options.Value;
            _logger = logger ?? NullLogger<DefaultClientVersionService>.Instance;
            _current = string.IsNullOrWhiteSpace(_bootstrap.Current) ? "0.1.0" : _bootstrap.Current.Trim();
        }

        public string CurrentClientVersion => _current;

        public MetaConfigVersion ResolveVersion(string stateTypeName, string entityId, MetaConfigVersion defaultVersion)
            => defaultVersion;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Mirror appsettings Server/Min/Max into the transport options once. Runtime
            // overrides on Min/Max go through IVersionPolicyGrain (read by ClientVersionPolicy)
            // and never write back through this path.
            if (!string.IsNullOrEmpty(_bootstrap.Server) && string.IsNullOrEmpty(_transport.ServerVersion))
                _transport.ServerVersion = _bootstrap.Server;
            if (!string.IsNullOrEmpty(_bootstrap.Min) && string.IsNullOrEmpty(_transport.MinClientVersion))
                _transport.MinClientVersion = _bootstrap.Min;
            if (!string.IsNullOrEmpty(_bootstrap.Max) && string.IsNullOrEmpty(_transport.MaxClientVersion))
                _transport.MaxClientVersion = _bootstrap.Max;

            var grain = _grains.GetGrain<ICurrentClientVersionGrain>(0);
            var stored = await grain.GetAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stored))
            {
                _logger.LogInformation(
                    "DefaultClientVersionService: bootstrap ICurrentClientVersionGrain from appsettings ('{Current}')",
                    _current);
                await grain.SetAsync(_current, "bootstrap").ConfigureAwait(false);
            }
            else if (stored != _current)
            {
                _logger.LogInformation(
                    "DefaultClientVersionService: loaded current from grain ('{Current}', overrides appsettings '{Bootstrap}')",
                    stored, _current);
                _current = stored;
            }

            _timer = new Timer(_ => _ = RefreshAsync(), null, PollInterval, PollInterval);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Dispose();
            _timer = null;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _timer?.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Apply a new current value locally without waiting for the next poll. Called by
        /// the admin grain right after writing through to <see cref="ICurrentClientVersionGrain"/>
        /// so the silo serving the admin request reflects the change immediately. Other silos
        /// pick it up via their next <see cref="PollInterval"/> tick.
        /// </summary>
        public void SetCurrentLocally(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return;
            var trimmed = version.Trim();
            if (trimmed == _current) return;
            _logger.LogInformation("DefaultClientVersionService: local push '{Old}' → '{New}'", _current, trimmed);
            _current = trimmed;
        }

        private async Task RefreshAsync()
        {
            try
            {
                var stored = await _grains.GetGrain<ICurrentClientVersionGrain>(0).GetAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stored) && stored != _current)
                {
                    _logger.LogInformation("DefaultClientVersionService: poll '{Old}' → '{New}'", _current, stored);
                    _current = stored!;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DefaultClientVersionService: poll failed (will retry next interval)");
            }
        }
    }
}
