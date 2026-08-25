using System;

namespace SqlAssist.Core;

/// <summary>
/// 可以逐字元讀取的文字來源。
/// </summary>
/// <remarks>
/// 讓語彙判斷不必先把整份文字複製成字串。這些判斷都得從頭掃到指定位置，
/// 掃描本身無法避免，但每按一次鍵就複製一份幾百 KB 的指令碼可以避免——
/// 那是打字時最容易感覺到的那種卡頓。
/// 編輯器的快照直接實作得出這個介面，Core 本身則用 <see cref="SqlStringText"/>。
/// </remarks>
public interface ISqlTextSource
{
    int Length { get; }

    char this[int index] { get; }

    string Substring(int start, int length);
}

/// <summary>以字串為來源的實作，供不在編輯器裡的呼叫端與測試使用。</summary>
public sealed class SqlStringText : ISqlTextSource
{
    private readonly string _text;

    public SqlStringText(string text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public int Length => _text.Length;

    public char this[int index] => _text[index];

    public string Substring(int start, int length) => _text.Substring(start, length);
}
