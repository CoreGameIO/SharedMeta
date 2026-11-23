using System.Text.Json.Nodes;
using SharedMeta.Inspector;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>Inspector pieces that need no cluster: JSON encoding, redaction, store eviction.</summary>
public class InspectorUnitTests
{
    private sealed class Account
    {
        public string Name = "ann";
        public string AuthToken = "abc";
        public Dictionary<string, int> Scores = new() { ["level"] = 3, ["secretBonus"] = 9 };
        public Dictionary<(int X, int Y), string> Cells = new() { [(1, 2)] = "tree" };
        public Dictionary<int, string> ById = new() { [7] = "seven" };
        public byte[] Blob = new byte[1000];
        public Account? Self;
    }

    [Fact]
    public void Json_RedactsMembersAndKeys_AndSurvivesAwkwardShapes()
    {
        var json = new MetaJson(new[] { "*token*", "*secret*" }, maxBinaryBytes: 16);
        var account = new Account();
        account.Self = account;

        var node = json.ToNode(account)!.AsObject();

        Assert.Equal("ann", node["Name"]!.GetValue<string>());
        Assert.Equal("***", node["AuthToken"]!.GetValue<string>());
        Assert.Equal(3, node["Scores"]!["level"]!.GetValue<int>());
        Assert.Equal("***", node["Scores"]!["secretBonus"]!.GetValue<string>());
        Assert.Equal("seven", node["ById"]!["7"]!.GetValue<string>());
        var cell = Assert.Single(node["Cells"]!.AsArray())!;
        Assert.Equal("tree", cell["Value"]!.GetValue<string>());
        Assert.Equal(1000, node["Blob"]!["_bytes"]!.GetValue<int>());
        Assert.Null(node["Self"]); // cycle ignored
    }

    [Fact]
    public void Store_EvictsOldestAndCleansEveryIndex()
    {
        var store = new InMemoryEventStore(maxEvents: 3, maxBytes: long.MaxValue);
        var events = Enumerable.Range(0, 5).Select(i => new MetaInspectorEvent
        {
            Kind = InspectorEventKinds.CallCompleted,
            EntityKey = "S:e" + (i % 2),
            CallId = "c" + i,
            CallerId = "p" + i,
            Service = "ISvc",
            Method = "M",
        }).ToList();
        store.WriteAsync(events);

        Assert.Equal(3, store.Count);
        Assert.Equal(2, store.Evicted);
        Assert.Empty(store.Query(new InspectorQuery { CallId = "c0" }));
        Assert.Empty(store.Query(new InspectorQuery { PlayerId = "p1" }));
        Assert.Equal(new[] { "c2", "c4" }, store.Query(new InspectorQuery { EntityKey = "S:e0" }).Select(e => e.CallId));
        Assert.Equal(3, store.Query(new InspectorQuery { Method = "ISvc.M" }).Count);
        Assert.Contains(store.Entities(), e => e.EntityKey == "S:e1" && e.Events == 1);
    }

    [Fact]
    public void Store_AppliesEveryFilter_AndIndexesFoldedRecipients()
    {
        var store = new InMemoryEventStore(maxEvents: 3, maxBytes: long.MaxValue);
        store.WriteAsync(new[]
        {
            new MetaInspectorEvent { Kind = InspectorEventKinds.CallCompleted, StateType = "S", EntityId = "e1", EntityKey = "S:e1", CallId = "c1", CallerId = "alice" },
            new MetaInspectorEvent { Kind = InspectorEventKinds.CallCompleted, StateType = "S", EntityId = "e1", EntityKey = "S:e1", CallId = "c2", CallerId = "bob" },
            new MetaInspectorEvent { Kind = InspectorEventKinds.BroadcastSent, StateType = "S", EntityId = "e1", EntityKey = "S:e1", CallId = "c2", TargetPlayers = new() { "carol", "dan" } },
        });

        Assert.Equal(new[] { "c1" }, store.Query(new InspectorQuery { EntityKey = "S:e1", PlayerId = "alice" }).Select(e => e.CallId));
        Assert.Single(store.Query(new InspectorQuery { CallId = "c2", PlayerId = "bob" }));
        Assert.Empty(store.Query(new InspectorQuery { CallId = "c1", EntityKey = "S:e2" }));
        Assert.Single(store.Query(new InspectorQuery { PlayerId = "dan" }));
        Assert.Empty(store.Query(new InspectorQuery { StateType = "T" }));
        Assert.Equal(3, store.Query(new InspectorQuery { StateType = "S" }).Count);

        // Evicting the broadcast must clean the recipients' index too.
        store.WriteAsync(Enumerable.Range(0, 3).Select(i => new MetaInspectorEvent { Kind = InspectorEventKinds.CallCompleted, EntityKey = "S:x" + i }).ToList());
        Assert.Empty(store.Query(new InspectorQuery { PlayerId = "dan" }));
    }

    [Fact]
    public async Task WatchAtOff_DoesNotKeepTheInspectorActive()
    {
        await using var runtime = new InspectorRuntime();
        var gate = (SharedMeta.Server.Core.Telemetry.IMetaCallObservationGate)runtime.ServerObserver;
        runtime.UpdateCaptureSettings(s => s.WatchedEntities["e"] = InspectorLevel.Off);
        Assert.False(gate.ObservesCalls);
        runtime.UpdateCaptureSettings(s => s.WatchedEntities["e"] = InspectorLevel.Ids);
        Assert.True(gate.ObservesCalls);
    }

    [Fact]
    public void Path_SelectsIntoState()
    {
        var state = JsonNode.Parse("{\"Inventory\":{\"Items\":[{\"Count\":4}]}}");
        Assert.Equal(4, InspectorService.Select(state, "inventory.items[0].count")!.GetValue<int>());
        Assert.Null(InspectorService.Select(state, "Inventory.Items[3]"));
    }
}
