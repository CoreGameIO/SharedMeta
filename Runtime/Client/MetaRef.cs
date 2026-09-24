using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using SharedMeta.Core;

namespace SharedMeta.Client
{
    /// <summary>
    /// A long-lived handle to one meta service on one entity, safe to store in a DI container for
    /// the process lifetime.
    /// <para>
    /// It holds the lookup key, never the client. A supersede restart or a Disconnect disposes
    /// every <c>ApiClient</c> under the connection and drops it from the resolver, so a field
    /// caching the client would be left pointing at a dead object with its broadcast subscription
    /// already torn off. Re-resolving on each access costs a lock plus two dictionary lookups —
    /// the documented allocation-free <c>TryGetService</c> path — and makes staleness structurally
    /// impossible rather than something a listener has to repair in time.
    /// </para>
    /// <para>
    /// The entity id is re-read on every access too, so a handle bound to a client's PlayerId
    /// follows that player across a relogin instead of pinning the id captured at construction.
    /// </para>
    /// </summary>
    /// <typeparam name="TApiClient">The generated API client type, e.g. ProfileServiceApiClient.</typeparam>
    public sealed class MetaRef<TApiClient> where TApiClient : class
    {
        private readonly Func<IMetaServiceResolver?> _resolver;
        private readonly Func<string?> _entityId;

        /// <summary>
        /// Binds to an entity id that can change over the handle's lifetime — pass
        /// <c>() =&gt; client.PlayerId</c> for a UserOwned service.
        /// </summary>
        public MetaRef(IMetaServiceResolver resolver, Func<string?> entityId)
            : this(ConstantResolver(resolver), entityId)
        {
        }

        /// <summary>Binds to a fixed entity id.</summary>
        public MetaRef(IMetaServiceResolver resolver, string entityId)
            : this(ConstantResolver(resolver), () => entityId)
        {
        }

        /// <summary>
        /// Binds to a resolver that doesn't exist yet, or gets replaced during the session.
        /// <para>
        /// A host commonly creates its <c>MetaClient</c> inside an async connect and drops it again
        /// on teardown, so a composition root running before that has no resolver to hand over, and
        /// a handle built from the first client would pin it after a re-bootstrap. Re-reading the
        /// resolver on each access makes the handle outlive the client the same way it already
        /// outlives a single connection.
        /// </para>
        /// </summary>
        public MetaRef(Func<IMetaServiceResolver?> resolver, Func<string?> entityId)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _entityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        }

        // Allocated once per handle, not per access.
        private static Func<IMetaServiceResolver?> ConstantResolver(IMetaServiceResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            return () => resolver;
        }

        /// <summary>
        /// The entity this handle currently points at. Null before the id is known — a UserOwned
        /// handle built before login has no player yet.
        /// </summary>
        public string? EntityId => _entityId();

        /// <summary>
        /// The live client, or null when the entity isn't subscribed yet or its connection was
        /// torn down. Never throws, never subscribes, never allocates.
        /// </summary>
        public TApiClient? Current => TryGet(out var api) ? api : null;

        /// <summary>True when <see cref="Current"/> would hand back a client.</summary>
        public bool IsAlive => TryGet(out _);

        /// <summary>
        /// Non-allocating attempt to reach the live client. False when the id isn't known yet,
        /// the entity isn't subscribed, or the client hasn't been created by a prior
        /// <see cref="GetAsync"/>.
        /// </summary>
        public bool TryGet([NotNullWhen(true)] out TApiClient? api)
        {
            var resolver = _resolver();
            var id = _entityId();
            // No resolver yet (meta not connected) and an empty id (not logged in) are both the
            // ordinary pre-connect state, not caller bugs — and the resolver's connection store
            // would throw on a null key, while this accessor promises never to throw.
            if (resolver == null || string.IsNullOrEmpty(id))
            {
                api = null;
                return false;
            }
            return resolver.TryGetService(id!, out api);
        }

