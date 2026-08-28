using System;
using Microsoft.VisualStudio.Text;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.Editor;

/// <summary>
/// 讓 Core 的語彙判斷直接讀取編輯器快照。
/// </summary>
/// <remarks>
/// 快照本身是不可變的，逐字元讀取安全且不需要複製。按鍵路徑上的判斷改走這裡，
/// 就不必為了「判斷游標在不在字串裡」而每按一次鍵複製一份完整的指令碼。
/// </remarks>
internal sealed class SnapshotTextSource : ISqlTextSource
{
    private readonly ITextSnapshot _snapshot;

    public SnapshotTextSource(ITextSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public int Length => _snapshot.Length;

    public char this[int index] => _snapshot[index];

    public string Substring(int start, int length) => _snapshot.GetText(start, length);
}
