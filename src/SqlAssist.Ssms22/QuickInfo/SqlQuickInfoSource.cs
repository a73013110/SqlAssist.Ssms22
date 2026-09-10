using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Notifications;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Metadata.Model;
using SqlAssist.Ssms22;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Preview;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.QuickInfo;

/// <summary>
/// 滑鼠停留在資料庫物件上時，顯示該物件的結構。
/// </summary>
/// <remarks>
/// 這條路徑在滑鼠移動的軌跡上，成本必須接近零：中繼資料只讀快取，
/// 連線只用已經解析好的目錄，同一個識別字重複停留也只解析一次。
/// </remarks>
internal sealed class SqlQuickInfoSource : IAsyncQuickInfoSource
{
    /// <summary>整份文字與上一次的語法分析，依文字快照快取，不保存中繼資料結果。</summary>
    /// <remarks>
    /// 一次滑鼠停留會產生數個 session，滑鼠在同一個字上輕微移動也會重來一次，
    /// 而 <see cref="SqlAssist.Core.Parsing.SqlScopeAnalyzer"/> 每一次都要對整份文字
    /// 做詞法分析。以單一不可變物件整份換掉，讀取端不必擔心欄位之間彼此不同步。
    /// </remarks>
    private sealed class ParsedIdentifier
    {
        public ParsedIdentifier(ITextSnapshot snapshot, string text, SqlObjectLookup lookup)
        {
            Snapshot = snapshot;
            Text = text;
            Lookup = lookup;
        }

        public ITextSnapshot Snapshot { get; }

        public string Text { get; }

        public SqlObjectLookup Lookup { get; }
    }

    private readonly ITextBuffer _textBuffer;
    private readonly IServiceProvider _serviceProvider;
    private ParsedIdentifier? _parsed;
    private bool _disposed;

    public SqlQuickInfoSource(ITextBuffer textBuffer, IServiceProvider serviceProvider)
    {
        _textBuffer = textBuffer;
        _serviceProvider = serviceProvider;
    }

