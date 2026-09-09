using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Statements;

namespace SqlAssist.Ssms22.Signatures;

/// <summary>簽章上的一個參數；平台照 <see cref="Locus"/> 把它標成粗體。</summary>
internal sealed class SqlFunctionParameter : IParameter
{
    public SqlFunctionParameter(ISignature signature, SqlSignatureParameter parameter)
    {
        Signature = signature;
        Name = parameter.Name;
        Documentation = parameter.Documentation;
        Locus = new Span(parameter.Start, parameter.Length);
    }

    public string Documentation { get; }

    public Span Locus { get; }

    public string Name { get; }

    public ISignature Signature { get; }

    /// <summary>簽章只有一行，兩種呈現是同一串字，位置因此也相同。</summary>
    public Span PrettyPrintedLocus => Locus;
}

/// <summary>
/// 一個純量函式的簽章，游標走到哪一個引數就換哪一個粗體。
/// </summary>
/// <remarks>
/// 「現在是第幾個引數」不由平台算——平台只知道 session 開在哪裡，不懂 T-SQL 的
/// 逗號與巢狀括號。所以這裡自己盯著游標與編輯，答案來自
/// <see cref="SqlCallSignature.TrackArgument"/>，那一支只掃左括號到游標那一段。
///
/// 左括號記成 <see cref="ITrackingPoint"/> 而不是一個位置：使用者在提示開著的期間
/// 會在括號<b>前面</b>繼續編輯（改名稱、在前面插一段 SELECT），固定位置會慢慢
/// 指到別的字上，於是引數序號整個算錯。
///
/// 這條路徑跑在 UI 執行緒上，而且每一次游標移動都會走到，因此不查中繼資料、
/// 不重新分析整份指令碼——要換的物件本來就代表這個 session 該收掉了。
/// </remarks>
internal sealed class SqlFunctionSignature : ISignature, IDisposable
{
    private readonly ISignatureHelpSession _session;
    private readonly ITrackingPoint _openParenthesis;
    private readonly ReadOnlyCollection<IParameter> _parameters;
    private IParameter? _current;
    private bool _disposed;

    private SqlFunctionSignature(
        ISignatureHelpSession session,
        ITrackingSpan applicableToSpan,
        ITrackingPoint openParenthesis,
        SqlSignatureText text,
        string documentation)
    {
        _session = session;
        _openParenthesis = openParenthesis;
        ApplicableToSpan = applicableToSpan;
        Content = text.Content;
        Documentation = documentation;

        var parameters = new List<IParameter>(text.Parameters.Count);

        foreach (var parameter in text.Parameters)
        {
            parameters.Add(new SqlFunctionParameter(this, parameter));
        }

        _parameters = new ReadOnlyCollection<IParameter>(parameters);

        // 先站在第一個參數上：平台在 session 成形的那一刻就會讀 CurrentParameter，
        // 而真正的序號要等 Track 算完。沒有這一行，讀到的是 null。
        _current = _parameters.Count > 0 ? _parameters[0] : null;
    }

    public static SqlFunctionSignature Create(
        ISignatureHelpSession session,
        ITrackingSpan applicableToSpan,
        ITrackingPoint openParenthesis,
        SqlSignatureText text,
        string documentation)
    {
        var signature = new SqlFunctionSignature(
            session,
            applicableToSpan,
            openParenthesis,
            text,
            documentation);

        session.TextView.Caret.PositionChanged += signature.OnCaretMoved;
        session.TextView.TextBuffer.Changed += signature.OnBufferChanged;
        session.Dismissed += signature.OnDismissed;

        // 這一次不准收掉 session：現在還在平台組 session 的呼叫堆疊裡，
        // 在那裡 Dismiss 是把腳下的地板抽掉。真的已經離開了的話，
        // 下一次游標移動或編輯就會收掉它。
        signature.Track(allowDismiss: false);
        return signature;
    }

    public ITrackingSpan ApplicableToSpan { get; }

    public string Content { get; }

    public IParameter CurrentParameter
    {
        get => _current!;
        private set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }

            var previous = _current;
            _current = value;
            CurrentParameterChanged?.Invoke(this, new CurrentParameterChangedEventArgs(previous, value));
        }
    }

    public event EventHandler<CurrentParameterChangedEventArgs>? CurrentParameterChanged;

    public string Documentation { get; }

    public ReadOnlyCollection<IParameter> Parameters => _parameters;

    /// <summary>簽章排成一行，沒有第二種排版。</summary>
    public string PrettyPrintedContent => Content;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.TextView.Caret.PositionChanged -= OnCaretMoved;
        _session.TextView.TextBuffer.Changed -= OnBufferChanged;
        _session.Dismissed -= OnDismissed;
    }

    private void OnDismissed(object sender, EventArgs args) => Dispose();

    private void OnCaretMoved(object sender, CaretPositionChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("更新參數提示", () => Track());

    private void OnBufferChanged(object sender, TextContentChangedEventArgs args) =>
        SqlAssistPlatformGuard.Run("更新參數提示", () => Track());

    /// <summary>
    /// 游標還在這一組括號裡嗎；在的話輪到第幾個。
    /// </summary>
    /// <remarks>
    /// 引數比參數多的時候（多打了一個逗號）停在最後一個參數上，不是把提示收掉：
    /// 那正是使用者最需要看到「這個函式只收三個」的一刻。
    /// </remarks>
    private void Track(bool allowDismiss = true)
    {
        if (_disposed || _session.IsDismissed || _session.TextView.IsClosed || _parameters.Count == 0)
        {
            return;
        }

        var snapshot = _session.TextView.TextBuffer.CurrentSnapshot;
        var caret = _session.TextView.Caret.Position.BufferPosition;

        if (caret.Snapshot != snapshot)
        {
            return;
        }

        var open = _openParenthesis.GetPosition(snapshot);

        // 只取左括號到游標那一段。整份文字取出來再切的話，這一行會在每一次
        // 游標移動時把整份指令碼複製一次。
        if (open < 0 || open >= snapshot.Length || caret.Position <= open)
        {
            Leave(allowDismiss);
            return;
        }

        var index = SqlCallSignature.TrackArgument(
            snapshot.GetText(open, caret.Position - open));

        if (index is not { } argument)
        {
            Leave(allowDismiss);
            return;
        }

        CurrentParameter = _parameters[Math.Min(argument, _parameters.Count - 1)];
    }

    private void Leave(bool allowDismiss)
    {
        if (allowDismiss)
        {
            _session.Dismiss();
        }
    }
}
