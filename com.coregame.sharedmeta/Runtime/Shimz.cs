#if !HAS_MEMORYPACK

namespace MemoryPack
{
    using System;

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public class MemoryPackableAttribute : Attribute { }

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