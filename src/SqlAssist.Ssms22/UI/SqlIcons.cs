using System;
using System.Collections.Immutable;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Text.Adornments;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Completion;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlIcons
{
    private sealed class Definition
    {
        private readonly SqlLanguageCache<ImageElement> _elements;

        public Definition(ImageMoniker moniker, Func<string> automationName)
        {
            // 同一個 moniker 同時供 WPF 與編輯器 adornment 使用，避免兩份對照漂移。
            Moniker = moniker;
            // ImageElement 不可變，朗讀名稱建立時就定了，所以依語言各留一份；同一語言取到的是同一個實體，
            // 補全篩選鈕靠實體比對也不受影響。
            _elements = new SqlLanguageCache<ImageElement>(_ => new ImageElement(moniker.ToImageId(), automationName()));
        }

        public ImageMoniker Moniker { get; }

        public ImageElement Element => _elements.Current;
    }

    // 依語意快取不可變資料；CrispImage 屬於各自的視覺樹，不在這裡共用。
    private static readonly Definition Unknown = new(KnownMonikers.UnknownMember, () => SqlKindText.Unknown);
    private static readonly Definition Keyword = new(KnownMonikers.IntellisenseKeyword, () => SqlKindText.Keyword);
    private static readonly Definition Snippet = new(KnownMonikers.Snippet, () => SqlKindText.Snippet);
    private static readonly Definition Schema = new(KnownMonikers.Schema, () => SqlKindText.Schema);
    private static readonly Definition Table = new(KnownMonikers.Table, () => SqlKindText.Table);
    private static readonly Definition View = new(KnownMonikers.View, () => SqlKindText.View);
    private static readonly Definition Procedure = new(KnownMonikers.StoredProcedure, () => SqlKindText.Procedure);
    private static readonly Definition ScalarFunction = new(KnownMonikers.ScalarFunction, () => SqlKindText.ScalarFunction);
    private static readonly Definition Column = new(KnownMonikers.Column, () => SqlKindText.Column);
    private static readonly Definition BuiltInFunction = new(KnownMonikers.Method, () => SqlKindText.BuiltInFunction);
    private static readonly Definition TableFunction = new(KnownMonikers.TableFunction, () => SqlKindText.TableFunction);
    private static readonly Definition InlineTableFunction = new(KnownMonikers.TableFunction, () => SqlKindText.InlineTableFunction);

    /// <summary>指令碼自己宣告的資料來源：表格加上一份指令碼。</summary>
    /// <remarks>
    /// 影像目錄裡沒有暫存資料表這一項，而與資料庫資料表共用 <c>Table</c> 的症狀是
    /// 清單與預覽都看不出 <c>#Loan</c> 與 <c>dbo.Loan</c> 是兩種不同的東西——
    /// 前者只活在這份文字裡，連線一斷就沒了。
    /// </remarks>
    private static readonly Definition ScriptDataSource =
        new(KnownMonikers.TableScript, () => SqlKindText.ScriptDataSource);

    private static readonly Definition Database = new(KnownMonikers.Database, () => SqlKindText.Database);
    private static readonly Definition GlobalVariable = new(KnownMonikers.GlobalVariable, () => SqlKindText.GlobalVariable);
    private static readonly Definition Variable = new(KnownMonikers.LocalVariable, () => SqlKindText.LocalVariable);
    private static readonly Definition DataType = new(KnownMonikers.Type, () => SqlKindText.DataType);
    private static readonly Definition Parameter = new(KnownMonikers.Parameter, () => SqlKindText.Parameter);
    private static readonly Definition Synonym = new(KnownMonikers.Synonym, () => SqlKindText.Synonym);
    private static readonly Definition Trigger = new(KnownMonikers.Trigger, () => SqlKindText.Trigger);
    private static readonly Definition Sequence = new(KnownMonikers.Sequence, () => SqlKindText.Sequence);
    private static readonly Definition TableType = new(KnownMonikers.UserDefinedTableType, () => SqlKindText.TableType);
    private static readonly Definition DatePart = new(KnownMonikers.Calendar, () => SqlKindText.DatePart);
    private static readonly Definition TableHint = new(KnownMonikers.IntellisenseKeyword, () => SqlKindText.TableHint);
    private static readonly Definition QueryHint = new(KnownMonikers.IntellisenseKeyword, () => SqlKindText.QueryHint);
    private static readonly Definition LinkedServer = new(KnownMonikers.LinkedServer, () => SqlKindText.LinkedServer);

    /// <remarks>
    /// 影像目錄裡沒有定序這一項，借字母排序那一顆：那正是定序決定的事
    /// （比較與排序的規則），而 <c>IntellisenseKeyword</c> 已經被兩種提示佔著，
    /// 再多一類就分不出誰是誰。
    /// </remarks>
    private static readonly Definition Collation = new(KnownMonikers.SortAscending, () => SqlKindText.Collation);
    private static readonly Definition SchemaOrDatabase =
        new(KnownMonikers.DatabaseSchema, () => CompletionText.FilterSchemasAndDatabases);

    // 建議清單列尾的例外標記。兩個警示用有色的狀態形狀，其餘用單色線條，讓一眼看得出輕重；
    // 系統物件借鎖頭，與物件總管替系統物件疊的那一顆同義。
    private static readonly Definition DestructiveMark = new(KnownMonikers.StatusWarning, () => CompletionText.MarkDestructive);
    private static readonly Definition DeprecatedMark = new(KnownMonikers.StrikeThrough, () => CompletionText.MarkDeprecated);
    private static readonly Definition SystemObjectMark = new(KnownMonikers.Lock, () => CompletionText.MarkSystemObject);
    private static readonly Definition RecentlyUsedMark = new(KnownMonikers.History, () => CompletionText.MarkRecentlyUsed);

    /// <remarks>
    /// 以旗標值當索引，每一種組合一份不可變陣列，依語言各留一份：<c>EXEC |</c> 的清單
    /// 有上千列系統程序，逐列配置陣列就是每一次開清單多上千次配置。
    /// </remarks>
    private static readonly SqlLanguageCache<ImmutableArray<ImageElement>[]> MarkElements = new(_ =>
    {
        var combinations = new ImmutableArray<ImageElement>[(int)SuggestionMarks.All + 1];

        for (var index = 0; index < combinations.Length; index++)
        {
            var builder = ImmutableArray.CreateBuilder<ImageElement>();

            for (var bit = 1; bit <= (int)SuggestionMarks.All; bit <<= 1)
            {
                if ((index & bit) != 0)
                {
                    builder.Add(GetMarkDefinition((SuggestionMark)bit).Element);
                }
            }

            combinations[index] = builder.ToImmutable();
        }

        return combinations;
    });

    /// <summary>篩選列上分類鈕的圖示：取那一類最具代表性的種類。</summary>
    /// <remarks>
    /// 結構描述與資料庫合成一類，只畫其中一種就只說了一半；影像目錄剛好有一顆資料庫疊著
    /// 結構描述的圖示。
    /// </remarks>
    public static ImageElement GetImageElement(SuggestionCategory category) => (category switch
    {
        SuggestionCategory.Column => Column,
        SuggestionCategory.Table => Table,
        SuggestionCategory.View => View,
        SuggestionCategory.Procedure => Procedure,
        SuggestionCategory.ScalarFunction => ScalarFunction,
        SuggestionCategory.TableFunction => TableFunction,
        SuggestionCategory.Sequence => Sequence,
        SuggestionCategory.BuiltInFunction => BuiltInFunction,
        SuggestionCategory.Keyword => Keyword,
        SuggestionCategory.Snippet => Snippet,
        SuggestionCategory.SchemaOrDatabase => SchemaOrDatabase,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    }).Element;

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

    /// <summary>建議清單列尾的標記圖示；沒有標記時是空陣列，平台就不畫。</summary>
    public static ImmutableArray<ImageElement> GetImageElements(SuggestionMark marks) =>
        marks == SuggestionMark.None
            ? ImmutableArray<ImageElement>.Empty
            : MarkElements.Current[(int)marks];

    private static Definition GetMarkDefinition(SuggestionMark mark) => mark switch
    {
        SuggestionMark.Destructive => DestructiveMark,
        SuggestionMark.Deprecated => DeprecatedMark,
        SuggestionMark.SystemObject => SystemObjectMark,
        SuggestionMark.RecentlyUsed => RecentlyUsedMark,
        _ => throw new ArgumentOutOfRangeException(nameof(mark), mark, null)
    };

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
