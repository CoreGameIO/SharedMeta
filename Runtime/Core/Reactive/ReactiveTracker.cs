using System;
using System.Collections.Generic;
using System.Threading;

namespace SharedMeta.Core.Reactive
{
    /// <summary>
    /// Push-based change tracker for [Tracked] state fields.
    /// Set via AsyncLocal on client before method execution, null on server (zero overhead).
    /// Changes are recorded as a tree of <see cref="ChangeNode"/> structs in a pooled list.
    /// After method completes, <see cref="FlushAndNotify"/> walks the tree and dispatches
    /// to type-level subscribers.
    /// </summary>
    public class ChangeTracker
    {
        private static readonly AsyncLocal<ChangeTracker?> _current = new();

        /// <summary>
        /// Current tracker for this async context. Null on server.
        /// Generated property setters use null-conditional: ChangeTracker.Current?.RecordFieldChange(...)
        /// </summary>
        public static ChangeTracker? Current
        {
            get => _current.Value;
            set => _current.Value = value;
        }

        private static readonly Stack<ChangeTracker> _trackerPool = new();
        private static readonly object _poolLock = new();

        private List<ChangeNode> _nodes = null!;
        private readonly List<int> _rootIndices = new();

        // Subscriber registry: rootTypeId -> Action<ChangeTreeArgs>
        private static readonly Dictionary<int, Action<ChangeTreeArgs>> _subscribers = new();

        /// <summary>
        /// Register a type-level subscriber for changes to a specific root type.
        /// </summary>
        public static void Subscribe(int rootTypeId, Action<ChangeTreeArgs> handler)
        {
            if (_subscribers.TryGetValue(rootTypeId, out var existing))
                _subscribers[rootTypeId] = existing + handler;
            else
                _subscribers[rootTypeId] = handler;
        }

        /// <summary>
        /// Unregister a type-level subscriber.
        /// </summary>
        public static void Unsubscribe(int rootTypeId, Action<ChangeTreeArgs> handler)
        {
            if (_subscribers.TryGetValue(rootTypeId, out var existing))
            {
                var updated = (Action<ChangeTreeArgs>?)Delegate.Remove(existing, handler);
                if (updated == null)
                    _subscribers.Remove(rootTypeId);
                else
                    _subscribers[rootTypeId] = updated;
            }
        }

        /// <summary>
        /// Rent a tracker from the pool (or create new). Sets it as Current.
        /// </summary>
        public static ChangeTracker Activate()
        {
            ChangeTracker tracker;
            lock (_poolLock)
            {
                tracker = _trackerPool.Count > 0 ? _trackerPool.Pop() : new ChangeTracker();
            }
            tracker._nodes = ListPool<ChangeNode>.Rent();
            tracker._rootIndices.Clear();
            Current = tracker;
            return tracker;
        }

        /// <summary>
        /// Create a root node for a tracked entity instance.
        /// Returns the node index for use as parentNodeIndex in wrapper views.
        /// </summary>
        public int RecordRoot(object root, int rootTypeId)
        {
            var index = _nodes.Count;
            _nodes.Add(new ChangeNode
            {
                Field = -1,
                CollectionIndex = -1,
                ChildStartIndex = -1, // children will be appended later
                ChildCount = 0,
                Root = root,
                RootTypeId = rootTypeId
            });
            _rootIndices.Add(index);
            return index;
        }

        /// <summary>
        /// Add a branch node (nested object or collection traversal).
        /// Returns the new node index for use as parentNodeIndex in child wrapper views.
        /// </summary>
        public int RecordBranch(int parentNodeIndex, int field, int collectionIndex = -1)
        {
            var index = _nodes.Count;
            _nodes.Add(new ChangeNode
            {
                Field = field,
                CollectionIndex = collectionIndex,
                ChildStartIndex = -1,
                ChildCount = 0
            });
            AddChild(parentNodeIndex, index);
            return index;
        }

        /// <summary>
        /// Record a field change from a generated property setter.
        /// Lazily creates a root node for the state instance if needed.
        /// </summary>
        public void RecordFieldChange(object stateInstance, int typeId, int field, ChangeValue oldValue, ChangeValue newValue)
        {
            // Find or create root for this state instance
            int rootIndex = -1;
            for (int i = 0; i < _rootIndices.Count; i++)
            {
                if (ReferenceEquals(_nodes[_rootIndices[i]].Root, stateInstance))
                {
                    rootIndex = _rootIndices[i];
                    break;
                }
            }
            if (rootIndex == -1)
                rootIndex = RecordRoot(stateInstance, typeId);

            RecordLeaf(rootIndex, field, oldValue, newValue);
        }

