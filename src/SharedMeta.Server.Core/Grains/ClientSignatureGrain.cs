using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Persisted state for <see cref="ClientSignatureGrain"/>. <see cref="Populated"/>
    /// distinguishes "grain activated but never registered" (fresh grain that grabbed an
    /// integer key by virtue of being asked about) from "registered with empty caps"
    /// (legitimate unrestricted client) — without the flag, <see cref="ExistsAsync"/>
    /// couldn't tell those apart.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class ClientSignatureGrainState
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public bool Populated { get; set; }
        [Id(1), Key(1), MemoryPackOrder(1)] public MetaClientSignature? Signature { get; set; }
        [Id(2), Key(2), MemoryPackOrder(2)] public ClientCapabilities? Capabilities { get; set; }
    }

    /// <summary>
    /// One activation per signature hash. State persisted under the <c>"Default"</c> storage
    /// provider so a freshly-restarted silo still recognizes previously-seen client builds
    /// and can serve cached capabilities without recomputation.
    /// </summary>
    public class ClientSignatureGrain : Grain, IClientSignatureGrain
    {
        private readonly IPersistentState<ClientSignatureGrainState> _state;

        public ClientSignatureGrain(
            [PersistentState("clientSignature", "Default")] IPersistentState<ClientSignatureGrainState> state)
        {
            _state = state;
        }

        public Task<ClientCapabilities?> GetCapabilitiesAsync()
            => Task.FromResult(_state.State.Populated ? _state.State.Capabilities : null);

        public Task<bool> ExistsAsync()
            => Task.FromResult(_state.State.Populated);

        public async Task SetAsync(MetaClientSignature signature, ClientCapabilities capabilities)
        {
            _state.State.Populated = true;
            _state.State.Signature = signature;
            _state.State.Capabilities = capabilities;
            await _state.WriteStateAsync();
        }

        public Task<MetaClientSignature?> GetSignatureAsync()
            => Task.FromResult(_state.State.Populated ? _state.State.Signature : null);
    }
}
