using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using SqlAssist.Core.Localization;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>套用對話框的結果。</summary>
internal enum SqlDictionaryApplyChoice
{
    Cancel,

    /// <summary>不在這裡執行，把指令碼寫進新的查詢視窗，由使用者自己按執行。</summary>
    OpenInQueryWindow,

    /// <summary>在一個交易裡寫回。</summary>
    Execute
}

/// <summary>
/// 套用說明變更之前的那一眼：整份指令碼、會寫到哪一台哪一個資料庫，以及執行的保證。
/// </summary>
/// <remarks>
/// 預覽的就是要執行的那一份：指令碼在打開對話框之前就定下來，這裡只讀不改。
/// 「在查詢視窗開啟」是附屬動作而不是第二個主要動作——它不寫任何東西，只是把同一份交給使用者；
/// 先關掉對話框再開窗，新開的查詢視窗才不會被這個強制回應視窗壓在後面。
/// </remarks>
internal sealed class SqlDictionaryApplyWindow : DialogWindow
{
    private static readonly SqlAssistChrome.Metrics Metrics = SqlAssistChrome.DefaultMetrics;

    private readonly SqlReadOnlyViewer _viewer = new();
    private SqlDictionaryApplyChoice _choice = SqlDictionaryApplyChoice.Cancel;

    public SqlDictionaryApplyWindow(string summary, string script)
    {
        SqlAssistDialogs.Configure(this, DictionaryText.ApplyTitle, 760, 560, minWidth: 480, minHeight: 320);
        _viewer.SetSql(script);
        AutomationProperties.SetName(_viewer, DictionaryText.ScriptName);
        Content = BuildLayout(summary);
        Closed += (_, _) => _viewer.Dispose();
    }

    /// <summary>以強制回應開啟，回傳使用者選的那一個。</summary>
    public SqlDictionaryApplyChoice Ask()
    {
        ShowModal();
        return _choice;
    }

    private Grid BuildLayout(string summary)
    {
        var root = new Grid { Margin = SqlAssistChrome.DialogPadding };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var metadata = SqlAssistChrome.CreateMetadataText(summary, Metrics);
        metadata.Margin = new Thickness(0, 0, 0, 12);
        root.Children.Add(metadata);

        var info = SqlAssistChrome.CreateInfoBar(SqlIcon.Information, DictionaryText.ApplyInfo);
        info.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(info, 1);
        root.Children.Add(info);

        Grid.SetRow(_viewer, 2);
        root.Children.Add(_viewer);

        var open = SqlAssistChrome.CreateButton(DictionaryText.OpenInQueryWindow, Metrics);
        open.Click += (_, _) => Choose(SqlDictionaryApplyChoice.OpenInQueryWindow);

        var cancel = SqlAssistChrome.CreateButton(CommonText.Cancel, Metrics);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Choose(SqlDictionaryApplyChoice.Cancel);

        var execute = SqlAssistChrome.CreateButton(DictionaryText.Execute, Metrics, primary: true);
        execute.IsDefault = true;
        execute.Click += (_, _) => Choose(SqlDictionaryApplyChoice.Execute);

        var footer = SqlAssistChrome.CreateDialogFooter(new[] { open }, null, cancel, execute);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        return root;
    }

    private void Choose(SqlDictionaryApplyChoice choice)
    {
        _choice = choice;
        Close();
    }
}
