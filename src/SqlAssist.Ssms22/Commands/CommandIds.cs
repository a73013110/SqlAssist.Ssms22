using System;

namespace SqlAssist.Ssms22.Commands;

/// <remarks>
/// 這些數值是使用者自訂鍵盤快速鍵的定址方式，不要為了整齊而重新編號——
/// 換掉一個 ID 等於安靜地解除他綁在上面的快速鍵。
/// 移除命令留下的空號（0x0101–0x0104、0x0203–0x0205、0x0207）刻意不回收。
///
/// 同一組數值也寫在 <c>Menus.vsct</c> 的 IDSymbol 與 <c>SqlAssist.registration.json</c>
/// 的按鈕（十進位）裡。三者分歧不會編譯失敗，按鈕就只是按不到，
/// 因此由 <c>tools/Test-CommandTable.ps1</c> 在建置前交叉驗證。
/// </remarks>
internal static class CommandIds
{
    public const string CommandSetString = "4a188946-9364-4f07-af7e-97f3bd7ca7a7";
    public static readonly Guid CommandSet = new(CommandSetString);

    public const int ToggleEnabled = 0x0100;
    public const int ToggleSuggestions = 0x0105;
    public const int ShowDiagnostics = 0x0200;
    public const int OpenSettings = 0x0201;
    public const int RefreshSuggestions = 0x0202;
    public const int ShowObjectStructure = 0x0206;

    /// <summary>
    /// 移至定義；<c>Menus.vsct</c> 把 F12 綁在這一個上。
    /// </summary>
    /// <remarks>
    /// 實測 SSMS 22 並沒有把 F12 綁在 <c>Edit.GoToDefinition</c> 上，所以這條
    /// 鍵繫結才是 F12 真正走的路，不是備援。改動時要連 <c>Menus.vsct</c> 的
    /// <c>KeyBindings</c> 一起看。
    /// </remarks>
    public const int GoToDefinition = 0x020A;

    /// <summary>選單項目，同時也是設定頁上的按鈕（註冊檔寫成十進位的 521）。</summary>
    public const int ManageSnippets = 0x0209;

    /// <summary>設定頁上的按鈕，不出現在選單（註冊檔寫成十進位的 520）。</summary>
    public const int OpenDiagnosticsLog = 0x0208;

    /// <summary>
    /// 結果格線的內部探測，只在「詳細記錄」打開時出現。
    /// </summary>
    /// <remarks>
    /// 原本是一次性的驗證命令，用來證明 <c>Menus.vsct</c> 那個群組真的掛進了
    /// <c>IDM_SQLWB_SQLRESGRID_CONTEXT</c>。留下來的理由是它問的問題沒有別的
    /// 地方問得到：SSMS 換版之後，結果格線的功能會安靜地整組失效——
    /// 沒有例外、沒有記錄，跟 MEF 快取過期同一類。那時候要先知道格線還在不在、
    /// 方法還叫不叫這個名字，才有辦法往下查。
    ///
    /// 報告一律不含儲存格內容，只記型別與是否為 <c>NULL</c>。
    /// </remarks>
    public const int ProbeResultGrid = 0x0300;

    /// <summary>結果格線：把選取範圍寫成 <c>#temp</c> 的建表與灌資料指令碼。</summary>
    public const int ResultGridTempTable = 0x0301;

    /// <summary>結果格線：把選取範圍寫成可以接在 <c>WHERE</c> 後面的條件。</summary>
    public const int ResultGridInPredicate = 0x0302;

    /// <summary>結果格線：每一欄的統計摘要。</summary>
    public const int ResultGridProfile = 0x0303;

    /// <summary>結果格線：這一格的完整內容。</summary>
    public const int ResultGridCell = 0x0304;
}
