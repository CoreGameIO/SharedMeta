using System.Text;
using Microsoft.CodeAnalysis;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Routes a generated provider's dispatch entry points through MetaProviderBase's tap helpers.
    /// Applied only when the server project references an assembly marked
    /// <c>[assembly: MetaCallTapProvider]</c>, so hosts without a tap get byte-identical output.
    /// </summary>
    internal static class CallTapEmitter
    {
        public const string MarkerAttribute = "SharedMeta.Server.Core.Diagnostics.MetaCallTapProviderAttribute";

        private const string DispatchCallSignature =
            "protected override System.Threading.Tasks.ValueTask<DispatchResult> DispatchCall(ushort methodId, System.ReadOnlyMemory<byte> payload)";
        private const string DispatchCallCoreSignature =
            "private System.Threading.Tasks.ValueTask<DispatchResult> DispatchCallCore(ushort methodId, System.ReadOnlyMemory<byte> payload)";

        private const string DispatchSignalSignature =
            "protected override async Task DispatchSignal(ushort methodId, System.ReadOnlyMemory<byte> payload)";
        private const string DispatchSignalCoreSignature =
            "private async Task DispatchSignalCore(ushort methodId, System.ReadOnlyMemory<byte> payload)";

        private const string RunInitSignature =
            "protected override async System.Threading.Tasks.Task<int> RunInitAsync(int currentVersion)";
        private const string RunInitCoreSignature =
            "private async System.Threading.Tasks.Task<int> RunInitCore(int currentVersion)";

        private const string Kind = "SharedMeta.Server.Core.Diagnostics.MetaDispatchKind";

        /// <summary>First and last line of the block added to each provider, so the tap's footprint is easy to find.</summary>
        public const string BlockStartLine = "        // ── Call tap ── routed through MetaProviderBase.Tap* helpers; the *Core methods are the";
        public const string BlockEndLine = "        // ── end call tap ──";

        /// <summary>
        /// True when any referenced assembly carries the marker. Every reference is checked by its
        /// attributes, not by name, so a tap may live in any assembly.
        /// </summary>
        public static bool IsTapReferenced(Compilation compilation)
        {
            // The server project itself may host the tap.
            foreach (var attribute in compilation.Assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == MarkerAttribute)
                    return true;
            }

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly) continue;
                foreach (var attribute in assembly.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString() == MarkerAttribute)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Rewrites the provider class emitted into <paramref name="sb"/> from <paramref name="start"/>:
        /// renames the dispatch bodies to *Core and adds tap-aware overrides in front of them.
        /// </summary>
        public static void Apply(StringBuilder sb, int start)
        {
            var text = sb.ToString(start, sb.Length - start);

            if (CountOf(text, DispatchCallSignature) != 1)
            {
                // The provider emitter changed shape; fail loudly rather than ship an untapped provider
                // that claims SupportsCallTap.
                sb.Insert(start, "#error SharedMeta call tap: generated provider has no single DispatchCall override to wrap." + NewLine);
                return;
            }

            bool hasSignal = text.Contains(DispatchSignalSignature);
            bool hasInit = text.Contains(RunInitSignature);

            // Same guard as DispatchCall: an override whose signature drifted from the one matched
            // here would silently stay unwrapped.
            if ((!hasSignal && HasOverride(text, "DispatchSignal(")) || (!hasInit && HasOverride(text, "RunInitAsync(")))
            {
                sb.Insert(start, "#error SharedMeta call tap: generated provider has a DispatchSignal/RunInitAsync override in an unexpected shape." + NewLine);
                return;
            }

            text = text.Replace(DispatchCallSignature, DispatchCallCoreSignature);
            if (hasSignal) text = text.Replace(DispatchSignalSignature, DispatchSignalCoreSignature);
            if (hasInit) text = text.Replace(RunInitSignature, RunInitCoreSignature);

            // Insert right after the class's opening brace.
            int classIdx = text.IndexOf(" : MetaProviderBase<", System.StringComparison.Ordinal);
            int braceIdx = classIdx < 0 ? -1 : text.IndexOf('{', classIdx);
            if (braceIdx < 0)
            {
                sb.Insert(start, "#error SharedMeta call tap: generated provider class header not found." + NewLine);
                return;
            }
            int insertAt = text.IndexOf('\n', braceIdx) + 1;

            var w = new StringBuilder();
            w.AppendLine(BlockStartLine);
            w.AppendLine("        // untouched dispatch bodies. Delegates are cached so a captured call allocates none.");
            w.AppendLine("        public override bool SupportsCallTap => true;");
            w.AppendLine();
            w.AppendLine("        private System.Func<ushort, System.ReadOnlyMemory<byte>, System.Threading.Tasks.ValueTask<DispatchResult>>? _tapDispatchCore;");
            w.AppendLine();
            w.AppendLine("        protected override System.Threading.Tasks.ValueTask<DispatchResult> DispatchCall(ushort methodId, System.ReadOnlyMemory<byte> payload)");
            w.AppendLine("            => ShouldTap(DispatchKind, methodId)");
            w.AppendLine("                ? TapDispatchCall(methodId, payload, _tapDispatchCore ??= DispatchCallCore)");
            w.AppendLine("                : DispatchCallCore(methodId, payload);");
            w.AppendLine();
            if (hasSignal)
            {
                w.AppendLine("        private System.Func<ushort, System.ReadOnlyMemory<byte>, System.Threading.Tasks.Task>? _tapSignalCore;");
                w.AppendLine();
                w.AppendLine("        protected override System.Threading.Tasks.Task DispatchSignal(ushort methodId, System.ReadOnlyMemory<byte> payload)");
                w.AppendLine($"            => ShouldTap({Kind}.Signal, methodId)");
                w.AppendLine("                ? TapDispatchSignal(methodId, payload, _tapSignalCore ??= DispatchSignalCore)");
                w.AppendLine("                : DispatchSignalCore(methodId, payload);");
                w.AppendLine();
            }
            if (hasInit)
            {
                w.AppendLine("        protected override System.Threading.Tasks.Task<int> RunInitAsync(int currentVersion)");
                w.AppendLine($"            => ShouldTap({Kind}.Init, 0)");
                w.AppendLine("                ? TapRunInit(currentVersion, RunInitCore)");
                w.AppendLine("                : RunInitCore(currentVersion);");
                w.AppendLine();
            }
            w.AppendLine(BlockEndLine);
            w.AppendLine();

            text = text.Insert(insertAt, w.ToString());
            sb.Remove(start, sb.Length - start);
            sb.Append(text);
        }

        private const string NewLine = "\n";

        private static bool HasOverride(string text, string member)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains(" override ") && line.Contains(member)) return true;
            }
            return false;
        }

        private static int CountOf(string text, string value)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(value, idx, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += value.Length;
            }
            return count;
        }
    }
}
