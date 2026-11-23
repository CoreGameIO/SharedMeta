using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharedMeta.Generator;
using Xunit;

namespace SharedMeta.Generator.Tests;

/// <summary>
/// The call-tap codegen is opt-in by reference: without a <c>[MetaCallTapProvider]</c> assembly the
/// server project's output carries no trace of it, and with one the tap only adds code around the
/// unchanged dispatch bodies.
/// </summary>
public class CallTapGenerationTests
{
    private const string BlockStart = "        // ── Call tap ──";
    private const string BlockEnd = "        // ── end call tap ──";
    private const string MarkerSource = "[assembly: SharedMeta.Server.Core.Diagnostics.MetaCallTapProvider]";

    private const string SharedSource = """
        using System.Threading.Tasks;
        using SharedMeta.Core;

        [assembly: MetaSerializer(SerializerType.Generic)]

        namespace GenTest
        {
            [MessagePack.MessagePackObject]
            public partial class TestState : ISharedState
            {
                [MessagePack.Key(0)] public int Value { get; set; }
                [MessagePack.Key(1)] public int Bumps { get; set; }
            }

            [MetaService(StateType = typeof(TestState))]
            public interface ITestService : IMetaService
            {
                [MetaMethod(Mode = ExecutionMode.Server)]
                int Add(int value, string note);

                [MetaMethod(Mode = ExecutionMode.Optimistic)]
                void OnAdded();

                [MetaMethod(Mode = ExecutionMode.Query)]
                int Peek();

                [MetaMethod(Mode = ExecutionMode.Signal)]
                void Ping(long ticks);
            }

            [MetaServiceImpl(typeof(ITestService), typeof(TestState))]
            public partial class TestService : ITestService
            {
                public int Add(int value, string note) { State.Value += value; return State.Value; }

                [Trigger(On = "Add")]
                public void OnAdded() { State.Bumps++; }

                public int Peek() => State.Value;

                public void Ping(long ticks) { }

                [MetaInit]
                public Task<int> Init(int version) => Task.FromResult(1);
            }
        }
        """;

    /// <summary>A service with nothing the server dispatches: the method table is empty but still emitted.</summary>
    private const string LocalQueryOnlySource = """
        using SharedMeta.Core;

        [assembly: MetaSerializer(SerializerType.Generic)]

        namespace GenTest
        {
            [MessagePack.MessagePackObject]
            public partial class ViewState : ISharedState
            {
                [MessagePack.Key(0)] public int Value { get; set; }
            }

            [MetaService(StateType = typeof(ViewState))]
            public interface IViewService : IMetaService
            {
                [MetaMethod(Mode = ExecutionMode.LocalQuery)]
                int Read();
            }

            [MetaServiceImpl(typeof(IViewService), typeof(ViewState))]
            public partial class ViewService : IViewService
            {
                public int Read() => State.Value;
            }
        }
        """;

    private static readonly Lazy<MetadataReference[]> s_references = new(LoadReferences);
    private static readonly Lazy<MetadataReference> s_sharedImage = new(() => BuildSharedImage(SharedSource));

    [Fact]
    public void WithoutMarker_ServerOutputHasNoTapCode()
    {
        var run = RunServer(extraReferences: Array.Empty<MetadataReference>(), sources: Array.Empty<string>());

        var config = GeneratedText(run, "ServerMetaConfiguration.g.cs");
        Assert.DoesNotContain("SupportsCallTap", config);
        Assert.DoesNotContain("DispatchCallCore", config);
        Assert.DoesNotContain("IMetaMethodDescriptors", config);
        Assert.DoesNotContain(run.GeneratedTrees, t => t.FilePath.EndsWith("MetaMethodDescriptors.g.cs"));
    }

    [Fact]
    public void WithMarker_TapOnlyAddsCodeAroundUnchangedBodies()
    {
        var plain = GeneratedText(RunServer(Array.Empty<MetadataReference>(), Array.Empty<string>()), "ServerMetaConfiguration.g.cs");
        var tapped = GeneratedText(RunServer(Array.Empty<MetadataReference>(), new[] { MarkerSource }), "ServerMetaConfiguration.g.cs");

        Assert.Contains("public override bool SupportsCallTap => true;", tapped);
        Assert.Contains("TapDispatchSignal", tapped);
        Assert.Contains("TapRunInit", tapped);

        Assert.Equal(Normalize(plain), Normalize(StripTap(tapped)));
    }

