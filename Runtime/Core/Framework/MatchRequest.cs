using MemoryPack;
using MessagePack;
using Orleans;

namespace SharedMeta.Core.Framework
{
    /// <summary>
    /// Request to join matchmaking.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MatchRequest
    {
        /// <summary>
        /// Desired number of players in the match.
        /// </summary>
        [Id(0), Key(0)] public int PlayerCount { get; set; } = 2;

        /// <summary>
        /// Game mode identifier (e.g., "bingo", "runner").
        /// </summary>
        [Id(1), Key(1)] public string GameMode { get; set; } = "default";

        /// <summary>
        /// Optional skill rating for matchmaking balance.
        /// </summary>
        [Id(2), Key(2)] public int? SkillRating { get; set; }

        /// <summary>
        /// Maximum wait time in seconds (0 = unlimited).
        /// </summary>
        [Id(3), Key(3)] public int MaxWaitSeconds { get; set; } = 60;
    }

    /// <summary>
    /// Result of a matchmaking request.
    /// </summary>
    [MemoryPackable, MessagePackObject]
    [GenerateSerializer]
    public partial class MatchRequestResult
    {
        /// <summary>
        /// Whether the request was accepted.
        /// </summary>
        [Id(0), Key(0)] public bool Accepted { get; set; }

        /// <summary>
        /// Queue position (if accepted and waiting).
        /// </summary>
        [Id(1), Key(1)] public int QueuePosition { get; set; }

        /// <summary>
        /// Error message (if not accepted).
        /// </summary>
        [Id(2), Key(2)] public string? ErrorMessage { get; set; }

        /// <summary>
        /// Estimated wait time in seconds.
        /// </summary>
        [Id(3), Key(3)] public int EstimatedWaitSeconds { get; set; }

        public static MatchRequestResult Success(int queuePosition, int estimatedWait) => new()
        {
            Accepted = true,
            QueuePosition = queuePosition,
            EstimatedWaitSeconds = estimatedWait
        };

        public static MatchRequestResult Error(string message) => new()
        {
            Accepted = false,
            ErrorMessage = message
        };
    }
}
