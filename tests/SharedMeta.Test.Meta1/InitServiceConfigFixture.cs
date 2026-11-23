using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    // [MetaInit] reading a [ServiceConfig] on a state with NO [MetaStateVersion] — the shape a
    // "grant the starting kit from balance config" init has. Without [MetaStateVersion] the
    // generated RunInitAsync sets no config context of its own, so init depends entirely on the
    // framework binding Context.Configs before it runs.

    [MetaConfigVersion(Client = "1.0.0", Config = "1.0.0")]
    [MetaConfigVersion(Client = "2.0.0", Config = "2.0.0")]
    public class InitStartKitConfig
    {
        public int Major { get; set; }
        public int StartGold { get; set; }
    }

    [SharedState]
    [MemoryPackable]
    public partial class InitStartKitState : ISharedState
    {
        [MemoryPackOrder(0)] public int Gold { get; set; }
        /// <summary>Major of the config [MetaInit] saw — proves which branch init ran under.</summary>
        [MemoryPackOrder(1)] public int InitConfigMajor { get; set; }
    }

    [MetaService(StateType = typeof(InitStartKitState))]
    [ServiceConfig(typeof(InitStartKitConfig), "Kit")]
    public interface IInitStartKitService : IMetaService
    {
        [MetaMethod(Mode = ExecutionMode.Server)]
        Task AddGold(int amount);

        /// <summary>Admin-shaped entry point: reaches a never-subscribed entity via the server API.</summary>
        [MetaMethod(Alias = "ReadGold", Mode = ExecutionMode.Server, GenerateClientApi = false)]
        int ReadGold();
    }

    [MetaServiceImpl(typeof(IInitStartKitService), typeof(InitStartKitState))]
    public partial class InitStartKitService : IInitStartKitService
    {
        [MetaInit]
        public Task<int> Init(int version)
        {
            if (version < 1)
            {
                State.Gold = Kit.StartGold;
                State.InitConfigMajor = Kit.Major;
                return Task.FromResult(1);
            }
            return Task.FromResult(version);
        }

        public Task AddGold(int amount)
        {
            State.Gold += amount;
            return Task.CompletedTask;
        }

        public int ReadGold() => State.Gold;
    }

    // ─── Fresh-at-target vs step-by-step migration ───────────────────────────────
    // A fresh entity must be created directly at the schema its client allows, under that
    // client's config; a persisted one climbs the remaining steps, each under the CURRENT
    // client's config — never a historical "transition" branch.

    [MetaConfigVersion(Client = "1.x.*", Config = "1.x.*")]
    [MetaConfigVersion(Client = "2.x.*", Config = "2.x.*")]
    [MetaConfigVersion(Client = "3.x.*", Config = "3.x.*")]
    public class FreshInitConfig
    {
        public int Major { get; set; }
        public int Minor { get; set; }
    }

    [MemoryPackable]
    [MetaStateVersion(2, "2.0", typeof(FreshInitConfig))]
    [MetaStateVersion(3, "3.0", typeof(FreshInitConfig))]
    public partial class FreshInitState : ISharedState
    {
        [MemoryPackOrder(0)] public int Value { get; set; }
        /// <summary>One entry per [MetaInit] call: "from>target@configMajor.configMinor".</summary>
        [MemoryPackOrder(1)] public List<string> Calls { get; set; } = new();
    }

    [MetaService(StateType = typeof(FreshInitState))]
    [ServiceConfig(typeof(FreshInitConfig), "Config")]
    public interface IFreshInitService : IMetaService
    {
        [MetaMethod(Alias = "GetValue", Mode = ExecutionMode.Query)]
        int GetValue();

        [MetaMethod(Alias = "GetCalls", Mode = ExecutionMode.Query)]
        string GetCalls();
    }

    [MetaServiceImpl(typeof(IFreshInitService), typeof(FreshInitState))]
    public partial class FreshInitService : IFreshInitService
    {
        public int GetValue() => State.Value;

        public string GetCalls() => string.Join(";", State.Calls);

        [MetaInit]
        public Task<int> Init(int version, int target)
        {
            State.Calls.Add($"{version}>{target}@{Config.Major}.{Config.Minor}");
            if (version == 0) { State.Value = target; return Task.FromResult(target); }
            if (version < 2 && target >= 2) { State.Value = 2; return Task.FromResult(2); }
            if (version < 3 && target >= 3) { State.Value = 3; return Task.FromResult(3); }
            return Task.FromResult(version);
        }
    }
}
