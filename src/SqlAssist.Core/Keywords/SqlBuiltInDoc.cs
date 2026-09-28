using System;
using System.Collections.Generic;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Keywords;

/// <summary>內建名稱的種類；決定提示的圖示與那一行摘要怎麼寫。</summary>
/// <remarks>
/// 每一個成員對應一份既有的封閉清單，那份清單就是它一行說明的唯一出處：
/// 函式是 <see cref="SqlFunctionCatalog"/>，型別是 <see cref="SqlDataTypeCatalog"/>，
/// 兩種提示與日期部分是 <see cref="SqlArgumentCatalog"/>，全域變數是
/// <see cref="SqlGlobalVariableCatalog"/>。系統程序與語句沒有那樣一份封閉清單——
/// 兩者的簽章與一行說明都寫在 <c>Keywords/BuiltInDocs/system-procedures.json</c>、
/// <c>statements.json</c> 裡，JSON 本身就是唯一出處。
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
    GlobalVariable,

    /// <summary>系統預存程序（<c>sp_executesql</c>、<c>sp_help</c>…）。</summary>
    SystemProcedure,

    /// <summary>只能整句寫、不是運算式的語句（<c>EXEC</c>、<c>THROW</c>、<c>MERGE</c>…）。</summary>
    Statement
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
        SqlBuiltInKind.SystemProcedure => SuggestionKind.Procedure,
        SqlBuiltInKind.Statement => SuggestionKind.Keyword,
        _ => SuggestionKind.DataType
    };

    /// <summary>標題底下那一行種類文字。</summary>
    public static string GetDisplayName(this SqlBuiltInKind kind) => kind switch
    {
        SqlBuiltInKind.Function => SqlKindText.BuiltInFunction,
        SqlBuiltInKind.TableHint => SqlKindText.TableHint,
        SqlBuiltInKind.QueryHint => SqlKindText.QueryHint,
        SqlBuiltInKind.DatePart => SqlKindText.DatePart,
        SqlBuiltInKind.GlobalVariable => SqlKindText.GlobalVariable,
        SqlBuiltInKind.SystemProcedure => SqlKindText.SystemProcedure,
        SqlBuiltInKind.Statement => SqlKindText.Statement,
        _ => KeywordText.KindDataType
    };

    /// <summary>建議項的種類對得回哪一種內建名稱；對不上的種類不走說明面板。</summary>
    /// <remarks>
    /// 建議項自己知道它是什麼，說明面板不必再從文字猜一次——<c>YEAR</c> 在日期部分
    /// 那份清單與內建函式目錄裡各有一筆，猜的話兩邊都說得通。
    ///
    /// 刻意不接 <see cref="SuggestionKind.Procedure"/> 與 <see cref="SuggestionKind.Keyword"/>：
    /// 前者同時涵蓋使用者自訂與系統預存程序，種類本身分不出是哪一種；後者涵蓋所有關鍵字，
    /// 不是只有 <see cref="SqlBuiltInKind.Statement"/> 收的那幾個，而且同一個字是不是語句要看位置。
    /// 系統程序看系統物件標記、語句問 <see cref="SqlStatementCandidates"/>，不透過這一支反推。
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

    /// <summary>
    /// 這份說明的判斷順序是不是該排在物件解析之前。
    /// </summary>
    /// <remarks>
    /// 單一規則：系統程序與語句為真，其餘為假。SQL Server 解析 <c>sp_</c> 開頭的名稱
    /// 本來就先找系統那一份，說明也不必連線才答得出來；函式與型別維持「物件解析優先」，
    /// <c>SELECT * FROM Format</c> 停在 <c>Format</c> 上要的是那張表，不是同名的內建函式。
    ///
    /// 四條入口（<c>SqlObjectNavigation</c>、<c>SqlQuickInfoSource</c>、<c>SqlClickTarget</c>、
    /// <c>SqlSuggestionTarget</c>）都問這一支而不是各自寫一份判斷；平台層排順序的唯一出處是
    /// <c>Ssms22/Editor/SqlBuiltInObjectResolution</c>，先呼叫這支決定要不要跳過物件解析，
    /// 不是另外重寫一套「先物件後說明」的判斷。
    /// </remarks>
    public static bool PrecedesObjectResolution(this SqlBuiltInKind kind) =>
        kind == SqlBuiltInKind.SystemProcedure || kind == SqlBuiltInKind.Statement;
}

/// <summary>一段可以直接執行的範例。</summary>
/// <remarks>
/// 一筆內建名稱的說明可以有 2–5 段，各自可以直接貼進查詢視窗執行，不依賴使用者的
/// 資料表（見 <c>docs/builtin-help.md</c>）。<see cref="Id"/> 只給覆蓋檔引用，不顯示。
/// </remarks>
public sealed class SqlBuiltInExample
{
    public SqlBuiltInExample(string id, string title, string sql)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>覆蓋檔用來指到這一段的編號；同一筆說明裡不重複。</summary>
    public string Id { get; }

