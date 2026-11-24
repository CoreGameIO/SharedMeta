using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Diagnostics;

/// <summary>
/// Marks an assembly that implements <see cref="IMetaCallTap"/>. When the server project
/// references such an assembly, the generator wraps the provider's dispatch entry points with
/// tap calls; without one the generated providers stay exactly as they are, so a host that does
/// not use a tap pays nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MetaCallTapProviderAttribute : Attribute
{
}

/// <summary>What kind of execution reached the provider's dispatch.</summary>
public enum MetaDispatchKind : byte
{
    /// <summary>Client RPC.</summary>
    Rpc,
    /// <summary>Call from another entity or from server code (ServerApi, timers).</summary>
    CrossEntity,
    /// <summary>Trigger run after the method that fired it, inside the same operation.</summary>
    Trigger,
    /// <summary>Self-targeted cross-entity call dispatched inside the outer call.</summary>
    Nested,
    /// <summary>Read-only query from a client or another entity.</summary>
    Query,
    /// <summary>Fire-and-forget signal.</summary>
    Signal,
    /// <summary>[MetaInit] / migration run.</summary>
    Init,
}

/// <summary>The provider as a tap sees it: identity, ordering and a state snapshot on demand.</summary>
public interface IMetaCallTapTarget
{
    string EntityId { get; }

    /// <summary>Full type name of the entity state.</summary>
    string StateTypeName { get; }

    /// <summary>Sequence number of the operation in progress, or of the last applied one between operations.</summary>
    long CurrentEntitySequence { get; }

    /// <summary>State packed with the provider's serializer into a fresh array the caller owns.</summary>
    byte[] GetStateBytes();

    /// <summary>Per-entity slot reserved for the tap's own bookkeeping; the provider never reads it.</summary>
    object? TapState { get; set; }
}

/// <summary>One dispatch entering the provider.</summary>
public readonly struct MetaTapCall
{
    public MetaTapCall(MetaDispatchKind kind, ushort methodId, ReadOnlyMemory<byte> payload,
        string? callerId, MetaCallContext? callContext, IMetaCallTapTarget target)
    {
        Kind = kind;
        MethodId = methodId;
        Payload = payload;
        CallerId = callerId;
        CallContext = callContext;
        Target = target;
    }

    public MetaDispatchKind Kind { get; }

    /// <summary>Server method id; 0 for <see cref="MetaDispatchKind.Init"/>.</summary>
    public ushort MethodId { get; }

    /// <summary>Argument bytes. May point into a pooled buffer — copy before keeping past the call.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    public string? CallerId { get; }

    /// <summary>Call context of the entity hop (trace, caller, method); null outside a hop.</summary>
    public MetaCallContext? CallContext { get; }

    public IMetaCallTapTarget Target { get; }
}

/// <summary>
/// Observes every execution the provider dispatches — RPC, cross-entity, trigger, nested call,
/// query, signal and init — with arguments, result and access to state.
/// <para>
/// Runs on the grain's turn, on the hot path: <see cref="IsEnabled"/> and
/// <see cref="ShouldCapture"/> must be cheap, and Begin/End must only copy what they keep and
/// hand it off. Result bytes are valid only during <c>End*</c>. Exceptions thrown by a tap are
/// swallowed and logged by the provider; they never fail the call.
/// </para>
/// </summary>
public interface IMetaCallTap
{
    bool IsEnabled { get; }

    bool ShouldCapture(MetaDispatchKind kind, ushort methodId, string entityId);

    /// <summary>
    /// Returns an opaque token handed back to the matching <c>End*</c>. Returning null declines the
    /// call after all (sampling, caller filters): no <c>End*</c> follows. A throwing Begin counts as null.
    /// </summary>
    object? Begin(in MetaTapCall call);

    void EndCall(object scope, ReadOnlyMemory<byte> resultBytes, Exception? error);

    void EndSignal(object scope, Exception? error);

    void EndInit(object scope, int fromVersion, int toVersion, Exception? error);
}
