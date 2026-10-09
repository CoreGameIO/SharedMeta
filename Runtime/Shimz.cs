#if !HAS_MEMORYPACK

namespace MemoryPack
{
    using System;

    // MemoryPack's "what kind of formatter to generate" enum. Stubbed so
    // [MemoryPackable(GenerateType.X)] compiles in Unity builds without the
    // real MemoryPack package.
    public enum GenerateType
    {
        Object = 0,
        VersionTolerant = 1,
        CircularReference = 2,
        Collection = 3,
        NoGenerate = 4,
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public class MemoryPackableAttribute : Attribute
    {
        public MemoryPackableAttribute() { }
        public MemoryPackableAttribute(GenerateType generateType) { }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MemoryPackOrderAttribute : Attribute 
    {
        public MemoryPackOrderAttribute(int order) { }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MemoryPackIgnoreAttribute : Attribute
    {
        public MemoryPackIgnoreAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Constructor)]
    public class MemoryPackConstructorAttribute : Attribute
    {
        public MemoryPackConstructorAttribute() { }
    }

    // 0.26.6+ Marker attribute used on MemoryPack-serializable ReadOnlyMemory<byte>
    // properties (e.g. PayloadDebug.DesyncStateBytes) so MemoryPack treats the
    // field as a raw byte-slice slot. Stubbed here for Unity builds without the
    // real MemoryPack package — runtime serialization falls back to the host
    // serializer (MessagePack via Orleans, or a Unity-side codec).
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class MemoryPackAllowSerializeAttribute : Attribute
    {
        public MemoryPackAllowSerializeAttribute() { }
    }

    public static class MemoryPackSerializer
    {
        public static byte[] Serialize<T>(T obj)
        {
            throw new Exception("MemoryPack serialization is not configured");
        }

        public static byte[] Serialize(Type type, object value)
        {
            throw new Exception("MemoryPack serialization is not configured");
        }

        public static T Deserialize<T>(byte[] bytes)
        {
            throw new Exception("MemoryPack serialization is not configured");
        }

        public static object Deserialize(Type type, byte[] bytes)
        {
            throw new Exception("MemoryPack serialization is not configured");
        }

    }
}

#endif