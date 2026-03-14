using MemoryPack;
using SharedMeta.Core;
using SharedMeta.Core.Patch;
using SharedMeta.Core.Reactive;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Test.Meta1;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Unit tests for push-based reactive change tracking.
/// Verifies ChangeTracker, ChangeNode tree, generated property setter tracking, pools, ChangeValue, and type-level subscriptions.
/// </summary>
public class ChangeTrackingTests
{
    // ─── ChangeValue factory methods ───

    [Fact]
    public void ChangeValue_FromInt_SetsKindAndValue()
    {
        var cv = ChangeValue.From(42);
        Assert.Equal(ChangeValueKind.Int, cv.Kind);
        Assert.Equal(42, cv.IntValue);
    }

    [Fact]
    public void ChangeValue_FromLong_SetsKindAndValue()
    {
        var cv = ChangeValue.From(123456789L);
        Assert.Equal(ChangeValueKind.Long, cv.Kind);
        Assert.Equal(123456789L, cv.LongValue);
    }

    [Fact]
    public void ChangeValue_FromFloat_SetsKindAndValue()
    {
        var cv = ChangeValue.From(3.14f);
        Assert.Equal(ChangeValueKind.Float, cv.Kind);
        Assert.Equal(3.14f, cv.FloatValue);
    }

    [Fact]
    public void ChangeValue_FromDouble_SetsKindAndValue()
    {
        var cv = ChangeValue.From(2.718281828);
        Assert.Equal(ChangeValueKind.Double, cv.Kind);
        Assert.Equal(2.718281828, cv.DoubleValue);
    }

    [Fact]
    public void ChangeValue_FromBool_SetsKindAndValue()
    {
        var cv = ChangeValue.From(true);
        Assert.Equal(ChangeValueKind.Bool, cv.Kind);
        Assert.True(cv.BoolValue);
    }

    [Fact]
    public void ChangeValue_FromString_SetsKindAndValue()
    {
        var cv = ChangeValue.From("hello");
        Assert.Equal(ChangeValueKind.String, cv.Kind);
        Assert.Equal("hello", cv.StringValue);
    }

    [Fact]
    public void ChangeValue_FromString_Null_SetsKindAndNull()
    {
        var cv = ChangeValue.From((string?)null);
        Assert.Equal(ChangeValueKind.String, cv.Kind);
        Assert.Null(cv.StringValue);
    }

    [Fact]
    public void ChangeValue_FromObject_SetsKindAndValue()
    {
        var obj = new object();
        var cv = ChangeValue.FromObject(obj);
        Assert.Equal(ChangeValueKind.Object, cv.Kind);
        Assert.Same(obj, cv.ObjectValue);
    }

    [Fact]
    public void ChangeValue_FromObject_Null_SetsKindAndNull()
    {
        var cv = ChangeValue.FromObject(null);
        Assert.Equal(ChangeValueKind.Object, cv.Kind);
        Assert.Null(cv.ObjectValue);
    }

    // ─── ChangeValue.ToString ───

    [Theory]
    [InlineData(42, "42")]
    [InlineData(-1, "-1")]
    [InlineData(0, "0")]
    public void ChangeValue_ToString_Int(int value, string expected)
    {
        Assert.Equal(expected, ChangeValue.From(value).ToString());
    }

    [Fact]
    public void ChangeValue_ToString_Long()
    {
        Assert.Equal("999999999999", ChangeValue.From(999999999999L).ToString());
    }

    [Fact]
    public void ChangeValue_ToString_Bool()
    {
        Assert.Equal("True", ChangeValue.From(true).ToString());
        Assert.Equal("False", ChangeValue.From(false).ToString());
    }

    [Fact]
    public void ChangeValue_ToString_String()
    {
        Assert.Equal("hello", ChangeValue.From("hello").ToString());
        Assert.Equal("null", ChangeValue.From((string?)null).ToString());
    }

    [Fact]
    public void ChangeValue_ToString_Object_Null()
    {
        Assert.Equal("null", ChangeValue.FromObject(null).ToString());
    }

