using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// 依一組規則檢查一張資料表的結構。
/// </summary>
/// <remarks>
/// 規則是傳進來的，不是寫死的：使用者關掉的那幾條在建立分析器時就不在清單裡，
/// 而不是跑完再過濾。跑完再濾的話，一條有 bug 的規則仍然會在被關掉之後
/// 拖慢每一次產生指令碼——而使用者關掉它多半正是因為它慢。
///
/// 規則本身不擲例外是契約而不是希望：一條規則炸掉會讓整份指令碼變不出來，
/// 而使用者要的是那份指令碼，不是一則健檢錯誤。<see cref="Analyze"/> 因此把
/// 每一條規則各自圍起來——這裡不分辨例外的種類，任何一種都只代表
/// 「這一條規則這一次沒有意見」。
/// </remarks>
public sealed class SqlSchemaAnalyzer
{
    /// <summary>內建規則，依識別碼排列。</summary>
    public static IReadOnlyList<ISqlSchemaRule> BuiltInRules { get; } = new ISqlSchemaRule[]
    {
        new SqlLargeObjectInIncludeRule(),
        new SqlRedundantIndexRule(),
        new SqlHeapTableRule(),
        new SqlMissingPrimaryKeyRule(),
        new SqlInconsistentColumnTypeRule(),
        new SqlEnumWithoutCheckRule(),
        new SqlMissingForeignKeyRule(),
        new SqlLegacyDateTimeRule(),
        new SqlDeprecatedLargeObjectRule()
    };

    /// <summary>跑全部內建規則。</summary>
    public static SqlSchemaAnalyzer Default { get; } = new(BuiltInRules);

    private readonly IReadOnlyList<ISqlSchemaRule> _rules;

    public SqlSchemaAnalyzer(IReadOnlyList<ISqlSchemaRule> rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>只留下識別碼在 <paramref name="enabledRuleIds"/> 裡的那幾條。</summary>
    /// <remarks>
    /// 傳 null 代表全開。空集合則是「一條都不跑」，與全開是兩回事——
    /// 把兩者混為一談的話，使用者關掉最後一條規則會得到全部打開。
    /// </remarks>
    public static SqlSchemaAnalyzer ForRules(IReadOnlyCollection<string>? enabledRuleIds)
    {
        if (enabledRuleIds is null)
        {
            return Default;
        }

        var enabled = new HashSet<string>(enabledRuleIds, StringComparer.OrdinalIgnoreCase);
        var rules = new List<ISqlSchemaRule>();

        foreach (var rule in BuiltInRules)
        {
            if (enabled.Contains(rule.Id))
            {
                rules.Add(rule);
            }
        }

        return new SqlSchemaAnalyzer(rules);
    }

    /// <summary>
    /// 跑一次健檢；順序是規則的順序，同一條規則之內是資料行的順序。
    /// </summary>
    /// <remarks>
    /// 順序穩定不是為了好看：發現會寫進指令碼的註解，而那份指令碼要進得了
    /// 版本控制。順序每次不同的話，兩份內容相同的指令碼會 diff 出一整片紅。
    /// </remarks>
    public IReadOnlyList<SqlSchemaFinding> Analyze(SqlObjectStructure structure)
    {
        if (structure is null)
        {
            throw new ArgumentNullException(nameof(structure));
        }

        var findings = new List<SqlSchemaFinding>();

        foreach (var rule in _rules)
        {
            Collect(rule, structure, findings);
        }

        return findings;
    }

    /// <remarks>
    /// 規則是延遲列舉的，所以例外會在 <c>foreach</c> 當中才冒出來，
    /// 不是在呼叫 <c>Analyze</c> 的那一行——圍在這裡才接得到。
    /// 接住之後那一條規則這一次的發現整組丟掉：半份發現比沒有發現更難懂。
    /// </remarks>
    private static void Collect(
        ISqlSchemaRule rule,
        SqlObjectStructure structure,
        List<SqlSchemaFinding> findings)
    {
        var produced = new List<SqlSchemaFinding>();

        try
        {
            foreach (var finding in rule.Analyze(structure))
            {
                produced.Add(finding);
            }
        }
        catch (Exception)
        {
            return;
        }

        findings.AddRange(produced);
    }
}
