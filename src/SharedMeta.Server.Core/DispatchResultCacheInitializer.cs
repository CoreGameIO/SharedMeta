using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using SharedMeta.Core;

namespace SharedMeta.Server.Core
{
    /// <summary>
    /// Populates <see cref="DispatchResult"/>'s cached primitive-return tables
    /// (<see cref="DispatchResult.True"/> / <see cref="DispatchResult.False"/> /
    /// <see cref="DispatchResult.Int"/> / <see cref="DispatchResult.Void"/>) once at silo
    /// bootstrap, using the silo-singleton <see cref="IMetaSerializer"/>.
    /// <para>
    /// Registered as an <see cref="IHostedService"/> by the generator-emitted
    /// <c>ConfigureMeta</c> extension. Cache populates in the ctor (resolved at host
    /// startup, before any grain activates), so generated dispatchers can return cached
    /// instances from the very first RPC.
    /// </para>
    /// </summary>
    public sealed class DispatchResultCacheInitializer : IHostedService
    {
        public DispatchResultCacheInitializer(IMetaSerializer serializer)
        {
            DispatchResult.InitializeCache(serializer);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
