using System.Threading.Tasks;
using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Auth
{
    /// <summary>
    /// Grain interface for device-based authentication.
    /// Grain key = DeviceId.
    /// Maps DeviceId to a persistent PlayerId.
    /// </summary>
    public interface IAuthGrain : IGrainWithStringKey
    {
        /// <summary>
        /// Login with this device. Returns existing PlayerId or creates a new one.
        /// </summary>
        Task<AuthLoginResult> LoginAsync();
    }

    /// <summary>
    /// Result of a login operation.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class AuthLoginResult
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string PlayerId { get; set; } = "";
        [Id(1), Key(1), MemoryPackOrder(1)] public bool IsNewPlayer { get; set; }
    }
}
