using SharedMeta.Core;

namespace Expedition.Shared
{
    [ServerMetaService]
    public interface IRandomService : IMetaService
    {
        int Next(int max);
    }
}
