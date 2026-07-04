using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Weavie.Analyzers;

/// <summary>
/// Bans optional (default-valued) parameters so adding a parameter is a compile error at every call
/// site, forcing each caller to make an explicit decision. Exempts the two idioms that require a
/// default: <see cref="System.Threading.CancellationToken"/> and <c>[Caller*]</c> parameters.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoOptionalParametersAnalyzer : DiagnosticAnalyzer {
	/// <summary>Diagnostic id reported for a banned optional parameter.</summary>
	public const string DiagnosticId = "WV0001";

	private static readonly DiagnosticDescriptor Rule = new(
		id: DiagnosticId,
		title: "Optional parameters are banned",
		messageFormat: "Parameter '{0}' has a default value; optional parameters are banned except CancellationToken and [Caller*] parameters — add an explicit overload or require the argument at every call site",
		category: "Weavie.Design",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "A default value lets a new parameter be added without touching any call site, hiding the change and letting every caller silently inherit the default. Removing defaults turns 'add a parameter' into a compile error at each call site, so the decision is made explicitly.");

	/// <inheritdoc/>
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

	/// <inheritdoc/>
	public override void Initialize(AnalysisContext context) {
		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterCompilationStartAction(static start => {
			var cancellationToken = start.Compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
			string[] callerAttributeNames = [
				"System.Runtime.CompilerServices.CallerMemberNameAttribute",
				"System.Runtime.CompilerServices.CallerLineNumberAttribute",
				"System.Runtime.CompilerServices.CallerFilePathAttribute",
				"System.Runtime.CompilerServices.CallerArgumentExpressionAttribute",
			];
			var callerAttributes = callerAttributeNames
				.Select(start.Compilation.GetTypeByMetadataName)
				.Where(static symbol => symbol is not null)
				.Select(static symbol => symbol!)
				.ToImmutableArray();

			start.RegisterSyntaxNodeAction(
				ctx => AnalyzeParameter(ctx, cancellationToken, callerAttributes),
				SyntaxKind.Parameter);
		});
	}

	private static void AnalyzeParameter(
		SyntaxNodeAnalysisContext context,
		INamedTypeSymbol? cancellationToken,
		ImmutableArray<INamedTypeSymbol> callerAttributes) {
		var syntax = (ParameterSyntax)context.Node;
		if (syntax.Default is null) {
			return;
		}

		if (context.SemanticModel.GetDeclaredSymbol(syntax, context.CancellationToken) is not IParameterSymbol symbol) {
			return;
		}

		if (cancellationToken is not null && SymbolEqualityComparer.Default.Equals(symbol.Type, cancellationToken)) {
			return;
		}

		foreach (var attribute in symbol.GetAttributes()) {
			if (attribute.AttributeClass is { } attributeClass
				&& callerAttributes.Any(caller => SymbolEqualityComparer.Default.Equals(attributeClass, caller))) {
				return;
			}
		}

		context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.Default.GetLocation(), symbol.Name));
	}
}
