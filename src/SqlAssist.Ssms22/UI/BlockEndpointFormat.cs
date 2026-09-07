using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace SqlAssist.Ssms22.UI;

internal static class BlockEndpointFormat
{
    public const string Keyword = "SqlAssist.BlockKeyword";
    public const string Symbol = "SqlAssist.BlockSymbol";

    [Export(typeof(ClassificationTypeDefinition))]
    [Name(Keyword)]
    [BaseDefinition("text")]
    internal static ClassificationTypeDefinition KeywordType = null!;

    [Export(typeof(ClassificationTypeDefinition))]
    [Name(Symbol)]
    [BaseDefinition("text")]
    internal static ClassificationTypeDefinition SymbolType = null!;
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = BlockEndpointFormat.Keyword)]
[Name(BlockEndpointFormat.Keyword)]
[Order(After = Priority.High)]
[UserVisible(false)]
internal sealed class BlockKeywordFormat : ClassificationFormatDefinition
{
    public BlockKeywordFormat() => DisplayName = "SqlAssist 配對端點預設";
}

[Export(typeof(EditorFormatDefinition))]
[ClassificationType(ClassificationTypeNames = BlockEndpointFormat.Symbol)]
[Name(BlockEndpointFormat.Symbol)]
[Order(After = Priority.High)]
[UserVisible(false)]
internal sealed class BlockSymbolFormat : ClassificationFormatDefinition
{
    public BlockSymbolFormat() => DisplayName = "SqlAssist 配對括號與字串引號";
}
