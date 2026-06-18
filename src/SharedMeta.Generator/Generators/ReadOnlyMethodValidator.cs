using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharedMeta.Generator.Generators
{
    /// <summary>
    /// 0.29.1+ Walks every <c>[MetaServiceImpl]</c> method whose owning interface declaration
    /// has <c>[MetaMethod(Mode = LocalQuery)]</c> or <c>[MetaMethod(Mode = Query)]</c> and
    /// validates the body satisfies the read-only contract:
    /// <list type="bullet">
    /// <item><b>No State mutation</b> — direct <c>State.X = …</c> assignments are rejected.</item>
    /// <item><b>No <see cref="SharedMeta.Core.Random.IMetaRandom"/> consumption</b> — <c>Context.Random</c>,
    /// <c>Context.ServerRandom</c>, and generator-emitted <c>{Name}Random</c> properties from
    /// <c>[NamedRandom]</c> would advance scroll position only on the calling client (LocalQuery)
    /// or only inside an off-replay path (Query), breaking determinism for every other observer.</item>
    /// <item><b>No cross-entity calls</b> — generator-emitted <c>GetI{Service}(entityId)</c> helpers
    /// round-trip through the grain mesh; calling them from a read-only method would either bypass
    /// the no-RPC promise (LocalQuery) or do an unbounded grain fan-out from a server read (Query).</item>
    /// </list>
    ///
    /// <para>Detection is <i>syntactic</i>: walks the method body via <see cref="CSharpSyntaxWalker"/>
    /// and inspects identifier chains. It does NOT catch:</para>
    /// <list type="bullet">
    /// <item>Collection mutations through methods (<c>State.Items.Add(x)</c>, <c>State.Map.Clear()</c>).</item>
    /// <item>Indirect mutations through helper methods that themselves mutate.</item>
    /// <item>Reflection-based access.</item>
    /// </list>
    /// <para>Those remain developer responsibility, documented on <c>ExecutionMode.LocalQuery</c>.</para>
    /// </summary>
    public static class ReadOnlyMethodValidator
    {
        public static string? Generate(ClassDeclarationSyntax node, INamedTypeSymbol symbol, Compilation compilation)
        {
            var attr = symbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaServiceImplAttribute");
            if (attr == null) return null;
            if (attr.ConstructorArguments.Length < 1) return null;
            var serviceInterfaceSymbol = attr.ConstructorArguments[0].Value as INamedTypeSymbol;
            if (serviceInterfaceSymbol == null) return null;

            // 0.29.1+ State-alias detection. The walker checks left-chain roots against
            // "State" plus any class member whose declared type matches the impl's state type.
            // Catches the very common `private GameState profile => State;` / `=> Context.State;`
            // shortcut where a developer mutates State via the alias and the literal-"State" check
            // would otherwise miss it. Local-variable aliasing (`var p = State; p.X = 1;`) requires
            // data-flow analysis and remains a documented gap.
            var stateAliases = new HashSet<string> { "State" };
            var stateTypeSymbol = attr.ConstructorArguments.Length >= 2
                ? attr.ConstructorArguments[1].Value as INamedTypeSymbol
                : null;
            if (stateTypeSymbol != null)
            {
                var stateTypeName = stateTypeSymbol.Name;        // simple name, e.g. "GameState"
                var stateTypeFqn  = stateTypeSymbol.ToDisplayString();
                foreach (var member in node.Members)
                {
                    switch (member)
                    {
                        case FieldDeclarationSyntax f when TypeMatches(f.Declaration.Type, stateTypeName, stateTypeFqn):
                            foreach (var v in f.Declaration.Variables)
                                stateAliases.Add(v.Identifier.Text);
                            break;
                        case PropertyDeclarationSyntax p when TypeMatches(p.Type, stateTypeName, stateTypeFqn):
                            stateAliases.Add(p.Identifier.Text);
                            break;
                    }
                }
            }

            // Build map of interface method name → declared ExecutionMode (only LocalQuery / Query).
            var readOnlyMethods = new Dictionary<string, string>();
            foreach (var member in serviceInterfaceSymbol.GetMembers().OfType<IMethodSymbol>())
            {
                var mm = member.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "SharedMeta.Core.MetaMethodAttribute");
                if (mm == null) continue;
                var modeArg = mm.NamedArguments.FirstOrDefault(a => a.Key == "Mode");
                if (modeArg.Value.IsNull) continue;
                if (modeArg.Value.Value is not int modeInt) continue;
                // LocalQuery = 0 (post-rename in 0.29.0), Query = 6. Both are read-only.
                string? modeName = modeInt switch
                {
                    0 => "LocalQuery",
                    6 => "Query",
                    _ => null,
                };
                if (modeName != null) readOnlyMethods[member.Name] = modeName;
            }
            if (readOnlyMethods.Count == 0) return null;

            // Build same-class method map so the walker can recurse into helper bodies. Key by
            // declared name (overloads collapse — fine, walker will analyze every body with that name).
            var classMethods = new Dictionary<string, List<MethodDeclarationSyntax>>();
            foreach (var m in node.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!classMethods.TryGetValue(m.Identifier.Text, out var list))
                {
                    list = new List<MethodDeclarationSyntax>();
                    classMethods[m.Identifier.Text] = list;
                }
                list.Add(m);
            }

            // Walk impl class methods. For each whose name matches a read-only interface method,
            // collect violations from the body.
            var diagnostics = new List<string>();
            foreach (var methodDecl in node.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!readOnlyMethods.TryGetValue(methodDecl.Identifier.Text, out var modeName)) continue;
                var body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                if (body == null) continue;

                var walker = new ReadOnlyBodyWalker(
                    modeName,
                    $"{symbol.Name}.{methodDecl.Identifier.Text}",
                    classMethods,
                    stateAliases);
                walker.Visit(body);
                diagnostics.AddRange(walker.Errors);
            }

            if (diagnostics.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Read-only contract violations detected for [MetaMethod(Mode = LocalQuery|Query)] methods.");
            sb.AppendLine("// See ExecutionMode.LocalQuery xmldoc for the full contract.");
            sb.AppendLine();
            foreach (var msg in diagnostics)
            {
                // #error gives a CS1029 with the method-FQN-tagged message at the impl class
                // location. Roslyn's IDE surface shows the squiggle on the #error line; the
                // message tells the dev which method + which line to fix.
                sb.AppendLine($"#error {msg}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// BCL collection mutator method names. Any invocation <c>State.X.{Name}(...)</c> where
        /// <c>Name</c> matches this set is flagged. Covers <see cref="List{T}"/>,
        /// <see cref="Dictionary{TKey,TValue}"/>, <see cref="HashSet{T}"/>, <see cref="Queue{T}"/>,
        /// <see cref="Stack{T}"/>, and the corresponding non-generic collections. User-defined
        /// collections with custom mutator names (e.g. <c>State.Inventory.AddItem(x)</c>) are not
        /// caught — extend the set or rename for clarity.
        /// </summary>
        private static readonly HashSet<string> CollectionMutators = new()
        {
            "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange",
            "Insert", "InsertRange", "Clear", "Sort", "Reverse",
            "Enqueue", "Dequeue", "Push", "Pop",
            "TrimExcess", "EnsureCapacity", "TryAdd",
        };

        /// <summary>Max recursion depth when walking same-class helper bodies — guards against pathological cycles.</summary>
        private const int MaxHelperDepth = 5;

        private sealed class ReadOnlyBodyWalker : CSharpSyntaxWalker
        {
            private readonly string _modeName;
            private readonly string _methodFqn;
            private readonly Dictionary<string, List<MethodDeclarationSyntax>> _classMethods;
            private readonly HashSet<string> _stateAliases;

            // 0.29.1+ Level-1 local-variable alias tracking. Per-walker (not shared with parent
            // / sub-walkers) so a helper's local 'p' doesn't accidentally alias the caller's
            // State-bound 'p'. Catches the common pattern
            //   var p = State;      → p is State
            //   var c = State.Cards; → c is State-rooted alias
            // Does NOT track reassignments (`p = otherThing`) or branch-conditional binds — that
            // requires Level-2 (control-flow) analysis. Doc'd as developer responsibility.
            private readonly HashSet<string> _localAliases = new();

            private readonly HashSet<string> _visited;
            private readonly int _depth;
            public List<string> Errors { get; } = new();

            public ReadOnlyBodyWalker(string modeName, string methodFqn,
                Dictionary<string, List<MethodDeclarationSyntax>> classMethods,
                HashSet<string> stateAliases,
                HashSet<string>? visited = null, int depth = 0)
            {
                _modeName = modeName;
                _methodFqn = methodFqn;
                _classMethods = classMethods;
                _stateAliases = stateAliases;
                _visited = visited ?? new HashSet<string>();
                _depth = depth;
            }

            /// <summary>True when <paramref name="root"/> matches the class-level State or a
            /// local variable previously bound to a State-rooted expression.</summary>
            private bool IsStateRoot(string? root)
                => root != null && (_stateAliases.Contains(root) || _localAliases.Contains(root));

            public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
            {
                foreach (var v in node.Declaration.Variables)
                {
                    if (v.Initializer?.Value is { } init
                        && LeftChainRoot(init) is { } initRoot
                        && IsStateRoot(initRoot))
                    {
                        _localAliases.Add(v.Identifier.Text);
                    }
                }
                base.VisitLocalDeclarationStatement(node);
            }

            /// <summary>
            /// <c>foreach (var x in State.Cells)</c> — binds <c>x</c> to each State-rooted element.
            /// Mutating <c>x.Y = 1</c> inside the loop mutates the snapshot through a reference
            /// when the element is a class (ref type) or a struct field on a List. Either way it's
            /// a contract violation; treat the loop variable as a State alias.
            /// <para>
            /// Scope caveat: the local alias persists for the rest of the method, not just the
            /// loop body. Re-using the same name later with a non-State binding would be a false
            /// positive — Level 2 scope-tracking deferred.
            /// </para>
            /// </summary>
            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                if (IsStateRoot(LeftChainRoot(node.Expression)))
                {
                    _localAliases.Add(node.Identifier.Text);
                }
                base.VisitForEachStatement(node);
            }

            public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
            {
                var root = LeftChainRoot(node.Left);
                if (IsStateRoot(root))
                {
                    var via = root == "State" ? "State" : $"State alias '{root}'";
                    Report(node, $"must not mutate {via} (direct assignment detected). " +
                                 "Switch to ExecutionMode.Optimistic / Server for writes, or move the write out of this method.");
                }
                base.VisitAssignmentExpression(node);
            }

            public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
            {
                // Context.Random / Context.ServerRandom
                if (node.Expression is IdentifierNameSyntax id && id.Identifier.Text == "Context")
                {
                    var name = node.Name.Identifier.Text;
                    if (name == "Random" || name == "ServerRandom")
                    {
                        Report(node, $"must not consume Context.{name} — random scroll advances would diverge per-client.");
                    }
                }

                // <Name>Random property (generator-emitted [NamedRandom] accessor — naming
                // convention is "{NamedRandomName}Random"). Catch any identifier ending with
                // "Random" used as a member access expression value.
                if (node.Expression is IdentifierNameSyntax randIdent && randIdent.Identifier.Text.EndsWith("Random"))
                {
                    Report(node, $"must not consume named random stream '{randIdent.Identifier.Text}' — advances scroll position non-deterministically.");
                }

                base.VisitMemberAccessExpression(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // 0.29.1+ Out-var aliasing through state-rooted calls:
                //   if (State.Map.TryGetValue(k, out var value)) { value.X = 1; }
                // The out parameter typically lands on an internal slot of the collection
                // (reference type elements share identity with the dictionary entry; struct
                // copies are detached but their fields aren't usually mutated anyway). Treat
                // every out-var emitted from a state-rooted invocation as a State alias.
                if (node.Expression is MemberAccessExpressionSyntax memForOut
                    && IsStateRoot(LeftChainRoot(memForOut.Expression))
                    && node.ArgumentList != null)
                {
                    foreach (var arg in node.ArgumentList.Arguments)
                    {
                        if (arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                            && arg.Expression is DeclarationExpressionSyntax decl
                            && decl.Designation is SingleVariableDesignationSyntax sv)
                        {
                            _localAliases.Add(sv.Identifier.Text);
                        }
                    }
                }

                // Collection mutators: State.X.{Add/Remove/Clear/...}(...). Walk left chain up to
                // the receiver of the called method to confirm it's State-rooted.
                if (node.Expression is MemberAccessExpressionSyntax mem
                    && CollectionMutators.Contains(mem.Name.Identifier.Text))
                {
                    var collectionRoot = LeftChainRoot(mem.Expression);
                    if (IsStateRoot(collectionRoot))
                    {
                        var via = collectionRoot == "State" ? "State" : $"State alias '{collectionRoot}'";
                        Report(node, $"must not call collection mutator '{mem.Name.Identifier.Text}' on {via}.* — " +
                                     "modifies the replicated snapshot. Switch to Optimistic/Server, or move the mutation out.");
                        // Don't return — keep visiting children so further violations in the same body surface.
                    }
                }

                // Generator-emitted cross-entity helpers follow the pattern
                //   Get<InterfaceName>(string entityId).<Method>(...)
                // where InterfaceName begins with 'I'. Detect the inner Get call's identifier.
                string? calledMethod = node.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax m2 => m2.Name.Identifier.Text,
                    _ => null,
                };
                if (calledMethod != null
                    && calledMethod.Length > 4
                    && calledMethod.StartsWith("GetI")
                    && calledMethod.EndsWith("Service"))
                {
                    Report(node, $"must not perform cross-entity call '{calledMethod}' — round-trips through grain mesh, " +
                                 "violates read-only contract. Move the cross-entity touch into an Optimistic / Server method.");
                }

                // 0.29.1+ Recurse into same-class helper body. Catches
                //   public int CardsInHand() => CountCards();
                //   private int CountCards() { State.Count++; return State.Cards.Count; }
                // Without recursion the State mutation in the helper would slip through.
                if (calledMethod != null
                    && _depth < MaxHelperDepth
                    && !_visited.Contains(calledMethod)
                    && _classMethods.TryGetValue(calledMethod, out var helperDecls))
                {
                    _visited.Add(calledMethod);
                    foreach (var helper in helperDecls)
                    {
                        var hbody = (SyntaxNode?)helper.Body ?? helper.ExpressionBody;
                        if (hbody == null) continue;
                        // Helper FQN keeps the LocalQuery method's FQN as the public-facing label —
                        // the dev opened the LocalQuery method, the helper is the implementation detail.
                        var sub = new ReadOnlyBodyWalker(_modeName, $"{_methodFqn} → {calledMethod}",
                            _classMethods, _stateAliases, _visited, _depth + 1);
                        sub.Visit(hbody);
                        Errors.AddRange(sub.Errors);
                    }
                    // Allow other call sites to the same helper to re-enter from a different LocalQuery's
                    // root walk (each top-level walker has its own _visited).
                }

                base.VisitInvocationExpression(node);
            }

            private void Report(SyntaxNode loc, string detail)
            {
                var lineSpan = loc.GetLocation().GetLineSpan();
                var line = lineSpan.StartLinePosition.Line + 1;
                var col = lineSpan.StartLinePosition.Character + 1;
                Errors.Add($"SharedMeta: [MetaMethod(Mode = ExecutionMode.{_modeName})] '{_methodFqn}' at line {line}:{col} {detail}");
            }

            /// <summary>
            /// Walk the leftmost identifier chain of an expression (handles <c>X</c>, <c>X.Y</c>,
            /// <c>X.Y.Z</c>, <c>X.Y[0]</c>, <c>X?.Y</c>, parenthesized variants) and return the root
            /// identifier name. Returns <c>null</c> when the chain doesn't terminate at a simple
            /// identifier (e.g. starts with a method call or a cast).
            /// </summary>
            private static string? LeftChainRoot(ExpressionSyntax expr)
            {
                ExpressionSyntax cur = expr;
                while (true)
                {
                    switch (cur)
                    {
                        case IdentifierNameSyntax id:
                            return id.Identifier.Text;
                        case MemberAccessExpressionSyntax m:
                            cur = m.Expression;
                            break;
                        case ElementAccessExpressionSyntax e:
                            cur = e.Expression;
                            break;
                        case ConditionalAccessExpressionSyntax c:
                            cur = c.Expression;
                            break;
                        case ParenthesizedExpressionSyntax p:
                            cur = p.Expression;
                            break;
                        default:
                            return null;
                    }
                }
            }
        }

        /// <summary>
        /// Syntactic type match — compares <paramref name="declared"/>'s text against the state
        /// type's simple name OR FQN. Handles both <c>private GameState profile;</c> and
        /// <c>private MyNs.GameState profile;</c> forms. Nullable annotations stripped. Generic
        /// arguments not considered (state types are normally non-generic).
        /// </summary>
        private static bool TypeMatches(TypeSyntax declared, string simpleName, string fqn)
        {
            var text = declared.ToString().TrimEnd('?').Trim();
            return text == simpleName || text == fqn || text.EndsWith("." + simpleName);
        }
    }
}
