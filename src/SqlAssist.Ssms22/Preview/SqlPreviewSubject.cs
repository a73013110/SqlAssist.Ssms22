using System;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Snippets;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 浮動預覽這一次要畫的是什麼。
/// </summary>
/// <remarks>
/// 四條入口（建議清單的向右鍵、停在同一項夠久的自動展開、滑鼠停留提示的連結、
/// Ctrl+F12）只有觸發方式不同，之後做的事完全一樣：解析出一個主體，再把它畫出來。
/// 中間這一格因此只有一個型別，而不是「物件一個欄位、內建說明另一個欄位」——
/// 分成兩份的話，對帳、換內容、同一項不重畫與收掉舊目標這幾處都要各長一個分支，
/// 而漏掉其中一個分支不會編譯失敗，只會讓畫面停在上一個東西。三種主體互斥，
/// 名稱（<see cref="Label"/>）與「是不是同一個」（<see cref="IsSame"/>）都只在這裡寫一次。
/// </remarks>
internal sealed class SqlPreviewSubject
{
    private SqlPreviewSubject(
        SqlObjectInfo? objectInfo,
        SqlBuiltInDoc? builtIn,
        SqlSnippet? snippet,
        SqlObjectStructure? script)
    {
        Object = objectInfo;
        BuiltIn = builtIn;
        Snippet = snippet;
        Script = script;
    }

    /// <summary>要向中繼資料要結構的資料庫物件；內建說明為 null。</summary>
    public SqlObjectInfo? Object { get; }

    /// <summary>
    /// 隨組件發布的內建名稱說明；資料庫物件為 null。
    /// </summary>
    /// <remarks>
    /// 這一支沒有查詢也沒有分層載入：畫完就結束，不起節流計時器，也不需要連線。
    /// </remarks>
    public SqlBuiltInDoc? BuiltIn { get; }

    /// <summary>建議清單裡選到的片段；說明面板印不完時才走到這裡。其餘一律 null。</summary>
    public SqlSnippet? Snippet { get; }

    /// <summary>膠囊與工具窗標題上寫的名稱：物件的完整名稱、內建名稱或片段標題。</summary>
    public string Label => Object?.QualifiedName ?? BuiltIn?.Name ?? Snippet?.Title ?? string.Empty;

    /// <summary>
    /// 指令碼自己宣告的物件已經讀好的結構；其餘一律 null。
    /// </summary>
    /// <remarks>
    /// 與物件綁在同一個主體上而不是另一個欄位：兩者分開存放時，換了目標卻沒有一起換
    /// 的症狀是畫面停在上一個宣告的資料行。停留提示與 Ctrl+F12 在定位那一步就讀好了，
    /// 建議清單那條入口只知道名稱，真的要畫時才向名冊要——所以這一格是後填的。
    /// </remarks>
    public SqlObjectStructure? Script { get; set; }

    public static SqlPreviewSubject ForObject(
        SqlObjectInfo objectInfo,
        SqlObjectStructure? script = null) => new(objectInfo, null, null, script);

    public static SqlPreviewSubject ForBuiltIn(SqlBuiltInDoc doc) => new(null, doc, null, null);

    public static SqlPreviewSubject ForSnippet(SqlSnippet snippet) => new(null, null, snippet, null);

    /// <summary>
    /// 兩次對帳指的是不是同一個東西。
    /// </summary>
    /// <remarks>
    /// 平台一次換選取會從方向鍵命令、說明 callback 與 ItemsUpdated 分別通知一次，
    /// 多數輪次解析出來的是同一個東西。認錯的症狀是使用者每按一次方向鍵眼前閃一下，
    /// 而且剛送出的查詢會被取消再送一次。
    /// </remarks>
    public static bool IsSame(SqlPreviewSubject? left, SqlPreviewSubject? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        // 片段清單不可變、存檔時整份換新（SqlSnippetLibrary），同一筆就是同一個參考；
        // 改過內容的那一筆是新的參考，正好要重畫。
        return IsSameObject(left.Object, right.Object) &&
               IsSameDoc(left.BuiltIn, right.BuiltIn) &&
               ReferenceEquals(left.Snippet, right.Snippet);
    }

    /// <summary>
    /// 兩個描述指的是不是同一個資料庫物件。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="SqlObjectInfo.ObjectId"/> 而不是參考相等：中繼資料快取、詳細資料
    /// 與結構查詢從頭到尾都以 ObjectId 當識別，這裡跟著同一套才不會出現「快取認為是
    /// 同一個、預覽認為換人了」。參考相等目前剛好成立，但那只是因為兩次都讀到同一個
    /// CompletionItem，換一條入口（停留提示、工具選單）就不成立。
    ///
    /// 指令碼自己宣告的物件沒有 <c>object_id</c>，一律是 0；只比編號會把 <c>#a</c>
    /// 與 <c>#b</c> 當成同一個，症狀是換了目標畫面卻還停在上一份結構。
    /// </remarks>
    public static bool IsSameObject(SqlObjectInfo? left, SqlObjectInfo? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.ObjectId == right.ObjectId &&
              (left.ObjectId != 0 ||
               string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 兩份說明指的是不是同一個內建名稱。
    /// </summary>
    /// <remarks>
    /// 種類要一起比：<c>CHAR</c> 在函式與型別兩份目錄裡各有一筆，只比名稱會讓
    /// 從函式換到型別的那一次被當成「同一項」而不重畫。
    /// </remarks>
    private static bool IsSameDoc(SqlBuiltInDoc? left, SqlBuiltInDoc? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.Kind == right.Kind &&
              string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
}
