using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Statements;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Signatures;

/// <summary>
/// 游標停在純量函式的引數清單裡時，浮出一行簽章。
/// </summary>
/// <remarks>
/// 補的是 SSMS 唯一沒有涵蓋到的那一格。實機量測（2026-09）：內建函式浮得出參數資訊，
/// <c>FROM</c> 後面的資料表值函式也浮得出來，<b>純量函式一律沒有</b>——連
/// <c>Ctrl+Shift+Space</c> 明示叫用都沒反應。所以這裡只做純量函式，其餘位置一個字
/// 都不畫：兩份提示搶同一格比缺一格糟糕。
///
/// 用平台的簽章提示（<see cref="ISignatureHelpBroker"/>）而不是自己開一個視窗：
/// 擺放、跟著捲動、Esc 收掉、與建議清單併存都是它的事，自己畫一份等於重寫一次，
/// 而且會在別人的視窗旁邊多出一套外觀——那正是 UI 準則擋著的事。
/// 平台不懂的只有「現在是第幾個引數」，那一段在 <see cref="SqlFunctionSignature"/>。
/// </remarks>
internal sealed class SqlSignatureHelp
{
    private readonly ITextView _textView;
    private readonly SqlMetadataService _metadataService;
    private readonly ISignatureHelpBroker _broker;

    /// <summary>
    /// 已經備好、等著平台來取的那一份。
    /// </summary>
    /// <remarks>
    /// <c>ISignatureHelpSource.AugmentSignatureHelpSession</c> 是<b>同步</b>的，
    /// 而參數要問中繼資料。所以順序反過來：先在背景把內容備齊，再叫平台開 session，
    /// 平台回頭來取的時候只是把備好的那一份拿走。
    /// 在 Augment 裡等查詢的話，那一次等待發生在 UI 執行緒上。
    /// </remarks>
    private PendingSignature? _pending;

    private SqlSignatureHelp(
        ITextView textView,
        SqlMetadataService metadataService,
        ISignatureHelpBroker broker)
    {
        _textView = textView;
        _metadataService = metadataService;
        _broker = broker;
    }

    private sealed class PendingSignature
    {
        public PendingSignature(
            ITrackingSpan applicableToSpan,
            ITrackingPoint openParenthesis,
            SqlSignatureText text,
            string documentation)
        {
            ApplicableToSpan = applicableToSpan;
            OpenParenthesis = openParenthesis;
            Text = text;
            Documentation = documentation;
        }

        public ITrackingSpan ApplicableToSpan { get; }

        public ITrackingPoint OpenParenthesis { get; }

        public SqlSignatureText Text { get; }

        public string Documentation { get; }
    }

    public static SqlSignatureHelp GetOrCreate(
        ITextView textView,
        SqlMetadataService metadataService,
        ISignatureHelpBroker broker)
    {
        return textView.Properties.GetOrCreateSingletonProperty(
            typeof(SqlSignatureHelp),
            () => new SqlSignatureHelp(textView, metadataService, broker));
    }

    /// <summary>這個編輯器上有沒有備好的內容；沒有掛過這個功能時為 null。</summary>
    public static SqlSignatureHelp? Peek(ITextView textView)
    {
        return textView.Properties.TryGetProperty<SqlSignatureHelp>(
            typeof(SqlSignatureHelp),
            out var help)
            ? help
            : null;
    }

    /// <summary>平台來取內容；只取得走一次。</summary>
    public void Augment(ISignatureHelpSession session, IList<ISignature> signatures)
    {
        var pending = Interlocked.Exchange(ref _pending, null);

        if (pending is null)
        {
            return;
        }

        signatures.Add(SqlFunctionSignature.Create(
            session,
            pending.ApplicableToSpan,
            pending.OpenParenthesis,
            pending.Text,
            pending.Documentation));
    }

    /// <summary>
    /// 依游標目前的位置決定要不要顯示，該顯示就備好內容並叫平台開 session。
    /// </summary>
    /// <remarks>
    /// 呼叫端一律是按鍵路徑（打了左括號、打了逗號、提交完一個函式），所以整段跑在
    /// 背景：詞法分析要掃過游標之前的整份文字，參數還可能要查一次資料庫。
    /// 這一輪來不及就這一輪不顯示，下一個逗號就有了。
    /// </remarks>
    public void Request()
    {
        var settings = SqlAssistSettingsStore.Current;

        if (!settings.Enabled || !settings.ParameterHintEnabled || _textView.IsClosed)
        {
            return;
        }

        var snapshot = _textView.TextBuffer.CurrentSnapshot;
        var caret = _textView.Caret.Position.BufferPosition;

        if (caret.Snapshot != snapshot)
        {
            return;
        }

        var text = snapshot.GetText();
        var position = caret.Position;

        // 打字時每一個左括號與逗號都會走一次，因此是 Typing／Debug。
        SqlAssistPlatformGuard.Begin(
            NotificationCatalog.ShowingSignatureHelp,
            () => RequestAsync(snapshot, text, position),
            NotificationKind.Completion, NotificationOrigin.Typing, NotificationLevel.Debug,
            ActiveSqlEditor.GetDocumentName(_textView));
    }

