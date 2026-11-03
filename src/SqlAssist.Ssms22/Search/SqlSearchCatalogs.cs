using System;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Connections;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// SQL Search 的一筆結果要用哪一份目錄：清單、預覽與移至定義<b>唯一</b>的出處。
/// </summary>
/// <remarks>
/// 範圍選的是哪一台由 <see cref="SqlCatalogSelection"/> 回答，這裡只多一條規則：
/// <b>一筆結果點下去時，伺服器照那一筆的 <see cref="SqlSearchOrigin"/>，不照現在的範圍。</b>
/// 清單比範圍活得久：換了伺服器之後，舊的那幾列仍然指向上一台，而現在的範圍答的是換過之後
/// 那一台。所以導航、沿用連線與目錄查詢三條路都把那一筆的伺服器帶進來問，
/// 範圍不在那一台時照實拒絕，不拿現在這一台同名、同號的東西代答。
/// 純判斷那一半（在樹上挑哪一台）在 <c>SqlSearchCatalogs.Servers.cs</c>，零 VS 相依。
/// </remarks>
internal sealed partial class SqlSearchCatalogs
{
    internal SqlSearchCatalogs(SqlCatalogSelection selection) =>
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));

    /// <summary>範圍列選定的那一台；與工具窗的範圍列共用同一份。</summary>
    public SqlCatalogSelection Selection { get; }

    /// <summary>
    /// 這一筆結果與作用中的查詢視窗落在同一台伺服器上。
    /// </summary>
    /// <remarks>
    /// 這是移至定義「新視窗沿用得到的那條連線對不對」的問題，與範圍選了哪一台無關：
    /// 使用者從物件總管選的，十之八九正是查詢視窗已經連著的那一台。
    ///
    /// 問的是<b>那一筆</b>的伺服器，不是這一輪範圍的：清單上的舊列可能來自上一台。
    ///
    /// 比對沿用 <see cref="SqlCatalogSelection.IsSameServer(string?, string?)"/>，與下拉「同一台不列兩次」及
    /// <see cref="ResolveExplorerServer"/> 同一份規則：伺服器名稱的寫法只有一份，
    /// 在這裡另寫一套的症狀是下拉說同一台、這一支說不同台。
    ///
    /// <b>只問伺服器，不問資料庫。</b>新視窗沿用的是連線，而一份結果清單本來就跨資料庫；
    /// 指令碼自己帶著它該去的那一個。要求連同資料庫一致等於把跨資料庫的結果整批擋掉。
    /// </remarks>
    public bool SharesActiveEditorServer(SqlSearchOrigin origin)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        return SqlCatalogSelection.IsSameServer(origin.ServerName, Selection.ActiveEditorServerName());
    }

    /// <summary>
    /// 這一筆結果在物件總管的哪一台上；樹上沒有那一台時回傳 null。
    /// </summary>
    /// <remarks>
    /// 照那一筆的伺服器找，不照現在的範圍：範圍換過之後，舊列的伺服器仍然是上一台。
    /// 找不到時<b>禁止</b>拿樹上任何一台頂替（包括選定的那一台）：頂替的症狀是導航跳到
    /// 另一台伺服器上同名的物件，而畫面上看起來完全正常。挑法見 <see cref="FindOnTree"/>。
    ///
    /// 只在使用者按下導航那一刻呼叫：列伺服器會取用物件總管服務，而那一步會把它的視窗
    /// 叫出來，理由見 <see cref="SsmsObjectExplorer"/>。
    /// </remarks>
    public SsmsObjectExplorerServer? ResolveExplorerServer(SqlSearchOrigin origin)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        return FindOnTree(Selection.Server, Selection.ListServers(), origin);
    }

    /// <summary>
    /// 在 <paramref name="origin"/> 那一台上的目錄；範圍已經不在那一台時回傳 null，
    /// 並以 <paramref name="elsewhere"/> 說出是這個原因。
    /// </summary>
    /// <remarks>
    /// 目錄只有範圍那一份，所以這一支<b>拒絕</b>而不去別處找：拿範圍那一台的目錄回答另一台
    /// 的 <c>object_id</c>，問到的是剛好同號的另一個物件（或它的父物件），而那份答案看起來
    /// 完全正常。<paramref name="elsewhere"/> 與「連不上」分開說，兩句的下一步不同：
    /// 一句是重新搜尋，另一句是去看連線。
    /// </remarks>
    public SqlMetadataCatalog? ResolveOn(SqlSearchOrigin origin, out bool elsewhere)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (origin is null) throw new ArgumentNullException(nameof(origin));

        // 比的是這份目錄自己那一台，不是另外問來的名字：兩者分開問的話，名字與目錄
        // 可能不是同一次解析的，同號的另一個物件就會混進來。
        var scope = Selection.ResolveConnection();
        elsewhere = scope is not null && !SqlCatalogSelection.IsSameServer(origin.ServerName, scope.ServerName);
        return elsewhere ? null : scope?.Catalog;
    }

    /// <summary>
    /// 這一筆結果指向的那個物件要用哪一份目錄；範圍已經不在它那一台時回傳 null。
    /// </summary>
    /// <remarks>
    /// 伺服器先照 <see cref="ResolveOn"/> 確認，再換資料庫。換目錄的規則只有
    /// <see cref="SqlMetadataCatalogRegistry.ScopeTo(SqlMetadataCatalog?, SqlObjectInfo?)"/>
    /// 一份，與查詢視窗那條路徑共用：一份結果清單本來就跨資料庫，而
    /// <c>object_id</c> 只在它自己那個資料庫裡唯一。
    /// </remarks>
    public SqlMetadataCatalog? ResolveFor(SqlObjectInfo objectInfo, SqlSearchOrigin origin, out bool elsewhere)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (objectInfo is null) throw new ArgumentNullException(nameof(objectInfo));

        return SqlMetadataCatalogRegistry.Default.ScopeTo(ResolveOn(origin, out elsewhere), objectInfo);
    }
}
