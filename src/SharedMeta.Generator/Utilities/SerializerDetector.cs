using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Utilities
{
    /// <summary>
    /// Detected serializer type for code generation.
    /// </summary>
    public enum DetectedSerializer
    {
        /// <summary>Use generic IPayloadWriter interface.</summary>
        Generic,

        /// <summary>Use MemoryPack ref struct writers.</summary>
        MemoryPack
    }

    /// <summary>
    /// Detects which serializer to use for code generation.
    ///
    /// Detection priority:
    /// 1. [assembly: MetaSerializer(SerializerType.X)] - explicit attribute takes priority
    /// 2. If attribute is SerializerType.Auto or not present:
    ///    - Check for MemoryPack in referenced assemblies
    ///    - If found: use MemoryPack
    ///    - Otherwise: use Generic
    /// </summary>
    public static class SerializerDetector
    {
        private const string MetaSerializerAttributeName = "SharedMeta.Core.MetaSerializerAttribute";
        // The NuGet package "MemoryPack" produces the runtime DLL "MemoryPack.Core" — that's
        // the assembly identity the compilation references. The legacy "MemoryPack" check
        // missed every consumer, silently falling through to Generic IPayloadWriter path
        // (which costs one IPayloadWriter instance + one byte[] per cross-entity call).
        private const string MemoryPackAssemblyName = "MemoryPack.Core";

        /// <summary>
        /// Detect which serializer to use based on assembly attributes and references.
        /// </summary>
        /// <param name="compilation">The compilation context.</param>
        /// <returns>The detected serializer type.</returns>
        public static DetectedSerializer Detect(Compilation compilation)
        {
            // 1. Check for [assembly: MetaSerializer(...)] attribute
            var metaSerializerAttr = compilation.Assembly
                .GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == MetaSerializerAttributeName);

            if (metaSerializerAttr != null &&
                metaSerializerAttr.ConstructorArguments.Length > 0 &&
                metaSerializerAttr.ConstructorArguments[0].Value is int typeValue)
            {
                // SerializerType enum values: Auto=0, MemoryPack=1, Generic=2
                switch (typeValue)
                {
                    case 1: // MemoryPack
                        return DetectedSerializer.MemoryPack;
                    case 2: // Generic
                        return DetectedSerializer.Generic;
                    // case 0 (Auto) falls through to auto-detection
                }
            }

            // 2. Auto-detect: check for MemoryPack in referenced assemblies
            var hasMemoryPack = compilation.ReferencedAssemblyNames
                .Any(a => a.Name == MemoryPackAssemblyName);

            return hasMemoryPack ? DetectedSerializer.MemoryPack : DetectedSerializer.Generic;
        }

        /// <summary>
        /// Check if MemoryPack is available in the compilation.
        /// </summary>
        public static bool HasMemoryPack(Compilation compilation)
        {
            return compilation.ReferencedAssemblyNames
                .Any(a => a.Name == MemoryPackAssemblyName);
        }
    }
}
