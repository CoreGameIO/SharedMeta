#if !USE_MEMORYPACK
// Stub attributes when MemoryPack package is not referenced.
// Allows code to compile without #if guards around [MemoryPackable] / [MemoryPackOrder].
// To enable real MemoryPack serialization, remove DisableMemoryPack property.

namespace MemoryPack
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, Inherited = false)]
    internal sealed class MemoryPackableAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
    internal sealed class MemoryPackOrderAttribute : System.Attribute
    {
        public MemoryPackOrderAttribute(int order) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Constructor)]
    internal sealed class MemoryPackConstructorAttribute : System.Attribute { }
}
#endif