    [Fact]
    public void ChangeValue_ToString_None()
    {
        var cv = default(ChangeValue);
        Assert.Equal("none", cv.ToString());
    }

    // ─── ChangeNode struct ───

    [Fact]
    public void ChangeNode_IsLeaf_WhenChildCountZero()
    {
        var node = new ChangeNode { ChildCount = 0, ChildStartIndex = -1 };
        Assert.True(node.IsLeaf);
        Assert.False(node.IsBranch);
    }

    [Fact]
    public void ChangeNode_IsBranch_WhenChildCountPositive()
    {
        var node = new ChangeNode { ChildCount = 3, ChildStartIndex = 5 };
        Assert.False(node.IsLeaf);
        Assert.True(node.IsBranch);
    }

    // ─── ChangeTracker basics ───

    [Fact]
    public void ChangeTracker_RecordLeaf_CreatesChangeNode()
    {
        var tracker = ChangeTracker.Activate();
        var rootIndex = tracker.RecordRoot(new object(), rootTypeId: 999);

        tracker.RecordLeaf(rootIndex, field: 1, ChangeValue.From(10), ChangeValue.From(20));

        Assert.True(tracker.HasChanges);
        tracker.Discard();
    }

    [Fact]
    public void ChangeTracker_FlushAndNotify_InvokesSubscriber()
    {
        bool called = false;
        int receivedTypeId = -1;
        void Handler(ChangeTreeArgs args)
        {
            called = true;
            receivedTypeId = args.RootTypeId;
        }

        ChangeTracker.Subscribe(42, Handler);

        var tracker = ChangeTracker.Activate();
        var rootIndex = tracker.RecordRoot(new object(), rootTypeId: 42);
        tracker.RecordLeaf(rootIndex, field: 0, ChangeValue.From(0), ChangeValue.From(100));

        tracker.FlushAndNotify();

        Assert.True(called);
        Assert.Equal(42, receivedTypeId);

        ChangeTracker.Unsubscribe(42, Handler);
    }

    [Fact]
    public void ChangeTracker_NestedBranch_CreatesTree()
    {
        var tracker = ChangeTracker.Activate();
        var rootIndex = tracker.RecordRoot(new object(), rootTypeId: 1);

        var branchIndex = tracker.RecordBranch(rootIndex, field: 10, collectionIndex: 0);
        tracker.RecordLeaf(branchIndex, field: 20, ChangeValue.From(5), ChangeValue.From(15));

        Assert.True(tracker.HasChanges);
        tracker.Discard();
    }

    [Fact]
    public void ChangeTracker_Discard_CleansUp()
    {
        var tracker = ChangeTracker.Activate();
        tracker.RecordRoot(new object(), rootTypeId: 1);
        Assert.True(tracker.HasChanges);

        tracker.Discard();

        Assert.Null(ChangeTracker.Current);
    }

    [Fact]
    public void ChangeTracker_Activate_SetsCurrent()
    {
        Assert.Null(ChangeTracker.Current);

        var tracker = ChangeTracker.Activate();

        Assert.NotNull(ChangeTracker.Current);
        Assert.Same(tracker, ChangeTracker.Current);
        tracker.Discard();
    }

    [Fact]
    public void ChangeTracker_HasChanges_FalseBeforeRecording()
    {
        var tracker = ChangeTracker.Activate();

        // After Activate, no nodes recorded yet — _nodes is empty
        Assert.False(tracker.HasChanges);

        // After RecordRoot, _nodes has one entry
        tracker.RecordRoot(new object(), rootTypeId: 1);
        Assert.True(tracker.HasChanges);

        tracker.Discard();
    }

    [Fact]
    public void ChangeTracker_FlushAndNotify_NoChanges_NoSubscriberCall()
    {
        bool called = false;
        void Handler(ChangeTreeArgs args) { called = true; }
        ChangeTracker.Subscribe(77, Handler);

        var tracker = ChangeTracker.Activate();
        // Root without children: FlushAndNotify skips roots with ChildCount == 0
        tracker.RecordRoot(new object(), rootTypeId: 77);
        tracker.FlushAndNotify();

        Assert.False(called);
        ChangeTracker.Unsubscribe(77, Handler);
    }

