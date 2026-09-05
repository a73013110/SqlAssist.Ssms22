using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.UI;

internal static class SqlIcons
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
    private static readonly Definition Unknown = new(KnownMonikers.UnknownMember, "未知物件");
    private static readonly Definition Keyword = new(KnownMonikers.IntellisenseKeyword, "關鍵字");
    private static readonly Definition Snippet = new(KnownMonikers.Snippet, "程式碼片段");
    private static readonly Definition Schema = new(KnownMonikers.Schema, "結構描述");
    private static readonly Definition Table = new(KnownMonikers.Table, "資料表");
    private static readonly Definition View = new(KnownMonikers.View, "檢視");
    private static readonly Definition Procedure = new(KnownMonikers.StoredProcedure, "預存程序");
    private static readonly Definition ScalarFunction = new(KnownMonikers.ScalarFunction, "純量函式");
    private static readonly Definition Column = new(KnownMonikers.Column, "欄位");
    private static readonly Definition BuiltInFunction = new(KnownMonikers.Method, "內建函式");
    private static readonly Definition TableFunction = new(KnownMonikers.TableFunction, "資料表值函式");
    private static readonly Definition InlineTableFunction = new(KnownMonikers.TableFunction, "內嵌資料表值函式");
    private static readonly Definition ScriptDataSource = new(KnownMonikers.Table, "指令碼資料來源");
    private static readonly Definition Database = new(KnownMonikers.Database, "資料庫");
    private static readonly Definition GlobalVariable = new(KnownMonikers.GlobalVariable, "全域變數");
    private static readonly Definition Variable = new(KnownMonikers.LocalVariable, "區域變數");
    private static readonly Definition DataType = new(KnownMonikers.Type, "資料型別");
    private static readonly Definition Parameter = new(KnownMonikers.Parameter, "參數");
    private static readonly Definition Synonym = new(KnownMonikers.Synonym, "同義字");
    private static readonly Definition Trigger = new(KnownMonikers.Trigger, "觸發程序");
    private static readonly Definition Sequence = new(KnownMonikers.Sequence, "序列");
    private static readonly Definition TableType = new(KnownMonikers.UserDefinedTableType, "使用者自訂資料表型別");
    private static readonly Definition DatePart = new(KnownMonikers.Calendar, "日期部分");
    private static readonly Definition TableHint = new(KnownMonikers.IntellisenseKeyword, "資料表提示");
    private static readonly Definition QueryHint = new(KnownMonikers.IntellisenseKeyword, "查詢提示");
    private static readonly Definition LinkedServer = new(KnownMonikers.LinkedServer, "連結伺服器");
    private static readonly Definition Other = new(KnownMonikers.Ellipsis, "其他");

    public static ImageElement Ellipsis => Other.Element;

    public static ImageMoniker GetMoniker(SuggestionKind kind) => GetDefinition(kind).Moniker;

    public static ImageMoniker GetMoniker(SqlObjectKind kind) => GetDefinition(kind).Moniker;

    public static ImageElement GetImageElement(SuggestionKind kind) => GetDefinition(kind).Element;

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

        // 指令碼自己宣告的三種。暫存資料表與建議清單裡的 ScriptDataSource 同一個
        // 圖示，資料表變數跟著區域變數走——它在使用者眼裡就是一個變數。
        SqlObjectKind.TemporaryTable => ScriptDataSource,
        SqlObjectKind.TableVariable => Variable,
        SqlObjectKind.CommonTableExpression => View,
        _ => Unknown
    };
}
