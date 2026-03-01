using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Subscription info for a player.
    /// </summary>
    [GenerateSerializer]
    public class EntitySubscriber
    {
        [Id(0)] public string PlayerId { get; set; } = "";
        [Id(1)] public DateTime LastPing { get; set; }
    }
}
