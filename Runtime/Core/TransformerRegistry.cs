using System;
using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Per-type singleton holder for argument transformers. Generated box/unbox call sites go
    /// through here, so the transformation a parameter gets is fixed at compile time and costs
    /// one static field read — no reflection, no registry lookup, and nothing that can differ
    /// between client and server at runtime.
    /// </summary>
    /// <remarks>
    /// Transformers must be stateless: one instance is shared for the process lifetime and is
    /// called concurrently from every grain and every client call.
    /// </remarks>
    /// <typeparam name="T">Transformer type. Needs a public parameterless constructor.</typeparam>
    public static class MetaTransformer<T> where T : new()
    {
        /// <summary>The shared instance.</summary>
        public static readonly T Instance = new T();
    }
}
