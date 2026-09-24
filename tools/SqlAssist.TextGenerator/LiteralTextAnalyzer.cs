using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SqlAssist.TextGenerator;

/// <summary>擋下寫在程式碼裡的中文字面值，讓使用者看得到的文字只有 .resjson 一個出處。</summary>
/// <remarks>
/// 豁免沿用 BCL 的 <c>[Localizable(false)]</c>（CA1303 的同一套語意）：標在參數上，
/// 直接傳進去的字面值不檢查；標在成員或型別上，裡面的字面值都不檢查。診斷紀錄與
/// 平台防護的作業名稱就是這樣豁免的——紀錄給維護者比對，不隨介面語言切換。
/// 遷移期間尚未處理的資料夾以該資料夾的 .editorconfig 把 SQLTXT100 關掉，處理完就刪檔。
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LiteralTextAnalyzer : DiagnosticAnalyzer
{
    private const string CheckProperty = "build_property.SqlAssistTextCheckLiterals";
    private const int PreviewLength = 24;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(SqlTextDiagnostics.LiteralText);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(CheckProperty, out var value)
                && string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            start.RegisterSyntaxNodeAction(
                Analyze,
                SyntaxKind.StringLiteralExpression,
                SyntaxKind.CharacterLiteralExpression,
                SyntaxKind.InterpolatedStringExpression);
        });
    }

    internal static bool ContainsCjk(string text)
    {
        foreach (var c in text)
        {
            if (c >= '　' && c <= '〿'
                || c >= '㐀' && c <= '䶿'
                || c >= '一' && c <= '鿿'
                || c >= '豈' && c <= '﫿'
                || c >= '＀' && c <= '￯')
            {
                return true;
            }
        }

        return false;
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var text = context.Node switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => string.Concat(
                interpolated.Contents.OfType<InterpolatedStringTextSyntax>().Select(part => part.TextToken.ValueText)),
            _ => string.Empty,
        };

        if (!ContainsCjk(text)
            || context.Node.FirstAncestorOrSelf<AttributeSyntax>() is not null
            || IsExemptMember(context.ContainingSymbol)
            || IsExemptArgument(context))
        {
            return;
        }

        var preview = text.Replace("\r", string.Empty).Replace("\n", " ");
        if (preview.Length > PreviewLength)
        {
            preview = preview.Substring(0, PreviewLength) + "…";
        }

        context.ReportDiagnostic(Diagnostic.Create(SqlTextDiagnostics.LiteralText, context.Node.GetLocation(), preview));
    }

    private static bool IsExemptMember(ISymbol? symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.GetAttributes().Any(IsNotLocalizable))
            {
                return true;
            }

            // 屬性存取子與事件存取子的屬性標在屬性本身。
            if (current is IMethodSymbol { AssociatedSymbol: { } associated }
                && associated.GetAttributes().Any(IsNotLocalizable))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExemptArgument(SyntaxNodeAnalysisContext context)
    {
        var argument = context.Node.FirstAncestorOrSelf<ArgumentSyntax>();
        if (argument is null
            || context.SemanticModel.GetOperation(argument, context.CancellationToken) is not IArgumentOperation operation
            || operation.Parameter is not { } parameter)
        {
            return false;
        }

        // 字面值要「直接」流進參數；中間隔著 lambda 的是另一段程式碼，不算。
        for (var node = context.Node.Parent; node is not null && node != argument; node = node.Parent)
        {
            if (node is AnonymousFunctionExpressionSyntax)
            {
                return false;
            }
        }

        return parameter.GetAttributes().Any(IsNotLocalizable);
    }

    private static bool IsNotLocalizable(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.LocalizableAttribute"
        && attribute.ConstructorArguments.Length == 1
        && attribute.ConstructorArguments[0].Value is false;
}