    /// <summary>
    /// 這一輪命令結束之後再問。
    /// </summary>
    /// <remarks>
    /// 打字時那個字元還沒進緩衝區——左括號都還沒寫進去，這時候問一定說「不在任何
    /// 引數清單裡」。與重開建議清單、Snippet 跳格是同一個理由，共用同一份排程。
    /// </remarks>
    public void RequestAfterCurrentCommand()
    {
        TextViewDispatch.AfterCurrentCommand(_textView, "顯示函式參數提示", _ => Request());
    }

    /// <summary>這個編輯器上現在有沒有開著的簽章提示。</summary>
    public bool IsActive => !_textView.IsClosed && _broker.IsSignatureHelpActive(_textView);

    private async Task RequestAsync(ITextSnapshot snapshot, string text, int caret)
    {
        var site = await Task.Run(() => SqlCallSignature.Resolve(text, caret)).ConfigureAwait(false);

        if (site is null)
        {
            return;
        }

        var location = await SqlObjectLocator
            .LocateAsync(_metadataService, text, site.NameStart, CancellationToken.None,
                NotificationOrigin.Typing)
            .ConfigureAwait(false);

        // 只做純量函式；資料表值函式與內建名稱交給 SSMS 自己那一份。
        if (location is null ||
            location.Column is not null ||
            location.Object.Kind != SqlObjectKind.ScalarFunction)
        {
            return;
        }

        var detail = location.Detail
            ?? await _metadataService
                .GetDetailAsync(location.Object, CancellationToken.None, NotificationOrigin.Typing)
                .ConfigureAwait(false);

        if (detail is null || Build(detail) is not { } signature)
        {
            return;
        }

        // 這裡開始碰編輯器：範圍、游標與 broker 都只在 UI 執行緒上有效。
        TextViewDispatch.AfterCurrentCommand(_textView, "開啟函式參數提示", _ =>
            Show(snapshot, site, signature, detail.Description ?? string.Empty));
    }

    /// <summary>
    /// 把參數排成一行簽章；沒有輸入參數時不顯示。
    /// </summary>
    /// <remarks>
    /// 沒有參數的函式（<c>dbo.fn_Today()</c>）不浮提示：這一份的用途是指出「現在
    /// 輪到第幾個引數、它叫什麼」，一個引數都沒有的時候它沒有話可說，
    /// 而它擋住的那一行程式碼是真的。
    /// </remarks>
    private static SqlSignatureText? Build(SqlObjectDetail detail)
    {
        // 選擇性參數只讀得出來就標，讀不到就少標幾個——與 EXEC 骨架同一份判斷、
        // 同一個方向：猜錯會讓使用者以為某個必填的參數可以省略。
        var optional = SqlModuleParameterDefaults.Find(detail.Definition);
        var parameters = new List<SqlStatementParameter>(detail.Parameters.Count);
        var returnType = string.Empty;

        foreach (var parameter in detail.Parameters)
        {
            // parameter_id 0 是傳回值，不是呼叫時傳得進去的東西；它的型別就是 RETURNS。
            if (parameter.Ordinal <= 0)
            {
                returnType = parameter.DataType;
                continue;
            }

            parameters.Add(new SqlStatementParameter(
                parameter.Name,
                parameter.DataType,
                parameter.IsOutput,
                optional.Contains(parameter.Name)));
        }

        return parameters.Count == 0
            ? null
            : SqlFunctionSignatureText.Build(
                detail.Object.QualifiedName,
                parameters,
                returnType);
    }

    private void Show(
        ITextSnapshot snapshot,
        SqlCallSignatureContext site,
        SqlSignatureText text,
        string documentation)
    {
        if (_textView.IsClosed || site.OpenParenthesis >= snapshot.Length)
        {
            return;
        }

        // 同一個編輯器只留一份。查詢期間使用者可能已經走進另一個呼叫，
        // 而舊的那一個要嘛自己收掉了、要嘛講的是別的函式。
        //
        // 建議清單那一邊禁止用 DismissAllSessions 搶 session（見平台護欄），
        // 這裡不同：那一條擋的是「別人開的清單被我們收掉」，而簽章這個表面上
        // 唯一會開 session 的就是這裡——SSMS 自己的參數資訊走的是舊版語言服務的
        // MethodTip，根本不是 ISignatureHelpSession。
        _broker.DismissAllSessions(_textView);

        _pending = new PendingSignature(
            snapshot.CreateTrackingSpan(
                Span.FromBounds(site.NameStart, site.OpenParenthesis + 1),
                SpanTrackingMode.EdgeInclusive),
            // Negative：使用者在括號前面插字時這個點跟著往後挪，插在括號後面
            // （也就是引數裡）時它留在原地——引數序號正是從這個點往右數的。
            snapshot.CreateTrackingPoint(site.OpenParenthesis, PointTrackingMode.Negative),
            text,
            documentation);

        // 錨在左括號上而不是游標上：游標會一路往右走到第三個引數，
        // 提示跟著跑的話，使用者眼睛要追著它移動。
        _broker.TriggerSignatureHelp(
            _textView,
            snapshot.CreateTrackingPoint(site.OpenParenthesis + 1, PointTrackingMode.Negative),
            trackCaret: false);

        SqlAssistDiagnostics.Write($"已顯示參數提示：{text.Content}", _textView);
    }
}
