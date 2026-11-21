using System.Threading.Tasks;

namespace SharedMeta.Core.Framework
{
    /// <summary>
    /// Interface for making requests to the lobby service.
    /// Injected into profile services that need matchmaking.
    /// Async because this is a cross-entity call (Orleans grain-to-grain).
    /// Results are recorded on server and replayed on client.
    /// </summary>
    [ServerMetaService]
    public interface ILobbyRequester
    {
        /// <summary>
        /// Request to join matchmaking queue.
        /// </summary>
        Task<bool> RequestMatchAsync(MatchRequest request);

        /// <summary>
        /// Cancel current matchmaking request.
        /// </summary>
        Task CancelMatchAsync(string gameMode);
    }
}
