using Xunit;

namespace SqlAssist.Core.Tests;

/// <summary>
/// 用 <c>|</c> 標出游標位置的測試輸入。
/// </summary>
/// <remarks>
/// 幾乎每一組解析測試都要「找到 <c>|</c>、斷言它存在、把它拿掉」，各寫一份除了重複，
/// 還讓少數幾個檔案的失敗訊息與別人不一樣。標記寫在敘述中間，測試資料一眼就看得出
/// 游標停在哪裡——那比另外傳一個位置數字好讀得多。
/// </remarks>
internal readonly struct SqlWithCaret
{
    private SqlWithCaret(string text, int caret)
    {
        Text = text;
        Caret = caret;
    }

    /// <summary>拿掉標記之後的文字。</summary>
    public string Text { get; }

    /// <summary>游標在 <see cref="Text"/> 裡的位置。</summary>
    public int Caret { get; }

    /// <summary>游標之前的那一段；只吃前文的判斷用這個。</summary>
    public string BeforeCaret => Text.Substring(0, Caret);

    public static SqlWithCaret Parse(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "測試輸入必須用 | 標出游標位置。");
        return new SqlWithCaret(sqlWithCaret.Remove(caret, 1), caret);
    }
}
