using System;
using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Auth
{
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class PlayerVersionGrainState
    {
        /// <summary>Major component of the highest client version ever connected. -1 = no history.</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public int MaxMajor { get; set; } = -1;
        /// <summary>Minor component of the highest client version ever connected.</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public int MaxMinor { get; set; }
    }

    public class PlayerVersionGrain : Grain, IPlayerVersionGrain
    {
        private readonly IPersistentState<PlayerVersionGrainState> _state;

        public PlayerVersionGrain(
            [PersistentState("playerVersion", "Default")] IPersistentState<PlayerVersionGrainState> state)
        {
            _state = state;
        }

        public Task<string?> GetMaxClientVersionAsync()
        {
            var s = _state.State;
            return Task.FromResult<string?>(s.MaxMajor < 0 ? null : $"{s.MaxMajor}.{s.MaxMinor}");
        }

        public async Task<ClientVersionRecordResult> RecordClientVersionAsync(string clientVersion)
        {
            if (!TryParseMajorMinor(clientVersion, out int cMaj, out int cMin))
                return new ClientVersionRecordResult { Accepted = true }; // unparseable — allow through

            var s = _state.State;
            if (s.MaxMajor >= 0)
            {
                // Reject downgrade: major or minor decreased
                if (cMaj < s.MaxMajor || (cMaj == s.MaxMajor && cMin < s.MaxMinor))
                    return new ClientVersionRecordResult
                    {
                        Accepted = false,
                        MaxVersion = $"{s.MaxMajor}.{s.MaxMinor}"
                    };
            }

            // Accept and update if this version is strictly newer (or first record)
            if (cMaj > s.MaxMajor || (cMaj == s.MaxMajor && cMin > s.MaxMinor))
            {
                s.MaxMajor = cMaj;
                s.MaxMinor = cMin;
                await _state.WriteStateAsync();
            }

            return new ClientVersionRecordResult { Accepted = true, MaxVersion = $"{cMaj}.{cMin}" };
        }

        private static bool TryParseMajorMinor(string v, out int major, out int minor)
        {
            major = minor = 0;
            var parts = v.Split('.');
            if (parts.Length < 2) return false;
            return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
        }
    }
}