    /// <summary>這一段示範的是什麼，例如「漏寫 OUTPUT 的陷阱」；不超過 20 個字。</summary>
    public string Title { get; }

    /// <summary>可以直接執行的一段 T-SQL。</summary>
    public string Sql { get; }
}

/// <summary>
/// 一個內建名稱的說明：簽章、參數、有哪幾種寫法與多段可以直接執行的範例。
/// </summary>
/// <remarks>
/// 欄位刻意來自不只一個地方。函式與型別的簽章與一行說明留在建議清單已經在用的那幾份
/// 目錄裡（見 <see cref="SqlBuiltInKind"/>），再抄一份到 JSON 的症狀是清單與提示各說一套，
/// 而且沒有任何徵兆；系統程序與語句沒有那樣一份目錄，<see cref="Signature"/> 由 JSON 的
/// <c>signature</c> 欄位當唯一出處。JSON 只放那幾份寫不下的東西：用途、陷阱、範例與寫法對照表。
/// </remarks>
public sealed class SqlBuiltInDoc
{
    private static readonly SqlBuiltInReference[] NoReferences = Array.Empty<SqlBuiltInReference>();
    private static readonly SqlBuiltInExample[] NoExamples = Array.Empty<SqlBuiltInExample>();

    public SqlBuiltInDoc(
        string name,
        SqlBuiltInKind kind,
        string signature,
        string summary,
        IReadOnlyList<SqlBuiltInExample> examples,
        string docsUrl,
        IReadOnlyList<SqlBuiltInReference>? references = null)
    {
        Name = name;
        Kind = kind;
        Signature = signature;
        Summary = summary;
        Examples = examples ?? NoExamples;
        DocsUrl = docsUrl;
        References = references ?? NoReferences;
    }

    /// <summary>標準寫法（一律大寫）。</summary>
    public string Name { get; }

    public SqlBuiltInKind Kind { get; }

    /// <summary>
    /// 函式的呼叫形狀，或系統程序／語句的寫法；型別、提示、日期部分與全域變數為空字串。
    /// </summary>
    public string Signature { get; }

    /// <summary>一行用途；還沒寫說明的名稱為空字串。</summary>
    public string Summary { get; }

    /// <summary>
    /// 2–5 段可以直接執行的範例，依撰寫順序排列；還沒寫的名稱是空集合。
    /// </summary>
    /// <remarks>
    /// 滑鼠停留提示只印第一段（<c>Examples[0]</c>）；浮動預覽的範例分頁由
    /// <see cref="SqlBuiltInExampleText.Combine(IReadOnlyList{SqlBuiltInExample})"/>
    /// 把每一段接成一份——段落之間插一行 <c>GO</c> 再空一行，讓整頁複製後可以分批執行，
    /// 見 <c>docs/builtin-help.md</c>。
    /// </remarks>
    public IReadOnlyList<SqlBuiltInExample> Examples { get; }

    /// <summary>線上文件位址；沒有時為空字串。</summary>
    public string DocsUrl { get; }

    /// <summary>
    /// 引數查得到哪些值、或有哪幾種寫法的對照表，例如 <c>CONVERT</c> 的 style 數值、
    /// 系統程序的「參數」與「寫法」兩張表。
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
    /// </remarks>
    public bool DeservesWindow => HasReferences || Examples.Count > 0;

    /// <summary>
    /// 滑鼠停留提示的「開啟完整說明」要不要出現。
    /// </summary>
    /// <remarks>
    /// 比 <see cref="DeservesWindow"/> 更嚴：提示已經印出第一段範例
    /// （<c>Examples[0]</c>），只有對照表、或範例還有第二段以後才值得再開一個視窗——
    /// 只有一段範例時，視窗裡的東西使用者已經在提示上看過了，蓋上去等於把它再放大一次。
    /// 這是「開啟完整說明」判斷唯一的一處，滑鼠停留提示與浮動預覽兩個表面都問它，
    /// 不要各自重寫一次「有沒有超過一段」。
    /// </remarks>
    public bool HasExpandedContent => HasReferences || Examples.Count > 1;

    /// <summary>除了名稱以外還有東西可說嗎。</summary>
    /// <remarks>
    /// 只有名稱的話，提示等於把使用者停在上面的那個字再唸一次，不值得一個視窗。
    /// </remarks>
    public bool HasContent =>
        Signature.Length > 0 || Summary.Length > 0 || Examples.Count > 0;
}