    [Fact]
    public void WithMarker_GeneratedServerCompiles()
    {
        var run = RunServer(Array.Empty<MetadataReference>(), new[] { MarkerSource }, out var output);

        Assert.Contains(run.GeneratedTrees, t => t.FilePath.EndsWith("MetaMethodDescriptors.g.cs"));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(20)));
    }

    [Fact]
    public void WithMarker_ServiceWithoutDispatchableMethods_Compiles()
    {
        var run = RunServer(new[] { BuildSharedImage(LocalQueryOnlySource) }, new[] { MarkerSource }, out var output,
            includeDefaultShared: false);

        Assert.Contains(run.GeneratedTrees, t => t.FilePath.EndsWith("MetaMethodDescriptors.g.cs"));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(20)));
    }

    [Fact]
    public void MarkerInAReferencedAssembly_EnablesTheTap()
    {
        var tapLibrary = Emit(CSharpCompilation.Create(
            "TapLibrary",
            new[] { CSharpSyntaxTree.ParseText(MarkerSource) },
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));

        var config = GeneratedText(RunServer(new[] { tapLibrary }, Array.Empty<string>()), "ServerMetaConfiguration.g.cs");
        Assert.Contains("SupportsCallTap", config);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static GeneratorDriverRunResult RunServer(MetadataReference[] extraReferences, string[] sources)
        => RunServer(extraReferences, sources, out _);

    private static GeneratorDriverRunResult RunServer(MetadataReference[] extraReferences, string[] sources, out Compilation output,
        bool includeDefaultShared = true)
    {
        var references = includeDefaultShared ? s_references.Value.Append(s_sharedImage.Value) : s_references.Value;
        var compilation = CSharpCompilation.Create(
            "GenTest.Server",
            sources.Append("namespace GenTest.Host { internal static class Anchor { } }")
                .Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Latest))),
            references.Concat(extraReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return Drive(compilation, out output);
    }

    private static MetadataReference BuildSharedImage(string source)
    {
        var compilation = CSharpCompilation.Create(
            "GenTest.Shared",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Drive(compilation, out var output);
        return Emit(output);
    }

    private static GeneratorDriverRunResult Drive(Compilation compilation, out Compilation output)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SharedMetaGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        return driver.GetRunResult();
    }

    private static MetadataReference Emit(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20);
            throw new InvalidOperationException($"{compilation.AssemblyName} failed to compile:{Environment.NewLine}"
                + string.Join(Environment.NewLine, errors));
        }
        return MetadataReference.CreateFromImage(ImmutableArray.Create(stream.ToArray()));
    }

    // Everything this test process can load: the runtime, ASP.NET, and the SharedMeta assemblies
    // copied next to the test.
    private static MetadataReference[] LoadReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        foreach (var path in tpa.Split(Path.PathSeparator))
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("SharedMeta.Generator", StringComparison.Ordinal)) continue;
            paths.Add(path);
        }
        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    }

    private static string GeneratedText(GeneratorDriverRunResult run, string fileName)
    {
        var tree = run.GeneratedTrees.SingleOrDefault(t => t.FilePath.EndsWith(fileName, StringComparison.Ordinal));
        Assert.NotNull(tree);
        return tree!.ToString();
    }

    /// <summary>Undoes exactly what the tap adds: the per-provider block, the *Core renames and the method-table registration.</summary>
    private static string StripTap(string text)
    {
        var lines = Normalize(text).Split('\n').ToList();
        var result = new List<string>();
        bool inBlock = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith(BlockStart, StringComparison.Ordinal)) { inBlock = true; continue; }
            if (inBlock)
            {
                if (line == BlockEnd)
                {
                    inBlock = false;
                    i++; // the blank line that follows the block
                }
                continue;
            }
            if (line.Contains("Method table and argument codecs for the referenced call tap."))
            {
                i += 2; // registration line + blank line
                continue;
            }
            result.Add(line);
        }

        return string.Join("\n", result)
            .Replace("private System.Threading.Tasks.ValueTask<DispatchResult> DispatchCallCore(",
                     "protected override System.Threading.Tasks.ValueTask<DispatchResult> DispatchCall(")
            .Replace("private async Task DispatchSignalCore(", "protected override async Task DispatchSignal(")
            .Replace("private async System.Threading.Tasks.Task<int> RunInitCore(",
                     "protected override async System.Threading.Tasks.Task<int> RunInitAsync(");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
