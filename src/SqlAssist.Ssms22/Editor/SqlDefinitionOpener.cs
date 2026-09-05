using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Diagnostics;
using SqlAssist.Core.Parsing;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 把游標處的物件定義開進一個新的查詢視窗。
/// </summary>
/// <remarks>
/// F12 與「工具 → SqlAssist → 移至定義」共用這一份。兩條入口只差在游標從哪裡來，
/// 「解析 → 取結構 → 組指令碼 → 開視窗 → 寫入」這五步一模一樣；各寫一份的下場是
/// 其中一份少了重入防護或少了一句失敗訊息。
///
/// <b>執行緒分工</b>是這個類別的重點：
/// <list type="bullet">
/// <item>UI 執行緒（按鍵路徑）——只做純文字判斷，決定這一次要不要接手。</item>
/// <item>背景——整份快照取文字、解析物件、查結構、組指令碼。</item>
/// <item>UI 執行緒——建立查詢視窗並寫入。</item>
/// </list>
///
/// 與滑鼠停留提示的差別寫在 <see cref="SqlObjectLocator"/>：提示在滑鼠軌跡上，
/// 只讀快取；這裡是使用者主動按的，等得起一次查詢。但「等得起」不等於可以在
/// UI 執行緒上等——那會變成按下 F12 之後整個 SSMS 停住。
/// </remarks>
internal sealed class SqlDefinitionOpener
{
    private const string OperationName = "物件定義";

    private readonly ITextView _textView;
    private readonly SqlMetadataService _metadataService;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>已經有一次在跑；連按 F12 不該開出兩個視窗。</summary>
    private int _inFlight;

    public SqlDefinitionOpener(
        ITextView textView,
        SqlMetadataService metadataService,
        IServiceProvider serviceProvider)
    {
        _textView = textView ?? throw new ArgumentNullException(nameof(textView));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// 開始把游標處的物件開進新視窗。
    /// </summary>
    /// <remarks>
    /// 這裡的閘門只看文字，不碰中繼資料也不碰連線——它跑在按鍵路徑上。
    /// 而且只看游標<b>所在的那一行</b>：整份快照取文字是一次完整的字串複製，
    /// 幾千行的指令碼就是幾百 KB，在 F12 按下去的那一瞬間做等於畫面停一下。
    ///
    /// 只看一行會比完整文字寬鬆（跨行的區塊註解裡也會判成識別字），那是刻意的：
    /// 這一關只決定「值不值得往背景送」，真正的答案由背景用完整文字重算一次。
    /// 寬鬆的代價是多一次背景查詢，嚴格的代價是 F12 在該有反應的地方沒反應。
    /// </remarks>
    /// <returns>本擴充接手了為 true；false 代表這一次讓 F12 照常落回平台。</returns>
    public bool TryBegin(SnapshotPoint caret)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!SqlAssistSettingsStore.Current.Enabled)
        {
            return false;
        }

        var line = caret.GetContainingLine();

        if (SqlIdentifierScanner.FindAt(line.GetText(), caret.Position - line.Start.Position) is not { } reference)
        {
            return false;
        }

