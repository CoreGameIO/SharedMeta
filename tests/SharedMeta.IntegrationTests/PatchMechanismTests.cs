using SharedMeta.Core;
using SharedMeta.Core.Patch;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Unit tests for the ServerPatch mechanism.
/// Tests PatchNode, PatchWrapper, and PatchApplier without an Orleans cluster.
/// </summary>
public class PatchMechanismTests
{
    private readonly IMetaSerializer _serializer = new MemoryPackMetaSerializer();

    /// <summary>
    /// Modify Sum via PatchWrapper, apply patch to a copy, verify both states match.
    /// </summary>
    [Fact]
    public void ValueType_PatchAndApply_StatesMatch()
    {
        // Arrange: create original state
        var original = new CounterState { Sum = 100, LastServerTimeTicks = 5000 };
        original.Operations.Add(new CounterOperation { CallerId = "p1", Value = 50, ClientSequence = 1 });

        // Deep copy via serialize/deserialize
        var copy = _serializer.Unpack<CounterState>(_serializer.Pack(original))!;

        // Act: modify original through PatchWrapper
        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        wrapper.Sum = 200;

        // Prune and apply
        root.Prune();
        Assert.True(root.HasChanges);

        var patchBytes = _serializer.Pack(root);
        var deserialized = _serializer.Unpack<PatchNode>(patchBytes)!;
        CounterStatePatchApplier.Apply(copy, deserialized, _serializer);

        // Assert: both states match
        Assert.Equal(200, original.Sum);
        Assert.Equal(200, copy.Sum);
        // Unchanged fields remain the same
        Assert.Equal(original.LastServerTimeTicks, copy.LastServerTimeTicks);
        Assert.Single(copy.Operations);
        Assert.Equal("p1", copy.Operations[0].CallerId);
    }

    /// <summary>
    /// Modify a collection via PatchableList, apply patch to a copy, verify both match.
    /// </summary>
    [Fact]
    public void Collection_AddItem_PatchAndApply_StatesMatch()
    {
        // Arrange
        var original = new CounterState { Sum = 10 };
        original.Operations.Add(new CounterOperation { CallerId = "p1", Value = 10, ClientSequence = 1 });

        var copy = _serializer.Unpack<CounterState>(_serializer.Pack(original))!;

        // Act: add an operation through the wrapper
        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        wrapper.Operations.Add(new CounterOperation { CallerId = "p2", Value = 20, ClientSequence = 2 });
        wrapper.Sum = 30;

        root.Prune();
        Assert.True(root.HasChanges);

        var patchBytes = _serializer.Pack(root);
        var patch = _serializer.Unpack<PatchNode>(patchBytes)!;
        CounterStatePatchApplier.Apply(copy, patch, _serializer);

        // Assert
        Assert.Equal(30, copy.Sum);
        Assert.Equal(2, copy.Operations.Count);
        Assert.Equal("p2", copy.Operations[1].CallerId);
        Assert.Equal(20, copy.Operations[1].Value);
    }

    /// <summary>
    /// Only modify Sum, verify Operations field is NOT included in the patch.
    /// </summary>
    [Fact]
    public void PartialChange_OnlyChangedFieldsInPatch()
    {
        var original = new CounterState { Sum = 100, LastServerTimeTicks = 999 };
        original.Operations.Add(new CounterOperation { CallerId = "x", Value = 100, ClientSequence = 1 });

        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        // Only change Sum
        wrapper.Sum = 200;

        root.Prune();
        Assert.True(root.HasChanges);

        // Patch should have exactly 1 child (Sum, FieldId=1)
        Assert.NotNull(root.Children);
        Assert.Single(root.Children);
        Assert.Equal(1, root.Children[0].FieldId); // [Id(1)] = Sum
        Assert.True(root.Children[0].IsTerminal);
    }

    /// <summary>
    /// No modifications → Prune removes everything → no HasChanges.
    /// </summary>
    [Fact]
    public void NoChanges_PruneRemovesAll()
    {
        var state = new CounterState { Sum = 42 };
        var root = new PatchNode { FieldId = -1 };
        var _ = new CounterStatePatchWrapper(state, root, _serializer);

        // No modifications at all
        root.Prune();

        Assert.False(root.HasChanges);
        Assert.Null(root.Children);
    }

