using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    /// <summary>
    /// State for <see cref="IStateLoadedHook"/> tests. <see cref="Tags"/> has no initializer — it
    /// stands for a member added after old records were written, which loads as <c>null</c>.
    /// </summary>
    [SharedState]
    [MemoryPackable]
    public partial class LoadHookState : ISharedState, IStateLoadedHook
    {
        [MemoryPackOrder(0)] public List<string>? Tags { get; set; }

        /// <summary>Test-chosen key for <see cref="Loads"/>; empty until set.</summary>
        [MemoryPackOrder(1)] public string Marker { get; set; } = "";

        /// <summary>Per marker: whether each load found <see cref="Tags"/> missing. The test silo
        /// runs in-process, so the test reads it directly.</summary>
        public static readonly ConcurrentDictionary<string, ConcurrentQueue<bool>> Loads = new();

        public bool OnLoadedFromStorage()
        {
            var missing = Tags == null;
            if (Marker.Length > 0)
                Loads.GetOrAdd(Marker, _ => new ConcurrentQueue<bool>()).Enqueue(missing);
            Tags ??= new List<string>();
            return missing;
        }
    }

    [MetaService(StateType = typeof(LoadHookState))]
    public interface ILoadHookService : IMetaService
    {
        /// <summary>Turns the state into what an old record looks like: <c>Tags</c> missing.</summary>
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task MakeLegacy(string marker);
    }

    [MetaServiceImpl(typeof(ILoadHookService), typeof(LoadHookState))]
    public partial class LoadHookService : ILoadHookService
    {
        public Task MakeLegacy(string marker)
        {
            State.Marker = marker;
            State.Tags = null;
            return Task.CompletedTask;
        }
    }
}
