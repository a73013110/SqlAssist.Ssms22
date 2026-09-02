using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SqlAssist.Core.Settings;
using Xunit;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// <c>SqlAssist.registration.json</c>、<see cref="SqlAssistMonikers"/> 與
/// <see cref="SqlAssistSettingsReader"/> 三者之間的一致性。
/// </summary>
/// <remarks>
/// 新增一個設定要同時動註冊檔、moniker 常數、讀取端的對應與 POCO 屬性，
/// 而漏掉其中任何一步都不會有編譯錯誤，只會讓那個設定在執行期安靜地
/// 永遠停在預設值——設定頁調得動、程式不理會，而且沒有任何訊息。
/// 這一組測試以註冊檔為基準把每一步都反推一次，讓那種遺漏變成建置失敗。
/// </remarks>
public sealed class SqlAssistSettingsReaderTests
{
    public static TheoryData<string> Monikers()
    {
        var data = new TheoryData<string>();

        foreach (var moniker in RegistrationManifest.Monikers)
        {
            data.Add(moniker);
        }

        return data;
    }

    /// <summary>
    /// 讀取端會去問註冊檔宣告的每一個設定，而且不多問。
    /// </summary>
    /// <remarks>
    /// 兩個方向都要擋：少問代表新設定沒接上，多問代表 moniker 打錯字或
    /// 註冊檔已經移除了那一項——後者在執行期同樣安靜。
    /// </remarks>
    [Fact]
    public void 讀取端問過註冊檔宣告的每一個設定()
    {
        var source = new FakeSettingValueSource();

        SqlAssistSettingsReader.Read(source);

        Assert.Equal(RegistrationManifest.Monikers, source.Requested);
    }

    /// <summary>訂閱清單少一個，改了設定就要重開查詢視窗才生效。</summary>
    [Fact]
    public void 訂閱清單涵蓋註冊檔宣告的每一個設定()
    {
        Assert.Equal(RegistrationManifest.Monikers, SqlAssistMonikers.All);
    }

    /// <summary>
    /// 註冊檔的預設值一路走到快照，結果必須等於 POCO 的屬性預設值。
    /// </summary>
    /// <remarks>
    /// 這是端到端的比對，列舉字串的解析與數值的收斂都包含在內：
    /// 註冊檔寫 <c>"delay"</c> 而讀取端只認得 <c>"Delay"</c> 這種錯，
    /// 逐項比對字面值抓不到，這裡抓得到。
    ///
    /// 兩邊各自宣告一次預設值是無法避免的：註冊檔那份是設定 UI 上
    /// 「恢復預設」會回到的值，POCO 那份是讀不到 Unified Settings 時
    /// 實際生效的值。它們分歧時使用者會看到設定頁顯示一個值、
    /// 擴充卻照另一個值運作。
    /// </remarks>
    [Fact]
    public void 註冊檔的預設值等於程式的預設值()
    {
        var actual = SqlAssistSettingsReader.Read(
            new FakeSettingValueSource(RegistrationManifest.DefaultValues));

        Assert.Empty(Differences(actual, new SqlAssistSettings()));
    }

    /// <summary>
    /// 每一個設定都真的落到快照的某個屬性上。
    /// </summary>
    /// <remarks>
    /// 只把這一項改成非預設值，快照就必須跟著變。抓的是「讀了值卻忘了
    /// 指派給屬性」——這種寫法連上一個測試都通得過，因為預設值本來就相等。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Monikers))]
    public void 每一個設定都會改變快照(string moniker)
    {
        var alternate = RegistrationManifest.Settings.Single(s => s.Moniker == moniker).Alternate;

        Assert.NotEmpty(Differences(ReadWith(moniker, alternate), new SqlAssistSettings()));
    }

    public static TheoryData<string> NumericMonikers()
    {
        var data = new TheoryData<string>();

        foreach (var setting in RegistrationManifest.Settings)
        {
            if (setting.Bounds is not null)
            {
                data.Add(setting.Moniker);
            }
        }

        return data;
    }

    /// <summary>
    /// 讀取端的收斂範圍就是註冊檔宣告的範圍。
    /// </summary>
    /// <remarks>
    /// 兩份範圍各寫一次無法避免：註冊檔那份約束設定 UI 上的輸入，
    /// <c>SqlAssistLimits</c> 那份負責手動編輯設定檔或讀不到註冊資訊時的界外值。
    /// 分歧的症狀很難看出來——設定頁讓使用者調到 3000 毫秒，擴充卻靜靜地
    /// 用 2000 在跑，而且兩邊都沒有錯誤。
    ///
    /// 不逐項比對常數，改用行為反推：界外值讀出來的快照必須與邊界值一模一樣，
    /// 而兩個邊界本身讀出來必須不同（收斂得比宣告更窄同樣是分歧）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(NumericMonikers))]
    public void 數值設定的收斂範圍等於註冊檔宣告的範圍(string moniker)
    {
        var bounds = RegistrationManifest.Settings.Single(s => s.Moniker == moniker).Bounds!.Value;

        Assert.Empty(Differences(ReadWith(moniker, bounds.Minimum - 1), ReadWith(moniker, bounds.Minimum)));
        Assert.Empty(Differences(ReadWith(moniker, bounds.Maximum + 1), ReadWith(moniker, bounds.Maximum)));
        Assert.NotEmpty(Differences(ReadWith(moniker, bounds.Minimum), ReadWith(moniker, bounds.Maximum)));
    }

    /// <summary>只把一個設定換成指定的值，其餘維持註冊檔的預設值。</summary>
    private static SqlAssistSettings ReadWith(string moniker, object value)
    {
        var values = new Dictionary<string, object>(RegistrationManifest.DefaultValues)
        {
            [moniker] = value
        };

        return SqlAssistSettingsReader.Read(new FakeSettingValueSource(values));
    }

    /// <summary>讀不到任何值時，整份快照就是屬性預設值。</summary>
    [Fact]
    public void 讀不到任何設定時回退為預設值()
    {
        var actual = SqlAssistSettingsReader.Read(new FakeSettingValueSource());

        Assert.Empty(Differences(actual, new SqlAssistSettings()));
    }

    /// <summary>兩份快照之間有差異的屬性，格式化成看得懂的訊息。</summary>
    private static IReadOnlyList<string> Differences(SqlAssistSettings actual, SqlAssistSettings expected)
    {
        return typeof(SqlAssistSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => new
            {
                property.Name,
                Actual = property.GetValue(actual),
                Expected = property.GetValue(expected)
            })
            .Where(pair => !Equals(pair.Actual, pair.Expected))
            .Select(pair => $"{pair.Name}：實際 {pair.Actual}，預期 {pair.Expected}")
            .ToArray();
    }
}
