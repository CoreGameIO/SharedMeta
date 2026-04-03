// Stub attributes for Unity/netstandard2.1 builds.
// On server (net10.0), the real packages provide these.

using System;

namespace Orleans
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
    public sealed class GenerateSerializerAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class IdAttribute : Attribute
    {
        public uint Id { get; }
        public IdAttribute(uint id) => Id = id;
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