        // 換人之前先確認沒有另一次在跑。回傳 true 而不是 false：使用者確實按在
        // 一個識別字上，讓 F12 落回平台只會在上一次還沒開完時多出一個奇怪的行為。
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            SqlAssistDiagnostics.Write($"上一次的{OperationName}還沒開完，忽略這一次");
            return true;
        }

        SqlAssistStatusBar.Show(_serviceProvider, $"正在取得 {reference.Name} 的定義…");

        // 刻意不走 SqlAssistPlatformGuard.Begin：那一族的意思是「這一輪安靜地什麼都
        // 不做」，但使用者是自己按下 F12 的，什麼都沒發生等於故障。失敗一律說得出原因，
        // 而收斂與回報都在 OpenAsync 裡。
        _ = OpenAsync(caret.Snapshot, caret.Position);
        return true;
    }

    /// <remarks>
    /// 這是沒有人接結果的工作，因此本身不能丟出例外——丟出去就是一個沒有人觀察的
    /// Task 例外，而使用者只看到「按了 F12 沒反應」。
    /// </remarks>
    private async Task OpenAsync(ITextSnapshot snapshot, int position)
    {
        string? failure = null;

        try
        {
            failure = await ResolveAndOpenAsync(snapshot, position).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 措辭與下面回報那一行刻意不同：這一行留的是完整堆疊，那一行留的是
            // 使用者實際看到的話。寫成同一句的話紀錄檔會出現兩行只差長度的訊息。
            SqlAssistDiagnostics.WriteAlways($"開啟{OperationName}時發生例外：{exception}");
            failure = $"開啟{OperationName}失敗：{exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }

        // 狀態列只能在 UI 執行緒上寫，而這一段本身也可能在 SSMS 關閉的過程中失敗；
        // 那時候已經沒有人接得到例外了，所以這一層才是 Guard 該出現的地方。
        await SqlAssistPlatformGuard.RunAsync(
            $"回報{OperationName}的結果",
            async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (failure is null)
                {
                    SqlAssistStatusBar.Clear(_serviceProvider);
                }
                else
                {
                    SqlAssistDiagnostics.WriteAlways(failure, _textView);
                    SqlAssistStatusBar.Show(_serviceProvider, failure);
                }

                return true;
            },
            fallback: false).ConfigureAwait(false);
    }

    /// <returns>成功時為 null，否則是要顯示給使用者的那一句。</returns>
    private async Task<string?> ResolveAndOpenAsync(ITextSnapshot snapshot, int position)
    {
        // 第一個 await 就把整條路徑推離 UI 執行緒，包含這一次字串複製本身。
        // 快照是不可變的，從哪一條執行緒讀都安全。
        var text = await Task.Run(() => snapshot.GetText()).ConfigureAwait(false);

        var location = await SqlObjectLocator
            .LocateAsync(_metadataService, text, position, CancellationToken.None)
            .ConfigureAwait(false);

        if (location is null)
        {
            return "游標處不是可辨識的資料庫物件。";
        }

        var objectInfo = location.Object;

        // 暫存資料表、資料表變數與 CTE 的定義就是使用者眼前那幾行。開一個新視窗把它
        // 複製一份，等於請他去看他已經在看的東西；而中繼資料對它們一列都查不到，
        // 交給下面那一段只會回報「取不到結構」，那句話還把原因說錯了。
        if (objectInfo.Kind.IsScriptDeclared())
        {
            return $"{objectInfo.QualifiedName} 是這份指令碼自己宣告的，定義就在目前的查詢視窗裡。";
        }

        var structure = await _metadataService
            .GetStructureAsync(objectInfo, CancellationToken.None)
            .ConfigureAwait(false);

        if (structure is null)
        {
            return $"取不到 {objectInfo.QualifiedName} 的結構，可能是連線已中斷或權限不足。";
        }

        // 目的地是 SSMS 剛開的空白查詢視窗，那份文件一行都還沒有——
        // SnapshotNewLine 在空白緩衝區上算出來的就是這個值。先在背景組好，
        // 才不必為了一份幾萬行的定義讓 UI 執行緒等一次字串處理。
        var script = SqlObjectScript.BuildEditable(structure, Environment.NewLine);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        return Write(script, objectInfo);
    }

    private string? Write(SqlObjectScriptText script, SqlObjectInfo objectInfo)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // 等待期間使用者把來源查詢視窗關掉了。這不是失敗，是他已經改變主意，
        // 不必再彈出一個新視窗，也沒有什麼好回報的。
        if (_textView.IsClosed)
        {
            SqlAssistDiagnostics.Write($"來源查詢視窗已關閉，放棄開啟 {objectInfo.QualifiedName} 的定義");
            return null;
        }

        var view = SsmsScriptWindow.TryCreateBlankQuery(_serviceProvider, out var failure);

        if (view is null)
        {
            return failure;
        }

        var replacement = new TextReplacement(
            script.Text,
            SqlAssistActivityKind.DefinitionOpened,
            $"已在新查詢視窗開啟 {objectInfo.QualifiedName} 的定義",
            script.CaretOffset);

        // 空白查詢視窗的樣板是一個 0 位元組的檔案，所以這一道守門平常永遠成立。
        // 它擋的是「拿到的不是剛開的那個視窗」——那一次會把指令碼蓋到使用者
        // 正在編輯的查詢上，而那是無法復原的損失。
        return new TextViewEditCoordinator(view).InsertIntoBlank(replacement)
            ? null
            : "新查詢視窗不是空的，已取消寫入定義。";
    }
}
