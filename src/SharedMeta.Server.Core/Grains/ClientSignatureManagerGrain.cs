using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;

namespace SharedMeta.Server.Core.Grains
{
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class ClientSignatureManagerGrainState
    {
        /// <summary>Set of registered signature hashes. HashSet on the wire because Orleans
        /// codec generation supports it and lookup is O(1) on the receiving side.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public HashSet<ulong> KnownHashes { get; set; } = new();
    }

    /// <summary>
    /// Cluster-singleton directory. Activation grabs the <c>"global"</c> key and
    /// persists the directory to the <c>"Default"</c> storage provider so silos
    /// recovering from a cold start see the same set of registered hashes.
    /// </summary>
    public class ClientSignatureManagerGrain : Grain, IClientSignatureManagerGrain
    {
        private readonly IPersistentState<ClientSignatureManagerGrainState> _state;

        public ClientSignatureManagerGrain(
            [PersistentState("clientSignatureManager", "Default")] IPersistentState<ClientSignatureManagerGrainState> state)
        {
            _state = state;
        }

        public Task<bool> IsKnownAsync(ulong signatureHash)
            => Task.FromResult(_state.State.KnownHashes.Contains(signatureHash));

        public async Task RegisterAsync(ulong signatureHash)
        {
            if (_state.State.KnownHashes.Add(signatureHash))
            {
                await _state.WriteStateAsync();
            }
        }
    }
}
