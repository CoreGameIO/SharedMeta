// Polyfills for C# language features not available in netstandard2.1 BCL.

#if !NET5_0_OR_GREATER
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
