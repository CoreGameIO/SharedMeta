using SharedMeta.Core;

namespace Expedition.Shared
{
    /// <summary>
    /// Server-side random number generator.
    /// Results are recorded on server and replayed on client.
    /// </summary>
    [ServerMetaService]
    public interface IRandomService : IMetaService
    {
        int Next(int max);
    }
}
