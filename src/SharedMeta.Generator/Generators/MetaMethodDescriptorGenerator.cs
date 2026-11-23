using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using SharedMeta.Generator.Utilities;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// Emits <c>GeneratedMetaMethodDescriptors</c> — the server's method table with argument and
    /// result codecs — for diagnostics tooling (call taps, inspectors). Generated in the server
    /// project only when a call tap is referenced.
    /// </summary>
    /// <remarks>
    /// The codecs speak the wire form, never the declared one: an argument with a transformer is
    /// decoded to its boxed type, because unboxing needs the entity state and config of a live call,
    /// which tooling reading a recorded payload does not have. Framing follows the serializer of the
    /// assembly declaring the service — the one whose dispatcher reads these bytes — exactly as the
    /// generated server API does when it packs arguments.
    /// </remarks>
    internal static class MetaMethodDescriptorGenerator
    {
        public const string ClassName = "GeneratedMetaMethodDescriptors";

        private sealed class MethodEntry
        {
            public string IdConst = "";
            public string ServiceFqn = "";
            public string ServiceName = "";
            public string Alias = "";
            public int Version;
            public string MethodName = "";
            public string StateFqn = "";
            public DetectedSerializer Serializer;
            public List<(string Name, string Declared, string Wire, bool Transformed)> Params = new();
            public string? ResultFqn;
        }

        /// <summary>
        /// Always returns a class, even with no dispatchable method: ConfigureMeta registers it
        /// whenever the tap is on, so a missing file would break the build.
        /// </summary>
        public static string Generate(string rootNamespace, IEnumerable<ServiceImplInfo> serviceImpls, Compilation compilation)
        {
            var entries = new List<MethodEntry>();
            var seenIds = new HashSet<string>();
            var stateTypes = new List<string>();
            var seenServices = new HashSet<string>();

            foreach (var impl in serviceImpls)
            {
                if (impl.HasInvalidServerMetaServiceCombo) continue;
                if (!seenServices.Add(impl.InterfaceFullName)) continue;

                var service = compilation.GetTypeByMetadataName(impl.InterfaceFullName);
                if (service == null) continue;

                var stateSymbol = compilation.GetTypeByMetadataName(impl.StateTypeFullName);
                var stateFqn = stateSymbol != null ? Fqn(stateSymbol) : "global::" + impl.StateTypeFullName;
                if (!stateTypes.Contains(stateFqn)) stateTypes.Add(stateFqn);

                var serializer = SerializerDetector.DetectForAssembly(service.ContainingAssembly, compilation);
                var ns = service.ContainingNamespace.ToDisplayString();

                var methods = service.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary)
                    .ToList();
                methods.AddRange(ImplDeclaredMethods.SymbolsForService(service, compilation));

                foreach (var method in methods)
                {
                    // LocalQuery runs on the client only; the server has no dispatch case for it.
                    if (ServerApiGenerator.IsMode(method, "LocalQuery")) continue;

                    var (alias, version) = ServerApiGenerator.ReadAliasAndVersion(method);
                    var idConst = "global::" + ns + ".Generated.GameMethodIds."
                        + SignatureHashGenerator.MakeMethodIdConstName(service.Name, alias, version);
                    if (!seenIds.Add(idConst)) continue;

                    var entry = new MethodEntry
                    {
                        IdConst = idConst,
                        ServiceFqn = Fqn(service),
                        ServiceName = service.Name,
                        Alias = alias,
                        Version = version,
                        MethodName = method.Name,
                        StateFqn = stateFqn,
                        Serializer = serializer,
                        ResultFqn = UnwrapResult(method.ReturnType) is { } r ? Fqn(r) : null,
                    };

                    var transforms = TransformerAnalysis.Analyze(method.Parameters, compilation);
                    for (int i = 0; i < method.Parameters.Length; i++)
                    {
                        var p = method.Parameters[i];
                        var t = transforms[i];
                        var wire = t.Transformed ? t.SimpleType! : Fqn(p.Type);
                        entry.Params.Add((p.Name, p.Type.ToDisplayString(), wire, t.Transformed));
                    }
                    entries.Add(entry);
                }
            }

            bool anyMemoryPack = entries.Any(e => e.Serializer == DetectedSerializer.MemoryPack);

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Method table and argument codecs for diagnostics tooling; emitted because a call tap is referenced.");
            sb.AppendLine("#nullable enable");
            sb.AppendLine("#pragma warning disable CS8600, CS8601, CS8603, CS8604, CS8625");
            sb.AppendLine("using System;");
            sb.AppendLine("using SharedMeta.Core;");
            sb.AppendLine("using SharedMeta.Server.Core.Diagnostics;");
            if (anyMemoryPack) sb.AppendLine("using MemoryPack;");
            sb.AppendLine();
            sb.AppendLine($"namespace {rootNamespace}.Server");
            sb.AppendLine("{");
            sb.AppendLine($"    public sealed class {ClassName} : IMetaMethodDescriptors");
            sb.AppendLine("    {");
            sb.AppendLine($"        public static readonly {ClassName} Instance = new {ClassName}();");
            sb.AppendLine();
            sb.AppendLine("        private static readonly Type[] s_stateTypes =");
            sb.AppendLine("        {");
            foreach (var st in stateTypes)
                sb.AppendLine($"            typeof({st}),");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        private readonly MetaMethodDescriptor[] _methods;");
            sb.AppendLine("        private readonly System.Collections.Generic.Dictionary<ushort, MetaMethodDescriptor> _byId;");
            sb.AppendLine();
            sb.AppendLine($"        private {ClassName}()");
            sb.AppendLine("        {");
            sb.AppendLine("            _methods = new MetaMethodDescriptor[]");
            sb.AppendLine("            {");
            foreach (var e in entries)
            {
                var parameters = e.Params.Count == 0
                    ? "Array.Empty<MetaParameterDescriptor>()"
                    : "new MetaParameterDescriptor[] { " + string.Join(", ", e.Params.Select(p =>
                        $"new MetaParameterDescriptor({Lit(p.Name)}, {Lit(p.Declared)}, typeof({p.Wire}), {(p.Transformed ? "true" : "false")})")) + " }";
                var result = e.ResultFqn != null ? $"typeof({e.ResultFqn})" : "null";
                sb.AppendLine($"                new MetaMethodDescriptor({e.IdConst}, typeof({e.ServiceFqn}), {Lit(e.ServiceName)}, {Lit(e.Alias)}, {e.Version},");
                sb.AppendLine($"                    {Lit(e.MethodName)}, typeof({e.StateFqn}), {parameters}, {result}),");
            }
            sb.AppendLine("            };");
            sb.AppendLine("            _byId = new System.Collections.Generic.Dictionary<ushort, MetaMethodDescriptor>(_methods.Length);");
            sb.AppendLine("            foreach (var m in _methods) _byId[m.MethodId] = m;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public System.Collections.Generic.IReadOnlyList<MetaMethodDescriptor> Methods => _methods;");
            sb.AppendLine();
            sb.AppendLine("        public MetaMethodDescriptor? Get(ushort methodId) => _byId.TryGetValue(methodId, out var d) ? d : null;");
            sb.AppendLine();
            sb.AppendLine("        public System.Collections.Generic.IReadOnlyList<Type> StateTypes => s_stateTypes;");
            sb.AppendLine();
            sb.AppendLine("        public Type? GetStateType(string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var t in s_stateTypes)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (t.Name == name || t.FullName == name || t.FullName?.Replace('+', '.') == name) return t;");
            sb.AppendLine("            }");
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");
            sb.AppendLine();

            EmitDecodeArgs(sb, entries);
            EmitEncodeArgs(sb, entries);
            EmitDecodeResult(sb, entries);

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitDecodeArgs(StringBuilder sb, List<MethodEntry> entries)
        {
            sb.AppendLine("        public object?[] DecodeArgs(ushort methodId, System.ReadOnlyMemory<byte> payload, IMetaSerializer serializer)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (methodId)");
            sb.AppendLine("            {");
            foreach (var e in entries)
            {
                sb.AppendLine($"                case {e.IdConst}:");
                sb.AppendLine("                {");
                var n = e.Params.Count;
                if (n == 0)
                {
                    sb.AppendLine("                    return Array.Empty<object?>();");
                }
                else if (e.Serializer == DetectedSerializer.MemoryPack && n == 1)
                {
                    sb.AppendLine($"                    return new object?[] {{ MemoryPackSerializer.Deserialize<{e.Params[0].Wire}>(payload.Span) }};");
                }
                else if (e.Serializer == DetectedSerializer.MemoryPack)
                {
                    sb.AppendLine("                    var __state = MemoryPackReaderOptionalStatePool.Rent(null);");
                    sb.AppendLine("                    var __reader = new MemoryPackReader(payload.Span, __state);");
                    sb.AppendLine("                    try");
                    sb.AppendLine("                    {");
                    for (int i = 0; i < n; i++)
                        sb.AppendLine($"                        var __a{i} = __reader.ReadValue<{e.Params[i].Wire}>();");
                    sb.AppendLine($"                        return new object?[] {{ {string.Join(", ", Enumerable.Range(0, n).Select(i => $"__a{i}"))} }};");
                    sb.AppendLine("                    }");
                    sb.AppendLine("                    finally");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        __reader.Dispose();");
                    sb.AppendLine("                    }");
                }
                else
                {
                    sb.AppendLine("                    using var __reader = serializer.CreateReader(payload);");
                    for (int i = 0; i < n; i++)
                        sb.AppendLine($"                    var __a{i} = __reader.Read<{e.Params[i].Wire}>();");
                    sb.AppendLine($"                    return new object?[] {{ {string.Join(", ", Enumerable.Range(0, n).Select(i => $"__a{i}"))} }};");
                }
                sb.AppendLine("                }");
            }
            sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(methodId), methodId, \"Unknown server method id\");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void EmitEncodeArgs(StringBuilder sb, List<MethodEntry> entries)
        {
            sb.AppendLine("        public byte[] EncodeArgs(ushort methodId, object?[] args, IMetaSerializer serializer)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (methodId)");
            sb.AppendLine("            {");
            foreach (var e in entries)
            {
                var n = e.Params.Count;
                sb.AppendLine($"                case {e.IdConst}:");
                sb.AppendLine("                {");
                sb.AppendLine($"                    if (args.Length != {n}) throw new ArgumentException($\"{e.ServiceName}.{e.Alias} takes {n} argument(s), got {{args.Length}}\", nameof(args));");
                if (n == 0)
                {
                    sb.AppendLine("                    return Array.Empty<byte>();");
                }
                else if (e.Serializer == DetectedSerializer.MemoryPack && n == 1)
                {
                    sb.AppendLine($"                    return MemoryPackSerializer.Serialize<{e.Params[0].Wire}>(({e.Params[0].Wire})args[0]!);");
                }
                else if (e.Serializer == DetectedSerializer.MemoryPack)
                {
                    sb.AppendLine("                    var __buffer = new System.Buffers.ArrayBufferWriter<byte>();");
                    for (int i = 0; i < n; i++)
                        sb.AppendLine($"                    MemoryPackSerializer.Serialize(__buffer, ({e.Params[i].Wire})args[{i}]!);");
                    sb.AppendLine("                    return __buffer.WrittenSpan.ToArray();");
                }
                else
                {
                    sb.AppendLine("                    using var __writer = serializer.CreateWriter();");
                    for (int i = 0; i < n; i++)
                        sb.AppendLine($"                    __writer.Write<{e.Params[i].Wire}>(({e.Params[i].Wire})args[{i}]!);");
                    // Copied: the writer's buffer goes back to its pool on dispose.
                    sb.AppendLine("                    return __writer.Complete().ToArray();");
                }
                sb.AppendLine("                }");
            }
            sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(methodId), methodId, \"Unknown server method id\");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        private static void EmitDecodeResult(StringBuilder sb, List<MethodEntry> entries)
        {
            sb.AppendLine("        public object? DecodeResult(ushort methodId, System.ReadOnlyMemory<byte> resultBytes, IMetaSerializer serializer)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (resultBytes.IsEmpty) return null;");
            sb.AppendLine("            switch (methodId)");
            sb.AppendLine("            {");
            foreach (var e in entries)
            {
                sb.Append($"                case {e.IdConst}: ");
                if (e.ResultFqn == null)
                    sb.AppendLine("return null;");
                else if (e.Serializer == DetectedSerializer.MemoryPack)
                    sb.AppendLine($"return MemoryPackSerializer.Deserialize<{e.ResultFqn}>(resultBytes.Span);");
                else
                    sb.AppendLine($"return serializer.Unpack<{e.ResultFqn}>(resultBytes);");
            }
            sb.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(methodId), methodId, \"Unknown server method id\");");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }

        private static ITypeSymbol? UnwrapResult(ITypeSymbol returnType)
        {
            if (returnType.SpecialType == SpecialType.System_Void) return null;
            if (returnType is INamedTypeSymbol named
                && (named.Name == "Task" || named.Name == "ValueTask")
                && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
            {
                return named.IsGenericType ? named.TypeArguments[0] : null;
            }
            return returnType;
        }

        // typeof / cast-safe: fully qualified, no nullable-reference annotations.
        private static string Fqn(ITypeSymbol type)
            => type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        private static string Lit(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
