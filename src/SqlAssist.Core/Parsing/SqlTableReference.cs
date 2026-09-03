using System;

namespace SqlAssist.Core.Parsing;

/// <summary>敘述中出現的一個資料來源，例如 <c>FROM dbo.Lib_Reader AS u</c>。</summary>
public sealed class SqlTableReference
{
    /// <summary>具名的資料來源：一到四段的名稱，可能跨資料庫或跨伺服器。</summary>
    public SqlTableReference(SqlObjectPath path, string? alias, int start, int end)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Alias = alias;
        IsDerived = false;
        Start = start;
        End = end;
    }

    /// <summary>
    /// 衍生資料表、資料表值建構式與資料表變數：中繼資料查不到它們。
    /// </summary>
    /// <remarks>
    /// 與具名來源分成兩個建構式，是因為這一種<b>沒有</b>路徑可言，而具名來源
    /// <b>一定</b>有。用同一個建構式加一個 <c>isDerived</c> 旗標的話，
    /// 「路徑是 null 但 isDerived 是 false」這種說不通的組合就建得出來，
    /// 而下游會拿它去查中繼資料。
    /// </remarks>
    public SqlTableReference(string objectName, string? alias, int start, int end)
    {
        DerivedName = objectName ?? string.Empty;
        Alias = alias;
        IsDerived = true;
        Start = start;
        End = end;
    }

    /// <summary>具名來源的完整位置；衍生來源為 null。</summary>
    public SqlObjectPath? Path { get; }

    private string DerivedName { get; } = string.Empty;

    /// <summary>結構描述限定字，沒寫時為 null。</summary>
    public string? SchemaName => Path?.SchemaName;

    /// <summary>資料庫限定字，沒寫時為 null。</summary>
    public string? DatabaseName => Path?.DatabaseName;

    /// <summary>連結伺服器限定字，沒寫時為 null。</summary>
    public string? ServerName => Path?.ServerName;

    /// <summary>物件名稱；衍生資料表沒有名稱時為空字串。</summary>
    public string ObjectName => Path?.Name ?? DerivedName;

    /// <summary>別名，沒寫時為 null。</summary>
    public string? Alias { get; }

    /// <summary>是否為衍生資料表或資料表值建構式，這種來源查不到中繼資料。</summary>
    public bool IsDerived { get; }

    /// <summary>
    /// 這個來源在目前這條連線上查得到嗎。
    /// </summary>
    /// <remarks>
    /// 衍生來源算「是」：它們本來就不查中繼資料，判斷不到這一步。
    /// 真正要擋的是 <c>FROM LibArchive.dbo.Loan l</c> 之後的 <c>l.</c>
    /// ——那裡若拿目前連線裡同名的表來回答，使用者看到的是一份看起來正常、
    /// 實際上屬於別張表的欄位清單。
    /// </remarks>
    public bool IsLocal => Path is null || Path.IsLocal;

    public int Start { get; }

    public int End { get; }

    /// <summary>在敘述中可用來限定欄位的名稱：有別名就是別名，否則是物件名稱。</summary>
    /// <remarks>
    /// 沒有別名時只取<b>名稱本體</b>，不含前面幾段：<c>FROM LibArchive.dbo.Loan</c>
    /// 之後用來限定欄位的是 <c>Loan.</c>，不是整串四段式名稱。
    /// </remarks>
    public string EffectiveName => string.IsNullOrEmpty(Alias) ? ObjectName : Alias!;

    public override string ToString()
    {
        var name = Path?.ToString() ?? DerivedName;
        return string.IsNullOrEmpty(Alias) ? name : $"{name} AS {Alias}";
    }
}
