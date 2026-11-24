using SharedMeta.Core;

namespace SharedMeta.Server.Core.Diagnostics;

/// <summary>One parameter of a meta method as it travels on the wire.</summary>
public sealed class MetaParameterDescriptor
{
    public MetaParameterDescriptor(string name, string declaredTypeName, Type wireType, bool isTransformed)
    {
        Name = name;
        DeclaredTypeName = declaredTypeName;
        WireType = wireType;
        IsTransformed = isTransformed;
    }

    public string Name { get; }

    /// <summary>Type the method body receives, as declared in source.</summary>
    public string DeclaredTypeName { get; }

    /// <summary>
    /// Type the argument is serialized as. Differs from the declared type for an argument with a
    /// transformer: decoding yields, and encoding expects, this boxed form.
    /// </summary>
    public Type WireType { get; }

    public bool IsTransformed { get; }
}

/// <summary>Static description of one server method id.</summary>
public sealed class MetaMethodDescriptor
{
    public MetaMethodDescriptor(ushort methodId, Type serviceInterface, string serviceName, string alias, int version,
        string methodName, Type stateType, MetaParameterDescriptor[] parameters, Type? resultType)
    {
        MethodId = methodId;
        ServiceInterface = serviceInterface;
        ServiceName = serviceName;
        Alias = alias;
        Version = version;
        MethodName = methodName;
        StateType = stateType;
        Parameters = parameters;
        ResultType = resultType;
    }

    /// <summary>Server method id (the index the provider dispatches on).</summary>
    public ushort MethodId { get; }
    public Type ServiceInterface { get; }

    /// <summary>Service name as the server signature records it.</summary>
    public string ServiceName { get; }
    public string Alias { get; }
    public int Version { get; }

    /// <summary>C# method name; differs from <see cref="Alias"/> when one is declared.</summary>
    public string MethodName { get; }
    public Type StateType { get; }
    public IReadOnlyList<MetaParameterDescriptor> Parameters { get; }

    /// <summary>Unwrapped return type; null for void / Task.</summary>
    public Type? ResultType { get; }
}

/// <summary>
/// Generated table of every server-dispatched meta method, with codecs that read and write their
/// arguments exactly as the dispatchers do. Registered by the generated <c>ConfigureMeta</c> only
/// when a call tap is referenced; meant for diagnostics tooling, not for the call path.
/// </summary>
public interface IMetaMethodDescriptors
{
    IReadOnlyList<MetaMethodDescriptor> Methods { get; }

    MetaMethodDescriptor? Get(ushort methodId);

    /// <summary>Every entity state type served by this host.</summary>
    IReadOnlyList<Type> StateTypes { get; }

    /// <summary>State type by full or short name; null when unknown.</summary>
    Type? GetStateType(string name);

    /// <summary>Arguments in wire form (see <see cref="MetaParameterDescriptor.WireType"/>).</summary>
    object?[] DecodeArgs(ushort methodId, ReadOnlyMemory<byte> payload, IMetaSerializer serializer);

    /// <summary>Inverse of <see cref="DecodeArgs"/>: wire-form arguments to a payload the dispatcher accepts.</summary>
    byte[] EncodeArgs(ushort methodId, object?[] args, IMetaSerializer serializer);

    /// <summary>Method result; null for void methods or empty bytes.</summary>
    object? DecodeResult(ushort methodId, ReadOnlyMemory<byte> resultBytes, IMetaSerializer serializer);
}