        /// <summary>
        /// The live client, subscribing to the entity on first use. Completes synchronously and
        /// without allocating once the entity is subscribed, so it is safe to call per frame
        /// rather than caching the result in a field.
        /// </summary>
        public ValueTask<TApiClient> GetAsync()
        {
            var resolver = _resolver()
                ?? throw new InvalidOperationException(
                    $"MetaRef<{typeof(TApiClient).Name}> has no meta client yet — nothing to resolve against. " +
                    "Wait for the connect to finish, or poll Current/IsAlive instead.");

            var id = _entityId();
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException(
                    $"MetaRef<{typeof(TApiClient).Name}> has no entity id yet. A handle bound to PlayerId " +
                    "cannot resolve before login completes.");

            if (resolver.TryGetService<TApiClient>(id!, out var existing))
                return new ValueTask<TApiClient>(existing);

            return new ValueTask<TApiClient>(resolver.GetServiceAsync<TApiClient>(id!));
        }

        /// <inheritdoc cref="EntityId"/>
        public override string ToString() => $"MetaRef<{typeof(TApiClient).Name}>({_entityId() ?? "<no entity>"})";
    }

    /// <summary>
    /// Typed walk over a generated <c>MetaRefs</c> container. Each handle is passed to
    /// <see cref="Visit{TApiClient}(MetaRef{TApiClient})"/> with its API client type as a real
    /// generic argument, resolved at compile time — no <see cref="Type"/> key, no
    /// <see cref="object"/>, no <c>MakeGenericType</c>.
    /// <para>
    /// This is the way to register every service with a DI container: the visitor is written once
    /// against whatever the container's typed bind method is, and a service added later is visited
    /// without touching the composition root.
    /// </para>
    /// </summary>
    public interface IMetaRefVisitor
    {
        /// <summary>A service addressed by the player's own entity (UserOwned).</summary>
        void Visit<TApiClient>(MetaRef<TApiClient> handle) where TApiClient : class;

        /// <summary>A service whose entity id is only known at runtime.</summary>
        void Visit<TApiClient>(MetaRefFactory<TApiClient> factory) where TApiClient : class;
    }

    /// <summary>
    /// Hands out <see cref="MetaRef{TApiClient}"/> handles for a service whose entity id isn't
    /// known until runtime — a guild, a match, a lobby. Register the factory in a DI container
    /// once; call <see cref="For"/> when the id arrives.
    /// <para>
    /// <see cref="For"/> allocates a handle per call by design: caching them would need either an
    /// unbounded map keyed by every entity id the session ever saw, or an eviction rule tied to
    /// connection lifetime — and a handle deliberately outlives the connection it points at, so
    /// that rule would be wrong. Resolve the handle once and hold it, the way the DI registration
    /// for a UserOwned service does; don't call <c>For</c> inside a per-frame loop.
    /// </para>
    /// </summary>
    public sealed class MetaRefFactory<TApiClient> where TApiClient : class
    {
        private readonly Func<IMetaServiceResolver?> _resolver;

        public MetaRefFactory(IMetaServiceResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            _resolver = () => resolver;
        }

        /// <summary>
        /// Binds to a resolver that doesn't exist yet, or gets replaced during the session — see
        /// the matching <see cref="MetaRef{TApiClient}"/> constructor.
        /// </summary>
        public MetaRefFactory(Func<IMetaServiceResolver?> resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>A handle bound to <paramref name="entityId"/> for this service.</summary>
        public MetaRef<TApiClient> For(string entityId)
        {
            if (string.IsNullOrEmpty(entityId))
                throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
            return new MetaRef<TApiClient>(_resolver, () => entityId);
        }

        /// <summary>
        /// A handle whose entity id is re-read on every access — for an id that moves during the
        /// session, such as the match the player is currently in.
        /// </summary>
        public MetaRef<TApiClient> ForDynamic(Func<string?> entityId) => new MetaRef<TApiClient>(_resolver, entityId);
    }
}
