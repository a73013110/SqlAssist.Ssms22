using System.Collections.Generic;
using SqlAssist.Core.Completion;

namespace SqlAssist.Core.Keywords;

/// <summary>內建名稱的種類；決定提示的圖示與那一行摘要怎麼寫。</summary>
/// <remarks>
/// 每一個成員對應一份既有的封閉清單，那份清單就是它一行說明的唯一出處：
/// 函式是 <see cref="SqlFunctionCatalog"/>，型別是 <see cref="SqlDataTypeCatalog"/>，
/// 兩種提示與日期部分是 <see cref="SqlArgumentCatalog"/>，全域變數是
/// <see cref="SqlGlobalVariableCatalog"/>。
/// </remarks>
public enum SqlBuiltInKind
{
    /// <summary>內建函式。</summary>
    Function,

    /// <summary>內建資料型別。</summary>
    DataType,

    /// <summary><c>WITH (…)</c> 的資料表提示。</summary>
    TableHint,

    /// <summary><c>OPTION (…)</c> 的查詢提示。</summary>
    QueryHint,

    /// <summary><c>DATEADD</c> 這一族第一個引數的日期部分。</summary>
    DatePart,

    /// <summary><c>@@</c> 開頭的全域變數。</summary>
    GlobalVariable
}

/// <summary>種類換成圖示與那一行種類文字。</summary>
/// <remarks>
/// 放在 Core 而不是各自寫在滑鼠停留提示與浮動視窗裡：兩個表面畫的是同一個圖示與
/// 同一行字，分成兩份的症狀是改了一邊另一邊沒改，而兩邊都看得見。
/// </remarks>
public static class SqlBuiltInKinds
{
    /// <summary>畫哪一個圖示；與建議清單裡同一種東西用的是同一張。</summary>
    public static SuggestionKind ToSuggestionKind(this SqlBuiltInKind kind) => kind switch
    {
        SqlBuiltInKind.Function => SuggestionKind.BuiltInFunction,
        SqlBuiltInKind.TableHint => SuggestionKind.TableHint,
        SqlBuiltInKind.QueryHint => SuggestionKind.QueryHint,
        SqlBuiltInKind.DatePart => SuggestionKind.DatePart,
        SqlBuiltInKind.GlobalVariable => SuggestionKind.GlobalVariable,
        _ => SuggestionKind.DataType
    };

    /// <summary>標題底下那一行種類文字。</summary>
    public static string GetDisplayName(this SqlBuiltInKind kind) => kind switch
    {
        SqlBuiltInKind.Function => "內建函式",
        SqlBuiltInKind.TableHint => "資料表提示",
        SqlBuiltInKind.QueryHint => "查詢提示",
        SqlBuiltInKind.DatePart => "日期部分",
        SqlBuiltInKind.GlobalVariable => "全域變數",
        _ => "內建型別"
    };

    /// <summary>建議項的種類對得回哪一種內建名稱；對不上的種類不走說明面板。</summary>
    /// <remarks>
    /// 建議項自己知道它是什麼，說明面板不必再從文字猜一次——<c>YEAR</c> 在日期部分
    /// 那份清單與內建函式目錄裡各有一筆，猜的話兩邊都說得通。
    /// </remarks>
    public static bool TryFromSuggestionKind(SuggestionKind kind, out SqlBuiltInKind builtIn)
    {
        switch (kind)
        {
            case SuggestionKind.BuiltInFunction:
                builtIn = SqlBuiltInKind.Function;
                return true;
            case SuggestionKind.DataType:
                builtIn = SqlBuiltInKind.DataType;
                return true;
            case SuggestionKind.TableHint:
                builtIn = SqlBuiltInKind.TableHint;
                return true;
            case SuggestionKind.QueryHint:
                builtIn = SqlBuiltInKind.QueryHint;
                return true;
            case SuggestionKind.DatePart:
                builtIn = SqlBuiltInKind.DatePart;
                return true;
            case SuggestionKind.GlobalVariable:
                builtIn = SqlBuiltInKind.GlobalVariable;
                return true;
            default:
                builtIn = SqlBuiltInKind.Function;
                return false;
        }
    }
}

/// <summary>
/// 一個內建名稱的說明：簽章、一行用途與一段範例。
/// </summary>
/// <remarks>
/// 欄位刻意來自不只一個地方。簽章與一行說明留在建議清單已經在用的那幾份目錄裡
/// （見 <see cref="SqlBuiltInKind"/>），再抄一份到 JSON 的症狀是清單與提示各說一套，
/// 而且沒有任何徵兆。JSON 只放那幾份寫不下的東西：用途、陷阱與範例。
/// </remarks>
public sealed class SqlBuiltInDoc
{
    private static readonly SqlBuiltInReference[] NoReferences = new SqlBuiltInReference[0];

    public SqlBuiltInDoc(
        string name,
        SqlBuiltInKind kind,
        string signature,
        string summary,
        string example,
        string docsUrl,
        IReadOnlyList<SqlBuiltInReference>? references = null)
    {
        Name = name;
        Kind = kind;
        Signature = signature;
        Summary = summary;
        Example = example;
        DocsUrl = docsUrl;
        References = references ?? NoReferences;
    }

    /// <summary>標準寫法（一律大寫）。</summary>
    public string Name { get; }

    public SqlBuiltInKind Kind { get; }

    /// <summary>函式的呼叫形狀；其餘種類為空字串。</summary>
    public string Signature { get; }

    /// <summary>一行用途；還沒寫說明的名稱為空字串。</summary>
    public string Summary { get; }

    /// <summary>一段可以直接執行的範例；還沒寫的名稱為空字串。</summary>
    public string Example { get; }

    /// <summary>線上文件位址；沒有時為空字串。</summary>
    public string DocsUrl { get; }

    /// <summary>
    /// 引數查得到哪些值的對照表，例如 <c>CONVERT</c> 的 style 數值。
    /// </summary>
    /// <remarks>
    /// 這些內容一眼看不完，因此不進滑鼠停留提示——提示視窗不能捲動也不能選取。
    /// 它們是浮動預覽那一側的內容，與物件結構共用同一個視窗。
    /// </remarks>
    public IReadOnlyList<SqlBuiltInReference> References { get; }

    /// <summary>有沒有值得開一個視窗慢慢看的東西。</summary>
    public bool HasReferences => References.Count > 0;

    /// <summary>
    /// 值不值得為它單獨開一個浮動視窗。
    /// </summary>
    /// <remarks>
    /// 對照表或範例其中之一。兩者都沒有時視窗裡只剩一個標題與一行說明，而那一行
    /// 使用者已經在提示或說明面板上看過了。Ctrl+F12 與建議清單那兩條入口問的是同一
    /// 件事，寫成兩份的症狀是同一個名稱在兩條入口上開得起來的不一樣。
    ///
    /// 滑鼠停留提示的「開啟完整說明」用的是更嚴的一條（只看對照表）：範例就印在那個
    /// 提示裡，為它再開一個視窗等於把使用者眼前的東西再放大一次。
    /// </remarks>
    public bool DeservesWindow => HasReferences || Example.Length > 0;

    /// <summary>除了名稱以外還有東西可說嗎。</summary>
    /// <remarks>
    /// 只有名稱的話，提示等於把使用者停在上面的那個字再唸一次，不值得一個視窗。
    /// </remarks>
    public bool HasContent =>
        Signature.Length > 0 || Summary.Length > 0 || Example.Length > 0;
}
