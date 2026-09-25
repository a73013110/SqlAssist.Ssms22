using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlIcons
{
    private sealed class Definition
    {
        public Definition(ImageMoniker moniker, string automationName)
        {
            // 同一個 moniker 同時供 WPF 與編輯器 adornment 使用，避免兩份對照漂移。
            Moniker = moniker;
            Element = new ImageElement(moniker.ToImageId(), automationName);
        }

        public ImageMoniker Moniker { get; }

        public ImageElement Element { get; }
    }

    // 依語意快取不可變資料；CrispImage 屬於各自的視覺樹，不在這裡共用。
    private static readonly Definition Unknown = new(KnownMonikers.UnknownMember, ChromeText.IconUnknown);
    private static readonly Definition Keyword = new(KnownMonikers.IntellisenseKeyword, ChromeText.IconKeyword);
    private static readonly Definition Snippet = new(KnownMonikers.Snippet, ChromeText.IconSnippet);
    private static readonly Definition Schema = new(KnownMonikers.Schema, ChromeText.IconSchema);
    private static readonly Definition Table = new(KnownMonikers.Table, ChromeText.IconTable);
    private static readonly Definition View = new(KnownMonikers.View, ChromeText.IconView);
    private static readonly Definition Procedure = new(KnownMonikers.StoredProcedure, ChromeText.IconProcedure);
    private static readonly Definition ScalarFunction = new(KnownMonikers.ScalarFunction, ChromeText.IconScalarFunction);
    private static readonly Definition Column = new(KnownMonikers.Column, CommonText.Column);
    private static readonly Definition BuiltInFunction = new(KnownMonikers.Method, ChromeText.IconBuiltInFunction);
    private static readonly Definition TableFunction = new(KnownMonikers.TableFunction, ChromeText.IconTableFunction);
    private static readonly Definition InlineTableFunction = new(KnownMonikers.TableFunction, ChromeText.IconInlineTableFunction);

    /// <summary>指令碼自己宣告的資料來源：表格加上一份指令碼。</summary>
    /// <remarks>
    /// 影像目錄裡沒有暫存資料表這一項，而與資料庫資料表共用 <c>Table</c> 的症狀是
    /// 清單與預覽都看不出 <c>#Loan</c> 與 <c>dbo.Loan</c> 是兩種不同的東西——
    /// 前者只活在這份文字裡，連線一斷就沒了。
    /// </remarks>
    private static readonly Definition ScriptDataSource =
        new(KnownMonikers.TableScript, ChromeText.IconScriptDataSource);

    private static readonly Definition Database = new(KnownMonikers.Database, CommonText.Database);
    private static readonly Definition GlobalVariable = new(KnownMonikers.GlobalVariable, ChromeText.IconGlobalVariable);
    private static readonly Definition Variable = new(KnownMonikers.LocalVariable, ChromeText.IconVariable);
    private static readonly Definition DataType = new(KnownMonikers.Type, ChromeText.IconDataType);
    private static readonly Definition Parameter = new(KnownMonikers.Parameter, ChromeText.IconParameter);
    private static readonly Definition Synonym = new(KnownMonikers.Synonym, ChromeText.IconSynonym);
    private static readonly Definition Trigger = new(KnownMonikers.Trigger, ChromeText.IconTrigger);
    private static readonly Definition Sequence = new(KnownMonikers.Sequence, ChromeText.IconSequence);
    private static readonly Definition TableType = new(KnownMonikers.UserDefinedTableType, ChromeText.IconTableType);
    private static readonly Definition DatePart = new(KnownMonikers.Calendar, ChromeText.IconDatePart);
    private static readonly Definition TableHint = new(KnownMonikers.IntellisenseKeyword, ChromeText.IconTableHint);
    private static readonly Definition QueryHint = new(KnownMonikers.IntellisenseKeyword, ChromeText.IconQueryHint);
    private static readonly Definition LinkedServer = new(KnownMonikers.LinkedServer, ChromeText.IconLinkedServer);

    /// <remarks>
    /// 影像目錄裡沒有定序這一項，借字母排序那一顆：那正是定序決定的事
    /// （比較與排序的規則），而 <c>IntellisenseKeyword</c> 已經被兩種提示佔著，
    /// 再多一類就分不出誰是誰。
    /// </remarks>
    private static readonly Definition Collation = new(KnownMonikers.SortAscending, ChromeText.IconCollation);
    private static readonly Definition Other = new(KnownMonikers.Ellipsis, CommonText.Other);

    public static ImageElement Ellipsis => Other.Element;

    public static ImageMoniker GetMoniker(SuggestionKind kind) => GetDefinition(kind).Moniker;

    public static ImageMoniker GetMoniker(SqlObjectKind kind) => GetDefinition(kind).Moniker;

    public static ImageElement GetImageElement(SuggestionKind kind) => GetDefinition(kind).Element;

    /// <summary>
    /// 依建議項本身挑圖示。
    /// </summary>
    /// <remarks>
    /// 同一個 <see cref="SuggestionKind"/> 未必是同一種東西，所以帶著來源資料的
    /// 優先：同義字歸入資料表那一類，圖示卻要畫成同義字；資料表變數在
    /// <c>@</c> 之後那份清單裡是 <see cref="SuggestionKind.Variable"/>，
    /// 而它與 <c>FROM</c> 之後的自己是同一張表——分辨的憑據就是它有沒有帶著宣告
    /// （<see cref="SqlScriptTable"/>），讀不出資料行的 <c>@readerId</c> 沒有。
    /// </remarks>
    public static ImageElement GetImageElement(SqlSuggestion suggestion) => suggestion.Tag switch
    {
        SqlObjectInfo objectInfo => GetImageElement(objectInfo.Kind),
        SqlScriptTable => ScriptDataSource.Element,
        _ => GetImageElement(suggestion.Kind)
    };

    public static ImageElement GetImageElement(SqlObjectKind kind) => GetDefinition(kind).Element;

    private static Definition GetDefinition(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Keyword => Keyword,
        SuggestionKind.Snippet => Snippet,
        SuggestionKind.Schema => Schema,
        SuggestionKind.Table => Table,
        SuggestionKind.View => View,
        SuggestionKind.Procedure => Procedure,
        SuggestionKind.Function => ScalarFunction,
        SuggestionKind.Column => Column,
        SuggestionKind.BuiltInFunction => BuiltInFunction,
        SuggestionKind.TableFunction => TableFunction,
        SuggestionKind.ScriptDataSource => ScriptDataSource,
        SuggestionKind.Database => Database,
        SuggestionKind.GlobalVariable => GlobalVariable,
        SuggestionKind.Variable => Variable,
        SuggestionKind.DataType => DataType,
        SuggestionKind.Parameter => Parameter,
        SuggestionKind.Trigger => Trigger,
        SuggestionKind.Sequence => Sequence,
        SuggestionKind.UserDefinedType => TableType,
        SuggestionKind.DatePart => DatePart,
        SuggestionKind.TableHint => TableHint,
        SuggestionKind.QueryHint => QueryHint,
        SuggestionKind.LinkedServer => LinkedServer,
        SuggestionKind.Collation or SuggestionKind.CollationInUse => Collation,
        _ => Unknown
    };

    private static Definition GetDefinition(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Unknown => Unknown,
        SqlObjectKind.Table => Table,
        SqlObjectKind.View => View,
        SqlObjectKind.Procedure => Procedure,
        SqlObjectKind.ScalarFunction => ScalarFunction,
        SqlObjectKind.InlineTableFunction => InlineTableFunction,
        SqlObjectKind.TableValuedFunction => TableFunction,
        SqlObjectKind.Synonym => Synonym,
        SqlObjectKind.Trigger => Trigger,
        SqlObjectKind.Sequence => Sequence,
        SqlObjectKind.TableType => TableType,

        // 指令碼自己宣告的三種共用同一個圖示。它們回答的是同一個問題——這是一張
        // 只活在這份文字裡的表，連線一斷就沒了——而 @rows 曾經跟著區域變數走，
        // 症狀是同一份 FROM 清單裡 #Loan 與 @rows 長得像兩種東西，
        // 使用者得自己記住哪個小老鼠是表。讀不出資料行的 @readerId 不走到這裡：
        // 它做不出 SqlObjectInfo，清單與預覽都仍然是一個變數。
        SqlObjectKind.TemporaryTable
            or SqlObjectKind.CommonTableExpression
            or SqlObjectKind.TableVariable => ScriptDataSource,
        _ => Unknown
    };
}