    [Fact]
    public void ChangeTracker_FlushAndNotify_NoSubscriber_DoesNotThrow()
    {
        var tracker = ChangeTracker.Activate();
        var rootIndex = tracker.RecordRoot(new object(), rootTypeId: 9999);
        tracker.RecordLeaf(rootIndex, field: 0, ChangeValue.From(0), ChangeValue.From(1));

        // No subscriber for typeId 9999 — should not throw
        tracker.FlushAndNotify();
    }

    [Fact]
    public void ChangeTracker_MultipleRoots_DispatchesSeparately()
    {
        var roots = new List<int>();
        void Handler1(ChangeTreeArgs args) { roots.Add(1); }
        void Handler2(ChangeTreeArgs args) { roots.Add(2); }

        ChangeTracker.Subscribe(100, Handler1);
        ChangeTracker.Subscribe(200, Handler2);

        var tracker = ChangeTracker.Activate();

        var root1 = tracker.RecordRoot(new object(), rootTypeId: 100);
        tracker.RecordLeaf(root1, field: 0, ChangeValue.From(0), ChangeValue.From(1));

        var root2 = tracker.RecordRoot(new object(), rootTypeId: 200);
        tracker.RecordLeaf(root2, field: 0, ChangeValue.From(0), ChangeValue.From(2));

        tracker.FlushAndNotify();

        Assert.Equal(2, roots.Count);
        Assert.Contains(1, roots);
        Assert.Contains(2, roots);

        ChangeTracker.Unsubscribe(100, Handler1);
        ChangeTracker.Unsubscribe(200, Handler2);
    }

    [Fact]
    public void ChangeTracker_MultipleSubscribers_SameType_AllCalled()
    {
        int callCount = 0;
        void Handler1(ChangeTreeArgs args) { callCount++; }
        void Handler2(ChangeTreeArgs args) { callCount++; }

        ChangeTracker.Subscribe(55, Handler1);
        ChangeTracker.Subscribe(55, Handler2);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 55);
        tracker.RecordLeaf(root, field: 0, ChangeValue.From(0), ChangeValue.From(1));
        tracker.FlushAndNotify();

        Assert.Equal(2, callCount);

