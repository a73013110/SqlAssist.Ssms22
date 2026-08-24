namespace SqlAssist.Core;

/// <summary>建議清單由誰負責顯示。</summary>
/// <remarks>
/// 刻意不加序列化標註：<c>DataContractJsonSerializer</c> 對列舉一律寫成數字，
/// <c>EnumMember</c> 只對 XML 生效。設定檔要保持可讀，因此
/// <see cref="SqlAssistSuggestionSettings"/> 以字串欄位保存，再對應到這個列舉。
/// </remarks>
public enum CompletionEngine
{
    /// <summary>
    /// 平台原生的非同步 IntelliSense。
    /// </summary>
    /// <remarks>
    /// 由編輯器負責清單的定位、螢幕邊界、捲動、滑鼠操作、篩選列與佈景主題，
    /// 並且與其他擴充套件共用同一個 session，不會同時出現兩份清單。
    /// 排名與命中標示仍由本擴充的比對器決定。
    /// </remarks>
    Native,

    /// <summary>
    /// 自製 WPF 清單。
    /// </summary>
    /// <remarks>
    /// 保留為後備：原生管線若在某個 SSMS 版本失效，改回這個至少還能用。
    /// 已知限制是它與 SSMS 內建清單會同時出現，而且只能用鍵盤操作。
    /// </remarks>
    Custom
}
