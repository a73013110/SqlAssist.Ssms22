using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>一筆待寫回的說明：載入當時的值與現在要改成的值。</summary>
public sealed class SqlDescriptionChange
{
    public SqlDescriptionChange(SqlDescriptionTarget target, string? original, string? value)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Original = SqlDescriptionValue.Normalize(original);
        Value = SqlDescriptionValue.Normalize(value);
    }

    public SqlDescriptionTarget Target { get; }

    /// <summary>載入當時的值；寫回前拿它與伺服器上現在的值比，不一樣就是別人先改過。</summary>
    public string Original { get; }

    public string Value { get; }

    /// <summary>改成空的：拿掉這筆屬性，而不是寫一個空字串進去。</summary>
    public bool IsRemoval => Value.Length == 0;

    public bool IsTooLong => Value.Length > SqlDescriptionValue.MaxLength;

    public override string ToString() => Target + ": " + Original + " → " + Value;
}

/// <summary>
/// 還沒寫回的說明編輯：每個目標一筆，改回原值就不算變更。
/// </summary>
/// <remarks>
/// 主從與資料字典兩種檢視改的是同一份——在其中一種改過、切到另一種要看得到，而「有幾項變更」
/// 只能有一個答案。原值跟著編輯一起記，不回頭去問資料字典：重新整理之後的快照可能已經是別人改過的值，
/// 拿新快照當原值的話，併發檢查就看不出那個差別。
/// </remarks>
public sealed class SqlDescriptionEdits
{
    private readonly Dictionary<SqlDescriptionTarget, SqlDescriptionChange> _changes = new();

    /// <summary>還沒寫回的變更有幾項。</summary>
    public int Count => _changes.Count;

    /// <summary>有沒有任何一項超過 <see cref="SqlDescriptionValue.MaxLength"/>；有的話整批不能寫。</summary>
    public bool HasTooLong => _changes.Values.Any(change => change.IsTooLong);

    /// <summary>照 <see cref="SqlDescriptionTarget.Order"/> 排好的變更。</summary>
    public IReadOnlyList<SqlDescriptionChange> Changes =>
        _changes.Values.OrderBy(change => change.Target, SqlDescriptionTarget.Order).ToList();

    public bool IsChanged(SqlDescriptionTarget target) => _changes.ContainsKey(target);

    /// <summary>這個目標現在的值：有編輯時是編輯過的，沒有時是 <paramref name="original"/>。</summary>
    public string Current(SqlDescriptionTarget target, string? original) =>
        _changes.TryGetValue(target, out var change) ? change.Value : SqlDescriptionValue.Normalize(original);

    /// <summary>
    /// 把一個目標改成 <paramref name="value"/>。
    /// </summary>
    /// <param name="original">載入當時的值；這個目標已經有編輯時沿用第一次記下的原值。</param>
    /// <returns>這一格的狀態真的變了（多一項、少一項或換了值）才回 true。</returns>
    public bool Set(SqlDescriptionTarget target, string? original, string? value)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        var baseline = _changes.TryGetValue(target, out var existing) ? existing.Original : SqlDescriptionValue.Normalize(original);

        if (SqlDescriptionValue.AreEqual(baseline, value))
        {
            return _changes.Remove(target);
        }

        if (existing is not null && SqlDescriptionValue.AreEqual(existing.Value, value)) return false;

        _changes[target] = new SqlDescriptionChange(target, baseline, value);
        return true;
    }

    /// <summary>放棄一個目標的編輯，回到原值。</summary>
    public bool Revert(SqlDescriptionTarget target) => _changes.Remove(target ?? throw new ArgumentNullException(nameof(target)));

    /// <summary>放棄全部編輯。</summary>
    public bool Clear()
    {
        if (_changes.Count == 0) return false;
        _changes.Clear();
        return true;
    }

    /// <summary>
    /// 重新載入之後：伺服器上已經是編輯那一句的，不再算變更。
    /// </summary>
    /// <remarks>
    /// 一條規則涵蓋三種來路：自己剛寫回的、把指令碼拿到查詢視窗執行過的、別人剛好寫了同一句的。
    /// 寫回之後另記「哪幾項成功了」的話，查詢視窗那一條永遠收不到，畫面會一直掛著已經生效的變更。
    /// 伺服器上是別的值時編輯留著、原值不動：那是併發檢查要擋的情況，由寫回時決定。
    /// </remarks>
    /// <param name="loaded">新載入的那一份裡這個目標的說明；字典裡已經沒有這個目標時給空字串。</param>
    /// <returns>不再算變更的目標。</returns>
    public IReadOnlyList<SqlDescriptionTarget> Settle(Func<SqlDescriptionTarget, string> loaded)
    {
        if (loaded is null) throw new ArgumentNullException(nameof(loaded));

        var settled = _changes.Values
            .Where(change => SqlDescriptionValue.AreEqual(change.Value, loaded(change.Target)))
            .Select(change => change.Target)
            .ToList();
        foreach (var target in settled) _changes.Remove(target);
        return settled;
    }
}