        ChangeTracker.Unsubscribe(55, Handler1);
        ChangeTracker.Unsubscribe(55, Handler2);
    }

    [Fact]
    public void ChangeTracker_Unsubscribe_RemovesHandler()
    {
        int callCount = 0;
        void Handler(ChangeTreeArgs args) { callCount++; }

        ChangeTracker.Subscribe(66, Handler);
        ChangeTracker.Unsubscribe(66, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 66);
        tracker.RecordLeaf(root, field: 0, ChangeValue.From(0), ChangeValue.From(1));
        tracker.FlushAndNotify();

        Assert.Equal(0, callCount);
    }

    [Fact]
    public void ChangeTracker_SequentialActivateFlush_PoolReuse()
    {
        // First cycle
        var tracker1 = ChangeTracker.Activate();
        tracker1.RecordRoot(new object(), rootTypeId: 1);
        tracker1.FlushAndNotify();

        // Second cycle — should reuse pooled tracker
        var tracker2 = ChangeTracker.Activate();
        Assert.Same(tracker1, tracker2);
        tracker2.RecordRoot(new object(), rootTypeId: 2);
        tracker2.FlushAndNotify();
    }

    // ─── ChangeTreeArgs tree traversal ───

    [Fact]
    public void ChangeTreeArgs_HasChange_DeepNestedTree()
    {
        bool found = false;
        void Handler(ChangeTreeArgs args)
        {
            // Field 30 is a deep leaf
            found = args.HasChange(30);
        }

        ChangeTracker.Subscribe(88, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 88);
        var branch1 = tracker.RecordBranch(root, field: 10, collectionIndex: 0);
        var branch2 = tracker.RecordBranch(branch1, field: 20, collectionIndex: 1);
        tracker.RecordLeaf(branch2, field: 30, ChangeValue.From(0), ChangeValue.From(99));

        tracker.FlushAndNotify();

        Assert.True(found);

        ChangeTracker.Unsubscribe(88, Handler);
    }

    [Fact]
    public void ChangeTreeArgs_FindLeaf_DeepNestedTree()
    {
        ChangeNode? foundLeaf = null;
        void Handler(ChangeTreeArgs args)
        {
            foundLeaf = args.FindLeaf(30);
        }

        ChangeTracker.Subscribe(89, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 89);
        var branch1 = tracker.RecordBranch(root, field: 10, collectionIndex: 0);
        var branch2 = tracker.RecordBranch(branch1, field: 20);
        tracker.RecordLeaf(branch2, field: 30, ChangeValue.From(5), ChangeValue.From(50));

        tracker.FlushAndNotify();

        Assert.NotNull(foundLeaf);
        Assert.Equal(5, foundLeaf.Value.OldValue.IntValue);
        Assert.Equal(50, foundLeaf.Value.NewValue.IntValue);

        ChangeTracker.Unsubscribe(89, Handler);
    }

    [Fact]
    public void ChangeTreeArgs_FindLeaf_ReturnsNull_WhenNotFound()
    {
        ChangeNode? result = null;
        bool handlerCalled = false;
        void Handler(ChangeTreeArgs args)
        {
            handlerCalled = true;
            result = args.FindLeaf(999); // non-existent field
        }

        ChangeTracker.Subscribe(90, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 90);
        tracker.RecordLeaf(root, field: 1, ChangeValue.From(0), ChangeValue.From(1));
        tracker.FlushAndNotify();

        Assert.True(handlerCalled);
        Assert.Null(result);

        ChangeTracker.Unsubscribe(90, Handler);
    }

    [Fact]
    public void ChangeTreeArgs_HasChange_ReturnsFalse_WhenFieldNotPresent()
    {
        bool has = true;
        void Handler(ChangeTreeArgs args) { has = args.HasChange(999); }

        ChangeTracker.Subscribe(91, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 91);
        tracker.RecordLeaf(root, field: 1, ChangeValue.From(0), ChangeValue.From(1));
        tracker.FlushAndNotify();

        Assert.False(has);

        ChangeTracker.Unsubscribe(91, Handler);
    }

    [Fact]
    public void ChangeTreeArgs_MultipleLeaves_SameParent()
    {
        int leafCount = 0;
        void Handler(ChangeTreeArgs args)
        {
            if (args.FindLeaf(1) != null) leafCount++;
            if (args.FindLeaf(2) != null) leafCount++;
            if (args.FindLeaf(3) != null) leafCount++;
        }

        ChangeTracker.Subscribe(92, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 92);
        tracker.RecordLeaf(root, field: 1, ChangeValue.From(0), ChangeValue.From(10));
        tracker.RecordLeaf(root, field: 2, ChangeValue.From(0), ChangeValue.From(20));

        tracker.FlushAndNotify();

        Assert.Equal(2, leafCount); // field 1 and 2 found, field 3 not found

        ChangeTracker.Unsubscribe(92, Handler);
    }

    // ─── Generated property setter tracking ───

    [Fact]
    public void GeneratedProperty_RecordsChange_WhenReactiveCounterSet()
    {
        var state = new CounterState();

        var tracker = ChangeTracker.Activate();

        // Setting via generated property setter triggers RecordFieldChange
        state.ReactiveCounter = 42;

        Assert.True(tracker.HasChanges);
        tracker.Discard();
    }

    [Fact]
    public void GeneratedProperty_NoChange_WhenSameValue()
    {
        var state = new CounterState();
        // _reactiveCounter defaults to 0

        var tracker = ChangeTracker.Activate();

        // Setting same value — EqualityComparer check in generated setter skips tracking
        state.ReactiveCounter = 0;

        Assert.False(tracker.HasChanges);
        tracker.Discard();
    }

    [Fact]
    public void GeneratedProperty_DetectsChange_WithCorrectFieldId()
    {
        bool reactiveCounterChanged = false;

        void Handler(ChangeTreeArgs args)
        {
            reactiveCounterChanged = args.HasChange((int)TrackingProperty.CounterState_ReactiveCounter);
        }

        ChangeTracker.Subscribe(CounterState.TrackedTypeId, Handler);

        var state = new CounterState();

        var tracker = ChangeTracker.Activate();
        state.ReactiveCounter = 100;
        tracker.FlushAndNotify();

        Assert.True(reactiveCounterChanged);

        ChangeTracker.Unsubscribe(CounterState.TrackedTypeId, Handler);
    }

    // ─── Type-level subscription ───

    [Fact]
    public void TypeLevelSubscription_FiresOnFlush()
    {
        object? receivedRoot = null;
        int oldValue = -1, newValue = -1;
        bool foundLeaf = false;

        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += args =>
        {
            receivedRoot = args.Root;
            var leaf = args.FindLeaf((int)TrackingProperty.CounterState_ReactiveCounter);
            if (leaf != null)
            {
                foundLeaf = true;
                oldValue = leaf.Value.OldValue.IntValue;
                newValue = leaf.Value.NewValue.IntValue;
            }
        };

        var state = new CounterState();

        var tracker = ChangeTracker.Activate();
        state.ReactiveCounter = 99;
        tracker.FlushAndNotify();

        Assert.Same(state, receivedRoot);
        Assert.True(foundLeaf);
        Assert.Equal(0, oldValue);
        Assert.Equal(99, newValue);

        TrackedCounterState.Unregister();
    }

    [Fact]
    public void TypeLevelSubscription_Unregister_StopsNotifications()
    {
        int callCount = 0;
        TrackedCounterState.Register();
        TrackedCounterState.OnChanged += _ => callCount++;

        // First flush — should fire
        var state1 = new CounterState();
        var tracker1 = ChangeTracker.Activate();
        state1.ReactiveCounter = 1;
        tracker1.FlushAndNotify();
        Assert.Equal(1, callCount);

        TrackedCounterState.Unregister();

        // Second flush — should not fire
        var state2 = new CounterState();
        var tracker2 = ChangeTracker.Activate();
        state2.ReactiveCounter = 2;
        tracker2.FlushAndNotify();
        Assert.Equal(1, callCount); // unchanged
    }

    // ─── ChangeTreeArgs_HasChange_FindsField ───

    [Fact]
    public void ChangeTreeArgs_HasChange_FindsField()
    {
        bool hasReactiveCounter = false;

        void Handler(ChangeTreeArgs args)
        {
            hasReactiveCounter = args.HasChange((int)TrackingProperty.CounterState_ReactiveCounter);
        }

        ChangeTracker.Subscribe(CounterState.TrackedTypeId, Handler);

        var state = new CounterState();

        var tracker = ChangeTracker.Activate();
        state.ReactiveCounter = 2;
        tracker.FlushAndNotify();

        Assert.True(hasReactiveCounter);

        ChangeTracker.Unsubscribe(CounterState.TrackedTypeId, Handler);
    }

    // ─── Serialization ───

    [Fact]
    public void ReactiveField_SerializesAsPlainInt()
    {
        var state = new CounterState();
        state.ReactiveCounter = 42;

        var bytes = MemoryPackSerializer.Serialize(state);
        var deserialized = MemoryPackSerializer.Deserialize<CounterState>(bytes)!;

        Assert.Equal(42, deserialized.ReactiveCounter);
    }

    [Fact]
    public void ReactiveField_Serialization_DefaultValue()
    {
        var state = new CounterState();
        // ReactiveCounter defaults to 0

        var bytes = MemoryPackSerializer.Serialize(state);
        var deserialized = MemoryPackSerializer.Deserialize<CounterState>(bytes)!;

        Assert.Equal(0, deserialized.ReactiveCounter);
    }

    [Fact]
    public void ReactiveField_Serialization_NegativeValue()
    {
        var state = new CounterState();
        state.ReactiveCounter = -100;

        var bytes = MemoryPackSerializer.Serialize(state);
        var deserialized = MemoryPackSerializer.Deserialize<CounterState>(bytes)!;

        Assert.Equal(-100, deserialized.ReactiveCounter);
    }

    // ─── Pools ───

    private class TestPoolable { }

    [Fact]
    public void Pools_RentReturn_Reuses()
    {
        var list1 = ListPool<int>.Rent();
        list1.Add(1);
        list1.Add(2);
        ListPool<int>.Return(list1);

        var list2 = ListPool<int>.Rent();
        Assert.Same(list1, list2);
        Assert.Empty(list2);
        ListPool<int>.Return(list2);

        var obj1 = ObjectPool<TestPoolable>.Rent();
        ObjectPool<TestPoolable>.Return(obj1);

        var obj2 = ObjectPool<TestPoolable>.Rent();
        Assert.Same(obj1, obj2);
        ObjectPool<TestPoolable>.Return(obj2);
    }

    [Fact]
    public void ListPool_MultipleRentReturn_AllReused()
    {
        var list1 = ListPool<string>.Rent();
        var list2 = ListPool<string>.Rent();
        var list3 = ListPool<string>.Rent();

        list1.Add("a");
        list2.Add("b");
        list3.Add("c");

        ListPool<string>.Return(list3);
        ListPool<string>.Return(list2);
        ListPool<string>.Return(list1);

        // Rent back — pool is LIFO so we get list1 first, then list2, then list3
        var r1 = ListPool<string>.Rent();
        var r2 = ListPool<string>.Rent();
        var r3 = ListPool<string>.Rent();

        // All should be empty (cleared on return)
        Assert.Empty(r1);
        Assert.Empty(r2);
        Assert.Empty(r3);

        ListPool<string>.Return(r1);
        ListPool<string>.Return(r2);
        ListPool<string>.Return(r3);
    }

    [Fact]
    public void ObjectPool_MultipleRentReturn()
    {
        var v1 = ObjectPool<TestPoolable>.Rent();
        var v2 = ObjectPool<TestPoolable>.Rent();

        ObjectPool<TestPoolable>.Return(v2);
        ObjectPool<TestPoolable>.Return(v1);

        var r1 = ObjectPool<TestPoolable>.Rent();
        var r2 = ObjectPool<TestPoolable>.Rent();

        // Both should be reused instances
        Assert.True(r1 == v1 || r1 == v2);
        Assert.True(r2 == v1 || r2 == v2);

        ObjectPool<TestPoolable>.Return(r1);
        ObjectPool<TestPoolable>.Return(r2);
    }

    // ─── Edge cases ───

    [Fact]
    public void ChangeTracker_BranchWithCollectionIndex_Preserved()
    {
        int receivedCollectionIndex = -1;
        void Handler(ChangeTreeArgs args)
        {
            var rootNode = args.Nodes[args.RootNodeIndex];
            if (rootNode.ChildCount > 0)
            {
                var branchNode = args.Nodes[rootNode.ChildStartIndex];
                receivedCollectionIndex = branchNode.CollectionIndex;
            }
        }

        ChangeTracker.Subscribe(95, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 95);
        var branch = tracker.RecordBranch(root, field: 10, collectionIndex: 7);
        tracker.RecordLeaf(branch, field: 20, ChangeValue.From(0), ChangeValue.From(1));

        tracker.FlushAndNotify();

        Assert.Equal(7, receivedCollectionIndex);

        ChangeTracker.Unsubscribe(95, Handler);
    }

    [Fact]
    public void ChangeTracker_RootReceivesObjectRef()
    {
        object? receivedRef = null;
        void Handler(ChangeTreeArgs args) { receivedRef = args.Root; }

        ChangeTracker.Subscribe(96, Handler);

        var myObj = new object();
        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(myObj, rootTypeId: 96);
        tracker.RecordLeaf(root, field: 0, ChangeValue.From(0), ChangeValue.From(1));

        tracker.FlushAndNotify();

        Assert.Same(myObj, receivedRef);

        ChangeTracker.Unsubscribe(96, Handler);
    }

    [Fact]
    public void ChangeTracker_FlushAndNotify_ClearsCurrentAfward()
    {
        var tracker = ChangeTracker.Activate();
        Assert.NotNull(ChangeTracker.Current);

        tracker.RecordRoot(new object(), rootTypeId: 1);
        tracker.FlushAndNotify();

        Assert.Null(ChangeTracker.Current);
    }

    [Fact]
    public void ChangeTracker_ChangeValues_PreservedInLeaf()
    {
        ChangeValue capturedOld = default, capturedNew = default;
        void Handler(ChangeTreeArgs args)
        {
            var leaf = args.FindLeaf(5);
            if (leaf != null)
            {
                capturedOld = leaf.Value.OldValue;
                capturedNew = leaf.Value.NewValue;
            }
        }

        ChangeTracker.Subscribe(97, Handler);

        var tracker = ChangeTracker.Activate();
        var root = tracker.RecordRoot(new object(), rootTypeId: 97);
        tracker.RecordLeaf(root, field: 5, ChangeValue.From("before"), ChangeValue.From("after"));

        tracker.FlushAndNotify();

        Assert.Equal(ChangeValueKind.String, capturedOld.Kind);
        Assert.Equal("before", capturedOld.StringValue);
        Assert.Equal(ChangeValueKind.String, capturedNew.Kind);
        Assert.Equal("after", capturedNew.StringValue);

        ChangeTracker.Unsubscribe(97, Handler);
    }

    // ─── Generated property + compound assignment ───

    [Fact]
    public void GeneratedProperty_CompoundAssignment_RecordsChange()
    {
        var state = new CounterState();
        state.ReactiveCounter = 10;

        var tracker = ChangeTracker.Activate();
        // Discard the initial set to start fresh
        tracker.Discard();

        tracker = ChangeTracker.Activate();
        state.ReactiveCounter += 5;

        Assert.True(tracker.HasChanges);
        tracker.Discard();
    }

    [Fact]
    public void GeneratedProperty_NoTracker_NoException()
    {
        // When ChangeTracker.Current is null (server-side), generated setter just writes the value
        Assert.Null(ChangeTracker.Current);

        var state = new CounterState();
        state.ReactiveCounter = 42;

        Assert.Equal(42, state.ReactiveCounter);
    }

    [Fact]
    public void GeneratedProperty_MultipleChanges_AllRecorded()
    {
        int changeCount = 0;
        void Handler(ChangeTreeArgs args) { changeCount++; }

        ChangeTracker.Subscribe(CounterState.TrackedTypeId, Handler);

        var state = new CounterState();
        var tracker = ChangeTracker.Activate();

        state.ReactiveCounter = 10;
        state.ReactiveCounter = 20; // second change on same field, same root

        tracker.FlushAndNotify();

        // One notification per root (not per change)
        Assert.Equal(1, changeCount);

        ChangeTracker.Unsubscribe(CounterState.TrackedTypeId, Handler);
    }

    [Fact]
    public void GeneratedProperty_RecordFieldChange_LazilyCreatesRoot()
    {
        object? capturedRoot = null;
        void Handler(ChangeTreeArgs args) { capturedRoot = args.Root; }

        ChangeTracker.Subscribe(CounterState.TrackedTypeId, Handler);

        var state = new CounterState();
        var tracker = ChangeTracker.Activate();

        // No explicit RecordRoot — RecordFieldChange creates it lazily
        state.ReactiveCounter = 42;

        tracker.FlushAndNotify();

        Assert.Same(state, capturedRoot);

        ChangeTracker.Unsubscribe(CounterState.TrackedTypeId, Handler);
    }
}