    /// <summary>
    /// Multiple field changes in one patch, all applied correctly.
    /// </summary>
    [Fact]
    public void MultipleFields_AllApplied()
    {
        var original = new CounterState { Sum = 0, LastServerTimeTicks = 0 };

        var copy = _serializer.Unpack<CounterState>(_serializer.Pack(original))!;

        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        wrapper.Sum = 999;
        wrapper.LastServerTimeTicks = 12345;
        wrapper.Operations.Add(new CounterOperation { CallerId = "test", Value = 999, ClientSequence = 1 });

        root.Prune();

        var patch = _serializer.Unpack<PatchNode>(_serializer.Pack(root))!;
        CounterStatePatchApplier.Apply(copy, patch, _serializer);

        Assert.Equal(999, copy.Sum);
        Assert.Equal(12345, copy.LastServerTimeTicks);
        Assert.Single(copy.Operations);
        Assert.Equal("test", copy.Operations[0].CallerId);
    }

    /// <summary>
    /// Replace entire collection via SetOperations, verify full replacement in patch.
    /// </summary>
    [Fact]
    public void CollectionFullReplace_PatchApplied()
    {
        var original = new CounterState { Sum = 10 };
        original.Operations.Add(new CounterOperation { CallerId = "old", Value = 1, ClientSequence = 1 });
        original.Operations.Add(new CounterOperation { CallerId = "old", Value = 2, ClientSequence = 2 });

        var copy = _serializer.Unpack<CounterState>(_serializer.Pack(original))!;
        Assert.Equal(2, copy.Operations.Count);

        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        // Full replacement
        wrapper.SetOperations(new List<CounterOperation>
        {
            new() { CallerId = "new", Value = 100, ClientSequence = 1 }
        });

        root.Prune();

        var patch = _serializer.Unpack<PatchNode>(_serializer.Pack(root))!;
        CounterStatePatchApplier.Apply(copy, patch, _serializer);

        Assert.Single(copy.Operations);
        Assert.Equal("new", copy.Operations[0].CallerId);
        Assert.Equal(100, copy.Operations[0].Value);
    }

    /// <summary>
    /// Non-tracking wrapper (null PatchNode) — mutations still work but no patch built.
    /// </summary>
    [Fact]
    public void NonTrackingWrapper_MutationsWork_NoPatch()
    {
        var state = new CounterState { Sum = 10 };
        var wrapper = new CounterStatePatchWrapper(state, null, _serializer);

        Assert.False(wrapper.IsTracking);

        // Mutations still go through to the state
        wrapper.Sum = 99;
        Assert.Equal(99, state.Sum);

        wrapper.Operations.Add(new CounterOperation { CallerId = "test", Value = 99 });
        Assert.Single(state.Operations);
    }

    /// <summary>
    /// PatchNode serialization round-trip preserves structure.
    /// </summary>
    [Fact]
    public void PatchNode_SerializationRoundTrip()
    {
        var root = new PatchNode { FieldId = -1 };
        var child1 = root.GetOrCreateChild(1);
        child1.MarkTerminal(_serializer.Pack(42L));
        var child2 = root.GetOrCreateChild(0);
        child2.MarkTerminal(_serializer.Pack(new List<int> { 1, 2, 3 }));

        root.Prune();
        Assert.True(root.HasChanges);

        var bytes = _serializer.Pack(root);
        var deserialized = _serializer.Unpack<PatchNode>(bytes)!;

        Assert.Equal(-1, deserialized.FieldId);
        Assert.NotNull(deserialized.Children);
        Assert.Equal(2, deserialized.Children.Count);

        var sum = deserialized.Children.First(c => c.FieldId == 1);
        Assert.True(sum.IsTerminal);
        Assert.Equal(42L, _serializer.Unpack<long>(sum.Value!));

        var ops = deserialized.Children.First(c => c.FieldId == 0);
        Assert.True(ops.IsTerminal);
        var list = _serializer.Unpack<List<int>>(ops.Value!);
        Assert.Equal(new List<int> { 1, 2, 3 }, list);
    }

    /// <summary>
    /// Collection Remove operation auto-marks dirty.
    /// </summary>
    [Fact]
    public void Collection_Remove_AutoTracksDirty()
    {
        var original = new CounterState { Sum = 30 };
        original.Operations.Add(new CounterOperation { CallerId = "a", Value = 10 });
        original.Operations.Add(new CounterOperation { CallerId = "b", Value = 20 });

        var copy = _serializer.Unpack<CounterState>(_serializer.Pack(original))!;

        var root = new PatchNode { FieldId = -1 };
        var wrapper = new CounterStatePatchWrapper(original, root, _serializer);

        wrapper.Operations.RemoveAt(0);

        root.Prune();
        Assert.True(root.HasChanges);

        var patch = _serializer.Unpack<PatchNode>(_serializer.Pack(root))!;
        CounterStatePatchApplier.Apply(copy, patch, _serializer);

        Assert.Single(copy.Operations);
        Assert.Equal("b", copy.Operations[0].CallerId);
    }
}
