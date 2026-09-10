using System;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Editor;
using System.Threading;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Blocks;

/// <summary>每個 buffer 一份快取；分割檢視共用解析，最後一個使用者離開才解除接線。</summary>
internal sealed class BlockAnalysis
{
    private readonly ITextBuffer _buffer;
    private readonly Dispatcher _dispatcher;
    private readonly BlockAnalysisWorker _worker = new();
    private CancellationTokenSource? _pending;
    private int _users;
    private bool _disposed;
    private bool _enabled;
    private int _delay;
    public ITextSnapshot? Snapshot { get; private set; }
    public BlockMatcher? Matcher { get; private set; }
    public event EventHandler? Updated;

    private BlockAnalysis(ITextBuffer buffer, Dispatcher dispatcher)
    {
        _buffer = buffer;
        _dispatcher = dispatcher;
        _buffer.Changed += OnChanged;
        _buffer.ContentTypeChanged += OnContentTypeChanged;
        SqlAssistSettingsStore.Changed += OnSettingsChanged;
        ApplySettings();
    }

    public static BlockAnalysis Acquire(ITextBuffer buffer, Dispatcher dispatcher)
    {
        var analysis = buffer.Properties.GetOrCreateSingletonProperty(() => new BlockAnalysis(buffer, dispatcher));
        analysis._users++;
        return analysis;
    }

    public void Release()
    {
        if (--_users != 0) return;
        _disposed = true;
        _pending?.Cancel();
        _pending = null;
        _buffer.Changed -= OnChanged;
        _buffer.ContentTypeChanged -= OnContentTypeChanged;
        SqlAssistSettingsStore.Changed -= OnSettingsChanged;
        _buffer.Properties.RemoveProperty(typeof(BlockAnalysis));
        Snapshot = null;
        Matcher = null;
    }

    private void OnChanged(object sender, TextContentChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("更新區塊配對快照", Schedule);

    private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("更新區塊語言類型", ApplySettings);

    private void OnSettingsChanged(object? sender, EventArgs args) =>
        _dispatcher.BeginInvoke(new Action(() => SqlAssistPlatformGuard.Run("套用區塊解析設定", ApplySettings)));

    private void ApplySettings()
    {
        if (_disposed) return;
        var settings = SqlAssistSettingsStore.Current;
        var enabled = settings.Enabled && settings.BlockMatchingEnabled &&
            _buffer.ContentType.IsOfType("SQL") && (settings.BlockKeywordHighlight || settings.BlockRangeBackground ||
            settings.BlockStructure || settings.BlockGlyphs || settings.BlockOverview || settings.BlockContextHint);
        var delay = settings.BlockDebounceMilliseconds;
        if (_enabled == enabled && _delay == delay) return;
        _enabled = enabled;
        _delay = delay;
        Schedule();
    }

    private void Schedule()
    {
        if (_disposed) return;
        _pending?.Cancel();
        _pending = null;
        Snapshot = null;
        Matcher = null;
        Updated?.Invoke(this, EventArgs.Empty);
        if (!_enabled) return;
        var snapshot = _buffer.CurrentSnapshot;
        var pending = new CancellationTokenSource();
        _pending = pending;
        SqlAssistPlatformGuard.Begin(NotificationCatalog.AnalyzingBlocks, async () =>
        {
            // 取消只丟棄過期結果；ScriptDom 本身無取消 API，因此同 buffer 限制一份解析。
            using (pending)
            {
                var token = pending.Token;
                try
                {
                    var matcher = await _worker.AnalyzeAsync(snapshot.GetText, _delay, token).ConfigureAwait(false);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_disposed || token.IsCancellationRequested ||
                            !ReferenceEquals(snapshot.Version, _buffer.CurrentSnapshot.Version)) return;
                        Snapshot = snapshot;
                        Matcher = matcher;
                        SqlAssistDiagnostics.Write($"區塊解析完成：version={snapshot.Version.VersionNumber}, pairs={matcher.Pairs.Count}");
                        Updated?.Invoke(this, EventArgs.Empty);
                    });
                }
                finally
                {
                    // UI 上清除參照後才 Dispose，避免下一輪 Cancel 遇上已釋放的來源。
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(_pending, pending)) _pending = null;
                    });
                }
            }
        }, NotificationKind.Analysis, NotificationOrigin.Typing, NotificationLevel.Debug,
            ActiveSqlEditor.GetContextName(_buffer));
    }
}
