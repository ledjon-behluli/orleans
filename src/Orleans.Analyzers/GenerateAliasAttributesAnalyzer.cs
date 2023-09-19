using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Linq;

namespace Orleans.Analyzers;

//TODO:
// * Grain Interfaces - x
// * Interface Methods - x
// * Grain Classes (ones that inherited from Grain)
// * Classes, Structs, Enums that have [GenerateSerializer]

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class GenerateAliasAttributesAnalyzer : DiagnosticAnalyzer
{
    public const string RuleId = "ORLEANS0010";
    private const string Category = "Usage";
    private static readonly LocalizableString Title = new LocalizableResourceString(nameof(Resources.AddAliasAttributesTitle), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString MessageFormat = new LocalizableResourceString(nameof(Resources.AddAliasMessageFormat), Resources.ResourceManager, typeof(Resources));
    private static readonly LocalizableString Description = new LocalizableResourceString(nameof(Resources.AddAliasAttributesDescription), Resources.ResourceManager, typeof(Resources));

    private static readonly DiagnosticDescriptor Rule = new(RuleId, Title, MessageFormat, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description: Description);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);
        context.RegisterSyntaxNodeAction(CheckSyntaxNode,
            SyntaxKind.InterfaceDeclaration);
            //SyntaxKind.ClassDeclaration,
            //SyntaxKind.StructDeclaration,
            //SyntaxKind.RecordDeclaration,
            //SyntaxKind.RecordStructDeclaration);
    }

    private void CheckSyntaxNode(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is InterfaceDeclarationSyntax { } interfaceDeclaration)
        {
            if (!context.SemanticModel
                .GetDeclaredSymbol(interfaceDeclaration, context.CancellationToken)
                .ExtendsGrainInterface())
            {
                return;
            }

            if (!interfaceDeclaration.HasAttribute(Constants.AliasAttributeName))
            {
                Report(ref context, interfaceDeclaration.GetLocation(), interfaceDeclaration.Identifier.ToString());
            }

            foreach (var methodDeclaration in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (methodDeclaration.IsStatic())
                {
                    continue;
                }

                if (!methodDeclaration.HasAttribute(Constants.AliasAttributeName))
                {
                    Report(ref context, methodDeclaration.GetLocation(), methodDeclaration.Identifier.ToString());
                }                
            }

            return;
        }
    }

    private static void Report(ref SyntaxNodeAnalysisContext context, Location location, string typeName)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>();

        builder.Add("TypeName", typeName);

        context.ReportDiagnostic(Diagnostic.Create(
                       descriptor: Rule,
                       location: location,
                       properties: builder.ToImmutable()));
    }
}
