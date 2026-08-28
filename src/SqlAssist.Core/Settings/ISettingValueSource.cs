namespace SqlAssist.Core.Settings;

/// <summary>
/// 以 moniker 取值的來源；<see cref="SqlAssistSettingsReader"/> 唯一的外界依賴。
/// </summary>
/// <remarks>
/// 存在的理由是把「怎麼問到值」與「值怎麼變成一份設定快照」切開。
/// 前者綁死在 SSMS 的 Unified Settings 服務上，只能在 SSMS 裡跑；
/// 後者是純粹的對應、剖析與收斂規則，也是實際會出錯的那一半——
/// 少對應一個設定不會有任何徵兆，只會安靜地吃預設值。
/// 切開之後那一半就落在 Core，測試可以餵一個假來源把它整個跑過。
///
/// 實作要吞掉自己的例外並回傳 <c>false</c>：任何一個 moniker 出問題
/// 都不該讓其餘十幾個跟著失效。
/// </remarks>
public interface ISettingValueSource
{
    /// <summary>取得一個設定值。</summary>
    /// <returns>讀不到、型別不符或發生例外時為 <c>false</c>，此時 <paramref name="value"/> 的內容無意義。</returns>
    bool TryGetValue<T>(string moniker, out T value)
        where T : notnull;
}
