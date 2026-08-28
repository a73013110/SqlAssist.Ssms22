using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Settings;

namespace SqlAssist.Core.Tests.Settings;

/// <summary>
/// 以字典充當 Unified Settings 的假來源，同時記錄被問過哪些 moniker。
/// </summary>
/// <remarks>
/// 記錄查詢是重點：新增設定卻忘了在
/// <see cref="SqlAssistSettingsReader"/> 建立對應時，唯一看得見的徵兆
/// 就是「這個 moniker 從頭到尾沒被問過」。
/// </remarks>
internal sealed class FakeSettingValueSource : ISettingValueSource
{
    private readonly IReadOnlyDictionary<string, object> _values;
    private readonly List<string> _requested = new();

    public FakeSettingValueSource()
        : this(new Dictionary<string, object>())
    {
    }

    public FakeSettingValueSource(IReadOnlyDictionary<string, object> values) => _values = values;

    /// <summary>被查詢過的 moniker，去除重複後依序數排序。</summary>
    public IReadOnlyList<string> Requested =>
        _requested.Distinct().OrderBy(moniker => moniker, System.StringComparer.Ordinal).ToArray();

    public bool TryGetValue<T>(string moniker, out T value)
        where T : notnull
    {
        _requested.Add(moniker);

        if (_values.TryGetValue(moniker, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