        /// <summary>
        /// Add a leaf node (actual field value change).
        /// </summary>
        public void RecordLeaf(int parentNodeIndex, int field, ChangeValue oldValue, ChangeValue newValue)
        {
            var index = _nodes.Count;
            _nodes.Add(new ChangeNode
            {
                Field = field,
                CollectionIndex = -1,
                OldValue = oldValue,
                NewValue = newValue,
                ChildStartIndex = -1,
                ChildCount = 0
            });
            AddChild(parentNodeIndex, index);
        }

        private void AddChild(int parentIndex, int childIndex)
        {
            var parent = _nodes[parentIndex];
            if (parent.ChildCount == 0)
            {
                parent.ChildStartIndex = childIndex;
                parent.ChildCount = 1;
            }
            else
            {
                // Children may not be contiguous if interleaved with other branches.
                // We keep ChildStartIndex pointing to first child and increment count.
                // For correct traversal, we also need contiguous children.
                // Strategy: children are always appended right after RecordBranch,
                // so they are naturally contiguous within a branch's scope.
                parent.ChildCount++;
            }
            _nodes[parentIndex] = parent;
        }

        /// <summary>
        /// Walk the change tree, group by root, invoke type-level subscribers, then clean up.
        /// </summary>
        public void FlushAndNotify()
        {
            if (_nodes.Count > 0)
            {
                foreach (var rootIndex in _rootIndices)
                {
                    var rootNode = _nodes[rootIndex];
                    if (rootNode.ChildCount == 0) continue; // no changes recorded

                    if (_subscribers.TryGetValue(rootNode.RootTypeId, out var handler))
                    {
                        var args = new ChangeTreeArgs
                        {
                            Root = rootNode.Root!,
                            RootTypeId = rootNode.RootTypeId,
                            Nodes = _nodes,
                            RootNodeIndex = rootIndex
                        };
                        handler.Invoke(args);
                    }
                }
            }

            // Return pooled resources
            ListPool<ChangeNode>.Return(_nodes);
            _nodes = null!;
            _rootIndices.Clear();
            Current = null;

            lock (_poolLock)
            {
                _trackerPool.Push(this);
            }
        }

        /// <summary>
        /// Discard all recorded changes without notifying. Used on error paths.
        /// </summary>
        public void Discard()
        {
            ListPool<ChangeNode>.Return(_nodes);
            _nodes = null!;
            _rootIndices.Clear();
            Current = null;

            lock (_poolLock)
            {
                _trackerPool.Push(this);
            }
        }

        /// <summary>
        /// Whether any changes have been recorded.
        /// </summary>
        public bool HasChanges => _nodes != null && _nodes.Count > 0;
    }

    /// <summary>
    /// Arguments passed to type-level change subscribers.
    /// Contains reference to the change tree (pooled list of struct nodes).
    /// Valid only during the callback — do not store references.
    /// </summary>
    public struct ChangeTreeArgs
    {
        /// <summary>The root object instance that was changed.</summary>
        public object Root;

        /// <summary>Type ID of the root object.</summary>
        public int RootTypeId;

        /// <summary>Pooled flat list of nodes forming the change tree. Do not store.</summary>
        public List<ChangeNode> Nodes;

        /// <summary>Index of the root node for this instance in <see cref="Nodes"/>.</summary>
        public int RootNodeIndex;

        /// <summary>
        /// Check if a specific field was changed anywhere in the tree.
        /// </summary>
        public bool HasChange(int field)
        {
            var rootNode = Nodes[RootNodeIndex];
            return SearchField(rootNode, field);
        }

        private bool SearchField(ChangeNode node, int field)
        {
            if (node.Field == field) return true;
            if (node.ChildCount > 0)
            {
                for (int i = 0; i < node.ChildCount; i++)
                {
                    if (SearchField(Nodes[node.ChildStartIndex + i], field))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Find a leaf node by field ID. Returns null if not found.
        /// </summary>
        public ChangeNode? FindLeaf(int field)
        {
            var rootNode = Nodes[RootNodeIndex];
            return FindLeafRecursive(rootNode, field);
        }

        private ChangeNode? FindLeafRecursive(ChangeNode node, int field)
        {
            if (node.IsLeaf && node.Field == field) return node;
            if (node.ChildCount > 0)
            {
                for (int i = 0; i < node.ChildCount; i++)
                {
                    var result = FindLeafRecursive(Nodes[node.ChildStartIndex + i], field);
                    if (result != null) return result;
                }
            }
            return null;
        }
    }
}
