using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 同名資料行的比較：同一個名字在幾張表上、型別與說明一不一致、空著的那幾格該寫什麼。
/// </summary>
/// <remarks>
/// 「同名」不分大小寫：<c>ReaderNo</c> 與 <c>READERNO</c> 在同一個資料庫裡幾乎一定是同一個意思，
/// 而預設定序本來就不分。
///
/// 說明的建議只在「有寫的那幾張都寫成同一句」時給：多數決會把一個寫錯的說明複製到更多張表上，
/// 而建議的意義是「照抄已經一致的那一句」，不是猜。
/// </remarks>
public sealed class SqlColumnNameGroup
{
    internal SqlColumnNameGroup(string name, int count, IReadOnlyList<string> types, IReadOnlyList<string> descriptions, int missing)
    {
        Name = name;
        Count = count;
        Types = types;
        Descriptions = descriptions;
        MissingDescriptions = missing;
    }

    /// <summary>第一次見到的寫法。</summary>
    public string Name { get; }

    /// <summary>有幾個資料行叫這個名字（每張表或檢視一個）。</summary>
    public int Count { get; }

    /// <summary>出現過的型別，出現次數多的在前。</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>寫過的說明（不含空白），出現次數多的在前。</summary>
    public IReadOnlyList<string> Descriptions { get; }

    /// <summary>沒有說明的有幾個。</summary>
    public int MissingDescriptions { get; }

    public bool HasMixedTypes => Types.Count > 1;

    public bool HasMixedDescriptions => Descriptions.Count > 1;

    /// <summary>不只一個，而且型別或說明不一樣；「只看不一致」照這一條。</summary>
    public bool IsInconsistent => Count > 1 && (HasMixedTypes || HasMixedDescriptions);

    /// <summary>空著的那幾格可以照抄的說明；寫過的不只一種、或一個都沒寫時為 null。</summary>
    public string? Suggestion => Descriptions.Count == 1 && MissingDescriptions > 0 ? Descriptions[0] : null;
}

/// <summary>一份資料字典裡每一個資料行名稱的比較結果。</summary>
public sealed class SqlColumnNameGroups
{
    private readonly Dictionary<string, SqlColumnNameGroup> _groups;

    private SqlColumnNameGroups(Dictionary<string, SqlColumnNameGroup> groups) => _groups = groups;

    public static SqlColumnNameGroups Empty { get; } = new(new Dictionary<string, SqlColumnNameGroup>(StringComparer.OrdinalIgnoreCase));

    public int Count => _groups.Count;

    /// <summary>
    /// 照目前的說明（含還沒寫回的編輯）分組。
    /// </summary>
    /// <remarks>
    /// 說明由呼叫端給：使用者剛在一張表上補了說明，其他同名的那幾格就該立刻看得到建議，
    /// 而那一句還沒寫回資料庫。整份重算是線性的，幾萬個資料行也只是一次走訪。
    /// </remarks>
    public static SqlColumnNameGroups Build(SqlDataDictionary dictionary, Func<SqlDescriptionTarget, string, string> currentDescription)
    {
        if (dictionary is null) throw new ArgumentNullException(nameof(dictionary));
        if (currentDescription is null) throw new ArgumentNullException(nameof(currentDescription));

        var tallies = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in dictionary.Objects)
        {
            foreach (var column in item.Columns)
            {
                if (!tallies.TryGetValue(column.Name, out var tally))
                {
                    tallies[column.Name] = tally = new Tally(column.Name);
                }

                tally.Count++;
                tally.Types.Add(column.DataType);

                var description = SqlDescriptionValue.Normalize(currentDescription(item.TargetOf(column), column.Description));
                if (description.Length == 0) tally.Missing++;
                else tally.Descriptions.Add(description);
            }
        }

        var groups = new Dictionary<string, SqlColumnNameGroup>(tallies.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in tallies)
        {
            var tally = pair.Value;
            groups[pair.Key] = new SqlColumnNameGroup(
                tally.Name, tally.Count, tally.Types.Ranked(StringComparer.OrdinalIgnoreCase), tally.Descriptions.Ranked(StringComparer.Ordinal), tally.Missing);
        }

        return new SqlColumnNameGroups(groups);
    }

    public SqlColumnNameGroup? Find(string columnName) =>
        columnName is not null && _groups.TryGetValue(columnName, out var group) ? group : null;

    private sealed class Tally
    {
        public Tally(string name) => Name = name;

        public string Name { get; }

        public int Count { get; set; }

        public int Missing { get; set; }

        public Bag Types { get; } = new();

        public Bag Descriptions { get; } = new();
    }

    /// <summary>值與出現次數；同次數時照第一次出現的順序，畫面上的先後才不會每次重算都跳。</summary>
    private sealed class Bag
    {
        private readonly List<string> _values = new();

        public void Add(string value) => _values.Add(value);

        public IReadOnlyList<string> Ranked(StringComparer comparer)
        {
            if (_values.Count == 0) return Array.Empty<string>();

            var order = new List<string>();
            var counts = new Dictionary<string, int>(comparer);

            foreach (var value in _values)
            {
                if (counts.TryGetValue(value, out var count)) counts[value] = count + 1;
                else
                {
                    counts[value] = 1;
                    order.Add(value);
                }
            }

            return order.Select((value, index) => (value, index))
                .OrderByDescending(entry => counts[entry.value])
                .ThenBy(entry => entry.index)
                .Select(entry => entry.value)
                .ToList();
        }
    }
}
