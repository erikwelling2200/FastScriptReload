using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor.Compilation.CodeRewriting
{
    /// <summary>
    /// Points every reference to a nested type declared in the patched file at the original type, by its fully qualified name.
    /// Only top-level types are renamed in a patch, so a nested type keeps its name inside the renamed copy and an unqualified
    /// reference binds to the copy's own second type: patched code would then create, cast and test against a type the
    /// unpatched code never uses. The nested declarations stay in the patch, so their methods are still redirected.
    ///
    /// Inside a nested class, a bare <c>this</c> is the copy's type while the object really is the original - redirected
    /// methods run on original instances - so it is cast to the original where it is used as a value.
    ///
    /// Must run on the original tree, before any other rewriter: it binds names with a semantic model of that tree.
    /// </summary>
    class NestedTypeReferenceRewriter : FastScriptReloadCodeRewriterBase
    {
        private readonly SemanticModel _model;
        private readonly SyntaxTree _tree;

        public NestedTypeReferenceRewriter(SemanticModel model, bool writeRewriteReasonAsComment)
            : base(writeRewriteReasonAsComment, false)
        {
            _model = model;
            _tree = model.SyntaxTree;
        }

        /// <summary>Whether the file declares a type inside another type - the only case this rewriter changes anything in.</summary>
        public static bool DeclaresNestedTypes(SyntaxNode root) =>
            root.DescendantNodes().Any(node => (node is BaseTypeDeclarationSyntax || node is DelegateDeclarationSyntax) && node.Parent is TypeDeclarationSyntax);

        /// <summary>
        /// The CLR full names (namespace-qualified, without nesting) of the top-level types in <paramref name="root"/> that
        /// declare nested types: the types whose nested types and private members a patch has to reach.
        /// </summary>
        public static IEnumerable<string> TopLevelTypesWithNestedTypes(SyntaxNode root) => root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(type => !(type.Parent is TypeDeclarationSyntax) && type.DescendantNodes().Any(node => (node is BaseTypeDeclarationSyntax || node is DelegateDeclarationSyntax) && node.Parent is TypeDeclarationSyntax))
            .Select(type =>
            {
                var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()).ToList();
                var name = type is TypeDeclarationSyntax declaration && declaration.TypeParameterList != null
                    ? $"{type.Identifier.ValueText}`{declaration.TypeParameterList.Parameters.Count}"
                    : type.Identifier.ValueText;
                return namespaces.Count > 0 ? $"{string.Join(".", namespaces)}.{name}" : name;
            });

        public override SyntaxNode Visit(SyntaxNode node)
        {
            // The whole name that binds to the type is replaced - Outer.Nested as one - so no later rewriter strips Outer.
            if (node is ExpressionSyntax && (node is SimpleNameSyntax || node is QualifiedNameSyntax || node is MemberAccessExpressionSyntax || node is AliasQualifiedNameSyntax)
                && !IsDeclarationName(node) && NestedTypeOfThisFile(node) is INamedTypeSymbol type)
            {
                var replacement = SyntaxFactory.ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).WithTriviaFrom(node);
                return AddRewriteCommentIfNeeded(replacement, $"{nameof(NestedTypeReferenceRewriter)}: original nested type");
            }
            return base.Visit(node);
        }

        public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node)
        {
            var nestedClass = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (nestedClass == null || !(nestedClass.Parent is TypeDeclarationSyntax)) return base.VisitThisExpression(node);
            // this.member reads the copy's own layout, which is the original's; only this as a value has the wrong type.
            if (node.Parent is MemberAccessExpressionSyntax access && access.Expression == node) return base.VisitThisExpression(node);
            if (node.Parent is ElementAccessExpressionSyntax || node.Parent is ConditionalAccessExpressionSyntax) return base.VisitThisExpression(node);
            if (!(_model.GetDeclaredSymbol(nestedClass) is INamedTypeSymbol original)) return base.VisitThisExpression(node);
            var cast = SyntaxFactory.ParseExpression($"(({original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})(object)this)").WithTriviaFrom(node);
            return AddRewriteCommentIfNeeded(cast, $"{nameof(NestedTypeReferenceRewriter)}: this as the original nested type");
        }

        /// <summary>The nested type <paramref name="node"/> names, when one of this file's types declares it; null otherwise.</summary>
        private INamedTypeSymbol NestedTypeOfThisFile(SyntaxNode node)
        {
            // The right-hand side of a qualified name is handled with its qualifier.
            if (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node) return null;
            if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node) return null;
            if (node.Parent is AliasQualifiedNameSyntax alias && alias.Name == node) return null;

            var symbol = _model.GetSymbolInfo(node).Symbol;
            if (symbol is IMethodSymbol constructor && constructor.MethodKind == MethodKind.Constructor) symbol = constructor.ContainingType;
            if (!(symbol is INamedTypeSymbol type) || type.ContainingType == null) return null;
            return type.OriginalDefinition.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == _tree) ? type : null;
        }

        /// <summary>A name that declares something - a nested type's own identifier is a token, but a <c>using</c> alias's is not.</summary>
        private static bool IsDeclarationName(SyntaxNode node) => node.Parent is UsingDirectiveSyntax || node.Parent is NameEqualsSyntax;
    }
}
