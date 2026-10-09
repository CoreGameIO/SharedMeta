using System;

namespace SharedMeta.Client
{
    /// <summary>
    /// Per-entity container holding the current state plus a mutation counter, owned by
    /// <c>MetaServiceResolver.EntityConnection</c> and shared across every API client
    /// subscribed to the same entity. Centralizing ownership here means:
    /// <list type="bullet">
    ///   <item>All API clients on the same entity see the same state object — even after
    ///   wholesale replacement (ServerReplace), where the previous design left non-receiving
    ///   clients pointing at a stale instance.</item>
    ///   <item><see cref="MutationCount"/> is shared — bumped exactly once per broadcast
    ///   that touches the state, regardless of which (or how many) API clients exist.</item>
    ///   <item>Foreign-service broadcasts (a method on a different service that targets the
    ///   same state) update the state and bump the counter, so a client that subscribed to
    ///   only one of several services on the entity still observes every state change.</item>
    /// </list>
    /// </summary>
    public sealed class EntityStateContainer<TState> : IEntityStateContainer where TState : class
    {
        private TState _state;

        /// <summary>Current state. Replaced via <see cref="Replace"/> on wholesale updates;
        /// mutated in place by service methods, ServerPatch appliers, and replay paths.</summary>
        public TState State => _state;

        /// <summary>Local-only counter incremented on every state mutation. Not persisted,
        /// not synchronized across clients, not tied to the network sequence number — just
        /// a polling signal that says "did anything modify this entity since I last looked?".</summary>
        public int MutationCount { get; private set; }

        /// <summary>Fired after every mutation. API clients subscribe to this to surface their
        /// own <c>OnStateMutated</c> event without having to filter broadcasts themselves.</summary>
        public event Action? OnMutated;

        public EntityStateContainer(TState initial)
        {
            _state = initial ?? throw new ArgumentNullException(nameof(initial));
        }

        /// <summary>Wholesale state replacement (ServerReplace path, reconnect refresh).
        /// Bumps <see cref="MutationCount"/> and fires <see cref="OnMutated"/>.</summary>
        public void Replace(TState newState)
        {
            if (newState == null) throw new ArgumentNullException(nameof(newState));
            _state = newState;
            MutationCount++;
            OnMutated?.Invoke();
        }

        /// <summary>Signals that the state object was mutated in place (ServerPatch applier,
        /// method replay, optimistic local execution). Bumps the counter and fires the event.</summary>
        public void NotifyMutated()
        {
            MutationCount++;
            OnMutated?.Invoke();
        }

        // Non-generic surface — see IEntityStateContainer.
        object IEntityStateContainer.State => _state;
        void IEntityStateContainer.ReplaceObject(object newState) => Replace((TState)newState);
    }

    /// <summary>
    /// Non-generic surface so the resolver can drive the container without knowing
    /// <c>TState</c> at runtime. Keeps <see cref="EntityStateContainer{TState}"/> the
    /// only place where typed state lives.
    /// </summary>
    public interface IEntityStateContainer
    {
        object State { get; }
        int MutationCount { get; }
        event Action OnMutated;
        void NotifyMutated();
        void ReplaceObject(object newState);
    }
}
