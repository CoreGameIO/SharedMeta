using SharedMeta.Core;
using SharedMeta.Test.SplitConfig.Models;

namespace SharedMeta.Test.SplitConfig.Services
{
    [MetaServiceImpl(typeof(ISplitConsumerService), typeof(SplitConsumerState))]
    public partial class SplitConsumerService : ISplitConsumerService
    {
        public int Bump()
        {
            Context.State.Counter++;
            return Context.State.Counter;
        }
    }
}
