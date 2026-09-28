using System;
using System.Runtime.InteropServices;
using SqlAssist.Core.Notifications;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Settings;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Preview;

/// <summary>
/// 停靠的結構預覽：從浮動預覽「移到工具視窗」的那一份。
/// </summary>
/// <remarks>
/// 浮動預覽屬於一個編輯器——換分頁就藏起來、只能擺在 SSMS 視窗之內。要一直開著對照、
/// 在另一個查詢視窗裡照著寫、或放到另一台螢幕的，交給殼層的停靠系統，這些它本來就會。
/// 內容與分層載入和浮動預覽同一份（<see cref="SqlStructurePanel"/>、<see cref="SqlStructurePresenter"/>），
/// 只是沒有外殼：圓角、握把、圖釘與進出場都是浮在編輯器上才需要的。
///
/// 只有一扇：再移一次就換掉內容。要同時對照好幾張表是多開幾扇的事，接縫在 <see cref="Show"/>。
/// </remarks>
[Guid("d4746618-0dcd-40bd-a5ad-8ec33078a641")]
public sealed class SqlStructureToolWindow : SqlToolWindowPane
{
    private SqlStructurePanel? _panel;
    private SqlStructurePresenter? _presenter;
    private SqlPreviewSubject? _subject;
    private SqlMetadataService? _service;

    public SqlStructureToolWindow()
    {
        Caption = PreviewText.ToolWindowCaption;
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        SqlAssistPlatformGuard.Run("建立結構工具窗", Build);
        SqlLanguageSwitch.Changed += OnLanguageChanged;
    }

    /// <summary>打開工具視窗並換上這一份，焦點落在搜尋框。</summary>
    internal static void Show(IServiceProvider serviceProvider, SqlPreviewSubject subject, SqlMetadataService? service) =>
        Open<SqlStructureToolWindow>(serviceProvider, PreviewText.ToolWindowMissing, PreviewText.ToolWindowOpenFailed,
            window =>
            {
                window.Display(subject, service);
                window._panel?.FocusSearch();
            });

    private void Display(SqlPreviewSubject subject, SqlMetadataService? service)
    {
        _subject = subject;
        _service = service;
        if (_panel is null || _presenter is null)
        {
            Build();
        }

        if (_panel is not { } panel || _presenter is not { } presenter)
        {
            return;
        }

        panel.ApplyFontSize(SqlAssistSettingsStore.Current.PreviewFontSize);
        presenter.Show(subject, service);
        Caption = PreviewText.ToolWindowCaptionFor(subject.Object?.QualifiedName ?? subject.BuiltIn?.Name ?? string.Empty);
    }

    private void Build()
    {
        _panel = new SqlStructurePanel(textView: null) { StatusInset = 14 };
        _presenter = new SqlStructurePresenter(_panel, _panel.Dispatcher, NotificationOrigin.User, () => PreviewText.ToolWindowCaption);
        Host.Content = _panel;
    }

    /// <summary>換語言時整份內容重建，再畫一次同一個主體；理由同 SQL Search 工具窗。</summary>
    private void OnLanguageChanged(object? sender, EventArgs args) =>
        SqlAssistPlatformGuard.Run("換語言重建結構工具窗", () =>
        {
            Release();
            Build();
            Caption = PreviewText.ToolWindowCaption;
            if (_subject is { } subject)
            {
                Display(subject, _service);
            }
        });

    private void Release()
    {
        _presenter?.Dispose();
        _presenter = null;
        _panel?.Dispose();
        _panel = null;
        Host.Content = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SqlLanguageSwitch.Changed -= OnLanguageChanged;
            Release();
        }

        base.Dispose(disposing);
    }
}
