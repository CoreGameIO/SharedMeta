using System;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Pluggable structural comparison for method return types in the Optimistic
    /// desync-detection path. When a comparer for type <typeparamref name="T"/> is
    /// discovered at compile time by the SharedMeta source generator, the generated
    /// ApiClient calls <see cref="AreEqual"/> instead of comparing serialized bytes.
    ///
    /// Use this when bytewise comparison gives false desyncs because the serializer
    /// is not stable for the type's value (e.g. <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// whose enumeration order depends on insertion sequence, or types containing
    /// floating-point fields where -0/+0 or NaN equality matters).
    ///
    /// Implementations must be deterministic and thread-safe — the generated
    /// continuation calls them from the threadpool.
    /// </summary>
    /// <typeparam name="T">The method return type to compare.</typeparam>
    public interface IMetaResultComparer<in T>
    {
        /// <summary>
        /// Return true when the server-authoritative result is equivalent to the
        /// locally-computed result for the purpose of desync detection.
        /// </summary>
        bool AreEqual(T server, T local);
    }

    /// <summary>
    /// Marks a class implementing <see cref="IMetaResultComparer{T}"/> for the
    /// SharedMeta source generator. The attribute is optional — any class implementing
    /// the interface is auto-discovered. Use it to opt out (<see cref="NoAutoRegister"/>)
    /// or to disambiguate when multiple comparers exist for the same type
    /// (<see cref="Priority"/>).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ResultComparerAttribute : Attribute
    {
        /// <summary>
        /// If true, the generator will not pick up this comparer. The byte-level
        /// fallback is used for the corresponding return type.
        /// </summary>
        public bool NoAutoRegister { get; set; }

        /// <summary>
        /// When two or more comparers exist for the same type, the one with the
        /// highest <see cref="Priority"/> wins. Ties produce a build-time error.
        /// Default is 0.
        /// </summary>
        public int Priority { get; set; }
    }
}
