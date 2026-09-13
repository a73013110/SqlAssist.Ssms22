using System;
using System.Windows.Controls;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.UI;

/// <summary>純文字編輯表面；不持有收藏、版本或儲存服務。</summary>
internal sealed class SqlTextEditor : UserControl
{
    private readonly TextBox _text = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
    private readonly SqlTextEditState _state;
    public SqlTextEditor(string sql)
    {
        _state = new SqlTextEditState(sql);
        _text.FontFamily = SqlAssistChrome.CodeFont;
        _text.AcceptsReturn = true;
        _text.AcceptsTab = true;
        _text.IsUndoEnabled = true;
        _text.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _text.Text = sql;
        _text.TextChanged += (_, _) => { _state.Text = _text.Text; Changed?.Invoke(this, EventArgs.Empty); };
        Content = _text;
    }
    public string Text => _state.Text;
    public bool IsModified => _state.IsModified;
    public bool IsReadOnly { get => _text.IsReadOnly; set => _text.IsReadOnly = value; }
    public event EventHandler? Changed;
}
