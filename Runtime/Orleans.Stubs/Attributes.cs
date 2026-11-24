// Stub attributes for Unity/netstandard2.1 builds.
// On server (net10.0), the real packages provide these.

using System;

namespace Orleans
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
    public sealed class GenerateSerializerAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.ReturnValue, Inherited = false)]
    public sealed class ImmutableAttribute : Attribute
    { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class IdAttribute : Attribute
    {
        public uint Id { get; }
        public IdAttribute(uint id) => Id = id;
    }
}

namespace Orleans.Concurrency
{
    /// <summary>
    /// Stub for Orleans's <c>Immutable&lt;T&gt;</c> wrapper. The real Orleans framework uses
    /// this marker to skip defensive deep-copies on in-silo grain hops (sharing the inner value
    /// by reference). The stub is a thin wrapper that exposes <see cref="Value"/> — Unity code
    /// reads it the same way the runtime does, just without the copier optimization.
    /// </summary>
    public readonly struct Immutable<T>
    {
        public T Value { get; }
        public Immutable(T value) { Value = value; }
    }
}

// MessagePack stubs — excluded when real MessagePack package is installed (HAS_MESSAGEPACK).
#if !HAS_MESSAGEPACK
namespace MessagePack
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class MessagePackObjectAttribute : Attribute
    {
        public bool KeyAsPropertyName { get; set; }
        public MessagePackObjectAttribute() { }
        public MessagePackObjectAttribute(bool keyAsPropertyName) { KeyAsPropertyName = keyAsPropertyName; }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class KeyAttribute : Attribute
    {
        public int IntKey { get; }
        public KeyAttribute(int x) => IntKey = x;
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class IgnoreMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class SerializationConstructorAttribute : Attribute { }
}
#endif