    public Task<QuickInfoItem?> GetQuickInfoItemAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        // 提示視窗失敗絕不可以影響編輯；記錄後安靜地什麼都不顯示。
        return SqlAssistPlatformGuard.RunAsync<QuickInfoItem?>(
            "物件提示產生",
            () => GetQuickInfoCoreAsync(session, cancellationToken),
            fallback: null);
    }

    private async Task<QuickInfoItem?> GetQuickInfoCoreAsync(
        IAsyncQuickInfoSession session,
        CancellationToken cancellationToken)
    {
        var settings = SqlAssistSettingsStore.Current;

        if (_disposed || !settings.Enabled || !settings.HoverEnabled)
        {
            return null;
        }

        var textView = session.TextView;

        if (textView is null)
        {
            return null;
        }

        // 滑鼠掃過每一個識別字都會走一次，因此是 Typing；「物件提示與結構預覽」
        // 那一格預設關著，想看背景在載入什麼的人自己打開。
        using var notification = NotificationCenter.Default.Begin(NotificationCatalog.PreparingObjectHint,
            NotificationKind.Preview, NotificationOrigin.Typing, NotificationLevel.Info,
            context: ActiveSqlEditor.GetContextName(textView));

        var snapshot = _textBuffer.CurrentSnapshot;
        var triggerPoint = session.GetTriggerPoint(snapshot);

        if (triggerPoint is not { } point)
        {
            return null;
        }

        // 建議清單、ALTER 展開與這裡看的是同一份服務：連線解析與三層快取都只做一次。
        // 先前這裡各自 new 一份，等於在滑鼠移動的軌跡上另外開一條會問 SSMS 連線的支線，
        // 而那個呼叫有 UI 執行緒相依性，忙的時候會直接反映成打字延遲。
        var metadataService = SqlCompletionServices.GetMetadataService(textView, _serviceProvider);

        // 詞法分析要掃過整份文字，不留在呼叫端的執行緒上。
        var resolution = await Task
            .Run(() => Resolve(metadataService, snapshot, point.Position), cancellationToken)
            .ConfigureAwait(false);

        if (resolution.Location is not { } location)
        {
            // 內建名稱不是資料庫物件，物件解析一定落空。排在後面而不是前面：
            // SELECT * FROM Format 停在 Format 上時要的是那張表，不是同名的內建函式。
            return BuildBuiltInItem(settings, textView, snapshot, resolution);
        }

        var applicableSpan = snapshot.CreateTrackingSpan(
            new Span(location.Reference.Start, location.Reference.Length),
            SpanTrackingMode.EdgeInclusive);

        // 指令碼自己宣告的暫存資料表、資料表變數與 CTE 在定位那一步就把明細讀出來了。
        // 它們的 object_id 一律是 0，回頭問中繼資料不是白跑一次查詢就是拿到別的東西。
        var script = location.Detail is { } scriptDetail ? new SqlObjectStructure(scriptDetail) : null;

        // 提示只給一眼看得完的份量，看不完的那一半交給浮動預覽。
        var openStructure = CreateOpenStructureAction(
            textView,
            metadataService,
            applicableSpan,
            location.Object,
            script);

        if (location.Column is { } column)
        {
            notification.Report(location.Object.QualifiedName);
            SqlAssistDiagnostics.Write($"已顯示欄位提示：{location.Object.QualifiedName}.{column.Name}");
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildColumn(location.Object, column, openStructure));
        }

        var detail = location.Detail ?? metadataService.PeekDetail(location.Object);

        if (detail is null)
        {
            // 快取沒有就這一輪只顯示標題，背景補上之後下一次停留就有內容。
            metadataService.WarmDetail(location.Object);
            return new QuickInfoItem(
                applicableSpan,
                SqlQuickInfoContentBuilder.BuildLoading(location.Object, openStructure));
        }

        notification.Report(location.Object.QualifiedName);
        SqlAssistDiagnostics.Write($"已顯示物件提示：{location.Object.QualifiedName}");
        return new QuickInfoItem(applicableSpan, SqlQuickInfoContentBuilder.Build(detail, openStructure));
    }

    /// <summary>
    /// 「開啟完整結構」連結要執行的動作；由編輯器在使用者點擊時呼叫。
    /// </summary>
    /// <remarks>
    /// 開的是建議清單用的同一個浮動預覽，錨在同一個識別字上。
    /// 兩條入口共用一份視窗與一份載入邏輯，行為與外觀不會有兩套。
    /// </remarks>
    /// <param name="script">指令碼宣告的物件已經讀好的結構；資料庫物件傳 null。</param>
    private Action CreateOpenStructureAction(
        ITextView textView,
        SqlMetadataService metadataService,
        ITrackingSpan anchor,
        SqlObjectInfo objectInfo,
        SqlObjectStructure? script)
    {
        return () =>
        {
            if (SqlStructurePreview.GetOrCreate(textView, _serviceProvider) is { } preview)
            {
                preview.ShowAt(anchor, objectInfo, metadataService, script);
            }
        };
    }

    /// <summary>
    /// 內建名稱的提示。
    /// </summary>
    /// <remarks>
    /// 「這個字是不是內建名稱」整段判斷在 <see cref="SqlBuiltInDocCatalog.TryGetAt"/>：
    /// 只看文字就決定得了的事不放在平台接線層。
    ///
    /// 這條路只查一份內嵌資料，不碰中繼資料也不碰連線，因此完全在滑鼠移動路徑的
    /// 成本預算內；沒有連線時照樣答得出來。
    /// </remarks>
    private QuickInfoItem? BuildBuiltInItem(
        SqlAssistSettings settings,
        ITextView textView,
        ITextSnapshot snapshot,
        Resolution resolution)
    {
        if (!settings.BuiltInHelpEnabled ||
            resolution.Reference is not { } reference ||
            !SqlBuiltInDocCatalog.TryGetAt(resolution.Text, reference, out var doc))
        {
            return null;
        }

        var span = snapshot.CreateTrackingSpan(
            new Span(reference.Start, reference.Length),
            SpanTrackingMode.EdgeInclusive);

        SqlAssistDiagnostics.Write($"已顯示內建說明：{doc.Name}");
        return new QuickInfoItem(
            span,
            SqlQuickInfoContentBuilder.BuildBuiltIn(
                doc,
                CreateOpenReferenceAction(textView, span, doc),
                CreateOpenDocsAction(doc)));
    }

    /// <summary>
    /// 「開啟完整說明」要執行的動作；沒有對照表時回傳 null，那一行就不出現。
    /// </summary>
    /// <remarks>
    /// 開的是物件結構用的同一個浮動視窗，錨在同一個名稱上——擺放、縮放、複製與
    /// 收掉的規則因此只有一套。內建說明沒有連線也沒有查詢，畫完就結束。
    /// </remarks>
    private Action? CreateOpenReferenceAction(
        ITextView textView,
        ITrackingSpan anchor,
        SqlBuiltInDoc doc)
    {
        if (!doc.HasReferences)
        {
            return null;
        }

        return () =>
        {
            if (SqlStructurePreview.GetOrCreate(textView, _serviceProvider) is { } preview)
            {
                preview.ShowBuiltInAt(anchor, doc);
            }
        };
    }

    /// <summary>
    /// 「線上文件」要執行的動作；沒有可用位址時回傳 null，那一行就不出現。
    /// </summary>
    /// <remarks>
    /// 只接受絕對的 https 位址。資源是隨組件發布的，但把字串直接交給殼層開啟
    /// 是一條真的會執行東西的路，形狀檢查的成本是零。
    ///
    /// 走 Guard 而不是自己 try：呼叫者是編輯器的連結處理常式，而提示視窗沒有任何
    /// 地方可以顯示失敗——沒有預設瀏覽器時應該安靜地什麼都不做，不是丟到平台上。
    /// </remarks>
    private static Action? CreateOpenDocsAction(SqlBuiltInDoc doc)
    {
        if (!Uri.TryCreate(doc.DocsUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return () => SqlAssistPlatformGuard.Run(
            "開啟線上文件",
            () => Process.Start(uri.AbsoluteUri));
    }

    /// <summary>解析結果；識別字與原文即使不是資料庫物件也要帶回來，內建說明才有得問。</summary>
    private readonly struct Resolution
    {
        public Resolution(string? text, SqlIdentifierReference? reference, SqlObjectLocation? location)
        {
            Text = text;
            Reference = reference;
            Location = location;
        }

        /// <summary>整份 SQL；內建說明要看名稱後面是不是接著左括號。</summary>
        public string? Text { get; }

        public SqlIdentifierReference? Reference { get; }

        public SqlObjectLocation? Location { get; }
    }

    /// <summary>只重用語法分析；清快取與背景載入不會改變 SQL 文字，物件與欄位必須重新比對。</summary>
    private Resolution Resolve(SqlMetadataService metadataService, ITextSnapshot snapshot, int position)
    {
        var parsed = Volatile.Read(ref _parsed);
        var text = parsed is not null && ReferenceEquals(parsed.Snapshot, snapshot)
            ? parsed.Text
            : snapshot.GetText();

        var reference = SqlIdentifierScanner.FindAt(text, position);

        if (reference is null)
        {
            return default;
        }

        if (parsed is not null &&
            ReferenceEquals(parsed.Snapshot, snapshot) &&
            reference.Start == parsed.Lookup.Reference.Start &&
            reference.End == parsed.Lookup.Reference.End)
        {
            return new Resolution(
                text,
                reference,
                SqlObjectLocator.LocateCached(metadataService, parsed.Lookup));
        }

        var lookup = SqlObjectLookup.Create(text, position);
        if (lookup is null)
        {
            return new Resolution(text, reference, null);
        }

        Volatile.Write(ref _parsed, new ParsedIdentifier(snapshot, text, lookup));
        return new Resolution(text, reference, SqlObjectLocator.LocateCached(metadataService, lookup));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // 中繼資料服務的所有權在 TextView，這裡只放掉自己的快取。
        _disposed = true;
        Volatile.Write(ref _parsed, null);
    }
}
