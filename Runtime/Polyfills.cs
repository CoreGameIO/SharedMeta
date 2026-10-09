// Polyfills for C# language features not available in netstandard2.1 BCL.

#if !NET5_0_OR_GREATER
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

// NotNullWhen ships in the netstandard2.1+ / net5+ BCL (those builds skip this via the guard).
// The polyfill exists only for Unity's ".NET Framework" API-compatibility level, whose BCL lacks
// it — without it, TryGetService's [NotNullWhen(true)] annotation would fail to compile there.
#if !NET5_0_OR_GREATER && !NETSTANDARD2_1 && !NETSTANDARD2_1_OR_GREATER
// ReSharper disable once CheckNamespace
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;
        public bool ReturnValue { get; }
    }
}
#endif
