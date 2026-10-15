using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.AgentBridge
{
    /// <summary>
    /// Decides from source alone whether an edit can be hot-reloaded with certainty: only method bodies changed, and nothing
    /// in the file trips one of the patterns where Fast Script Reload compiles and redirects without the change taking effect.
    /// </summary>
    /// <remarks>
    /// It is a whitelist on purpose. An edit it cannot prove safe is reported as needing a recompile, never as applied.
    /// </remarks>
    public static class AgentReloadGate
    {
        private static readonly HashSet<string> InitialisationMessages = new HashSet<string>
        {
            "Awake", "Start", "OnEnable", "OnValidate", "Reset", "CreateGUI", "OnAfterDeserialize",
        };

        private static readonly HashSet<string> InitialisationAttributes = new HashSet<string>
        {
            "InitializeOnLoadMethod", "RuntimeInitializeOnLoadMethod", "DidReloadScripts", "InitializeOnEnterPlayMode",
        };

        public class Verdict
        {
            public readonly List<string> Violations = new List<string>();
            public readonly List<string> Caveats = new List<string>();
            public readonly List<ChangedMember> ChangedMembers = new List<ChangedMember>();
            public bool NoCodeChange;
        }

        /// <summary>A method or accessor whose body changed, with the names it carries in the compiled assembly.</summary>
        public class ChangedMember
        {
            public string TypeMetadataName;
            public string DisplayName;
            public string[] RuntimeNames;
            public bool IsOperator;
        }

        public static Verdict Analyse(string compiledSource, string currentSource, IEnumerable<string> preprocessorSymbols)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols);
            var oldRoot = CSharpSyntaxTree.ParseText(compiledSource, options).GetCompilationUnitRoot();
            var newRoot = CSharpSyntaxTree.ParseText(currentSource, options).GetCompilationUnitRoot();
            var verdict = new Verdict();

            if (SyntaxFactory.AreEquivalent(oldRoot, newRoot, topLevel: false))
            {
                verdict.NoCodeChange = true;
                return verdict;
            }

            CheckFile(newRoot, verdict.Violations);
            if (!SyntaxFactory.AreEquivalent(Skeleton(oldRoot), Skeleton(newRoot), topLevel: false))
            {
                verdict.Violations.Add("more than method bodies changed (a declaration, signature, field, initializer, constructor, attribute or using differs)");
                return verdict;
            }

            var oldBodies = BodyOwners(oldRoot).ToList();
            var newBodies = BodyOwners(newRoot).ToList();
            for (var i = 0; i < newBodies.Count; i++)
            {
                if (SyntaxFactory.AreEquivalent(oldBodies[i], newBodies[i], topLevel: false)) continue;
                CheckChangedMember(oldBodies[i], newBodies[i], verdict);
            }
            return verdict;
        }

        /// <summary>Patterns that make every patch of the file unreliable, whichever member changed.</summary>
        private static void CheckFile(CompilationUnitSyntax root, List<string> violations)
        {
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.Modifiers.Any(SyntaxKind.PartialKeyword)) violations.Add($"{type.Identifier.Text} is a partial type, which is compiled without its other parts");
                if (type is RecordDeclarationSyntax) violations.Add($"{type.Identifier.Text} is a record");
                if (HasAttribute(type.AttributeLists, "PreventHotReload")) violations.Add($"{type.Identifier.Text} is marked [PreventHotReload]");
                if (HasAttribute(type.AttributeLists, "BurstCompile")) violations.Add($"{type.Identifier.Text} is Burst-compiled");
            }

            // Only top-level types get a renamed patched copy; a nested one keeps its name, so patched code would bind to a second,
            // unrelated type of the same name and misread the instances the original code created.
            foreach (var nested in root.DescendantNodes().Where(node => (node is BaseTypeDeclarationSyntax || node is DelegateDeclarationSyntax) && node.Parent is TypeDeclarationSyntax))
            {
                var name = nested is BaseTypeDeclarationSyntax type ? type.Identifier.Text : ((DelegateDeclarationSyntax)nested).Identifier.Text;
                violations.Add($"the file declares nested type {name}, which patched code would confuse with a copy of itself");
            }

            // The patched copy of a type declares its own statics, so redirected code would read and write a second, empty copy.
            foreach (var field in root.DescendantNodes().OfType<BaseFieldDeclarationSyntax>())
            {
                if (!field.Modifiers.Any(SyntaxKind.StaticKeyword) || field.Modifiers.Any(SyntaxKind.ConstKeyword)) continue;
                var names = string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.Text));
                violations.Add($"the file declares static field {names}; redirected code would use a separate copy of it");
            }
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var isAuto = property.AccessorList != null && property.AccessorList.Accessors.All(accessor => accessor.Body == null && accessor.ExpressionBody == null);
                if (property.Modifiers.Any(SyntaxKind.StaticKeyword) && isAuto)
                {
                    violations.Add($"the file declares static auto-property {property.Identifier.Text}; redirected code would use a separate copy of it");
                }
            }
        }

        private static void CheckChangedMember(SyntaxNode oldMember, SyntaxNode newMember, Verdict verdict)
        {
            var declaration = Declaration(newMember);
            var name = DisplayName(declaration);
            var types = newMember.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().ToList();

            foreach (var type in types)
            {
                if (!(type is ClassDeclarationSyntax classDeclaration))
                {
                    verdict.Violations.Add($"{name} is in {type.Identifier.Text}, which is not a class (struct and interface methods are not redirected)");
                    return;
                }
                if (classDeclaration.TypeParameterList != null)
                {
                    verdict.Violations.Add($"{name} is in generic type {type.Identifier.Text}");
                    return;
                }
            }
            if (declaration is MethodDeclarationSyntax method)
            {
                if (method.TypeParameterList != null) verdict.Violations.Add($"{name} is a generic method");
                if (method.Modifiers.Any(SyntaxKind.AsyncKeyword)) verdict.Violations.Add($"{name} is async");
            }

            // Compiler-generated closure, iterator and async classes are matched by generated names that shift between compiles.
            var generated = new[] { oldMember, newMember }.SelectMany(node => node.DescendantNodes()).FirstOrDefault(node =>
                node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax || node is QueryExpressionSyntax
                || node is YieldStatementSyntax || node is AwaitExpressionSyntax);
            if (generated != null) verdict.Violations.Add($"{name} contains a lambda, local function, query, iterator or await");

            if (declaration is MemberDeclarationSyntax member && HasAttribute(member.AttributeLists, "MethodImpl") && member.AttributeLists.ToString().Contains("AggressiveInlining"))
            {
                verdict.Violations.Add($"{name} is marked AggressiveInlining");
            }

            var identifier = Identifier(declaration);
            if (InitialisationMessages.Contains(identifier) || (declaration is MemberDeclarationSyntax attributed && InitialisationAttributes.Any(attribute => HasAttribute(attributed.AttributeLists, attribute))))
            {
                verdict.Caveats.Add($"{name} runs once at initialisation; objects that already ran it keep what the old code did, only later calls use the new code");
            }

            verdict.ChangedMembers.Add(new ChangedMember
            {
                TypeMetadataName = MetadataName(types),
                DisplayName = name,
                RuntimeNames = RuntimeNames(newMember, declaration),
                IsOperator = declaration is OperatorDeclarationSyntax || declaration is ConversionOperatorDeclarationSyntax,
            });
        }

        /// <summary>Every node that owns a hot-reloadable body, in document order: methods, operators and accessors.</summary>
        private static IEnumerable<SyntaxNode> BodyOwners(SyntaxNode root)
        {
            return root.DescendantNodes().Where(IsBodyOwner);
        }

        private static bool IsBodyOwner(SyntaxNode node)
        {
            switch (node)
            {
                case MethodDeclarationSyntax method: return method.Body != null || method.ExpressionBody != null;
                case OperatorDeclarationSyntax op: return op.Body != null || op.ExpressionBody != null;
                case ConversionOperatorDeclarationSyntax conversion: return conversion.Body != null || conversion.ExpressionBody != null;
                case AccessorDeclarationSyntax accessor: return accessor.Body != null || accessor.ExpressionBody != null;
                case ArrowExpressionClauseSyntax arrow: return arrow.Parent is PropertyDeclarationSyntax || arrow.Parent is IndexerDeclarationSyntax;
                default: return false;
            }
        }

        /// <summary>The file with every hot-reloadable body emptied: equal skeletons mean only those bodies differ.</summary>
        private static SyntaxNode Skeleton(SyntaxNode root)
        {
            return root.ReplaceNodes(BodyOwners(root).Where(node => !(node.Parent != null && IsBodyOwner(node.Parent))), (original, _) =>
            {
                switch (original)
                {
                    case ArrowExpressionClauseSyntax arrow:
                        return arrow.WithExpression(SyntaxFactory.IdentifierName("_"));
                    case MethodDeclarationSyntax method:
                        return method.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
                    case OperatorDeclarationSyntax op:
                        return op.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
                    case ConversionOperatorDeclarationSyntax conversion:
                        return conversion.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
                    case AccessorDeclarationSyntax accessor:
                        return accessor.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
                    default:
                        throw new InvalidOperationException($"Unexpected body owner {original.Kind()}");
                }
            });
        }

        private static SyntaxNode Declaration(SyntaxNode bodyOwner)
        {
            if (bodyOwner is ArrowExpressionClauseSyntax) return bodyOwner.Parent;
            if (bodyOwner is AccessorDeclarationSyntax) return bodyOwner.Parent.Parent;
            return bodyOwner;
        }

        private static string Identifier(SyntaxNode declaration)
        {
            switch (declaration)
            {
                case MethodDeclarationSyntax method: return method.Identifier.Text;
                case PropertyDeclarationSyntax property: return property.Identifier.Text;
                case EventDeclarationSyntax eventDeclaration: return eventDeclaration.Identifier.Text;
                case IndexerDeclarationSyntax _: return "this[]";
                case OperatorDeclarationSyntax op: return "operator " + op.OperatorToken.Text;
                case ConversionOperatorDeclarationSyntax conversion: return "operator " + conversion.Type;
                default: return declaration.Kind().ToString();
            }
        }

        private static string DisplayName(SyntaxNode declaration)
        {
            var type = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            return type == null ? Identifier(declaration) : $"{type.Identifier.Text}.{Identifier(declaration)}";
        }

        /// <summary>Names the member's IL methods can have; explicit interface implementations match on their suffix.</summary>
        private static string[] RuntimeNames(SyntaxNode bodyOwner, SyntaxNode declaration)
        {
            var accessorKinds = bodyOwner is AccessorDeclarationSyntax accessor
                ? new[] { accessor.Keyword.Text }
                : bodyOwner is ArrowExpressionClauseSyntax ? new[] { "get" } : null;

            switch (declaration)
            {
                case MethodDeclarationSyntax method:
                    return new[] { method.Identifier.Text };
                case PropertyDeclarationSyntax property:
                    return accessorKinds.Select(kind => $"{kind}_{property.Identifier.Text}").ToArray();
                case IndexerDeclarationSyntax _:
                    return accessorKinds.Select(kind => $"{kind}_Item").ToArray();
                case EventDeclarationSyntax eventDeclaration:
                    return accessorKinds.Select(kind => $"{kind}_{eventDeclaration.Identifier.Text}").ToArray();
                default:
                    return Array.Empty<string>();
            }
        }

        private static string MetadataName(List<BaseTypeDeclarationSyntax> types)
        {
            var outermost = types.First();
            var namespaces = outermost.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString());
            var prefix = string.Join(".", namespaces);
            var nested = string.Join("+", types.Select(type => type.Identifier.Text));
            return prefix.Length == 0 ? nested : $"{prefix}.{nested}";
        }

        private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name)
        {
            return lists.SelectMany(list => list.Attributes).Any(attribute =>
            {
                var text = attribute.Name.ToString();
                var simple = text.Substring(text.LastIndexOf('.') + 1);
                return simple == name || simple == name + "Attribute";
            });
        }
    }
}
