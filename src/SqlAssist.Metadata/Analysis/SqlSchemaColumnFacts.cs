using System;
using System.Collections.Generic;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Analysis;

/// <summary>
/// 規則之間共用的幾個問法。
/// </summary>
/// <remarks>
/// 每條規則各自掃一次索引、各自從擴充屬性裡挑出說明，寫起來短，代價是同一份
/// 判斷散成好幾份——而其中一份漏掉「唯一條件約束也算候選鍵」的那一天，
/// 只有那一條規則會開始誤報，看起來像是那條規則自己的問題。
/// </remarks>
internal static class SqlSchemaColumnFacts
{
    private const string DescriptionProperty = "MS_Description";

    /// <summary>資料行名稱換成它的 <c>MS_Description</c>；沒有說明的資料行不在裡面。</summary>
    public static Dictionary<string, string> DescriptionsByColumn(SqlObjectStructure structure)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in structure.ExtendedProperties)
        {
            if (property.Level == SqlExtendedPropertyLevel.Column &&
                property.TargetName is { Length: > 0 } column &&
                string.Equals(property.Name, DescriptionProperty, StringComparison.OrdinalIgnoreCase))
            {
                result[column] = property.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// 這個資料行是不是本表自己的識別碼。
    /// </summary>
    /// <remarks>
    /// 主索引鍵之外，<b>單一資料行的唯一索引或唯一條件約束</b>也算：那種資料行是
    /// 這張表的候選鍵，不是指向別張表的外來鍵。漏掉這一半的話，一個
    /// <c>PublicId uniqueidentifier</c> 會被當成「疑似外鍵卻沒有 FK」報出來，
    /// 而那正是使用者最先失去信任的那種誤報。
    /// </remarks>
    public static HashSet<string> KeyColumns(SqlObjectStructure structure)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in structure.Indexes)
        {
            if (!index.IsPrimaryKey && !index.IsUnique && !index.IsUniqueConstraint)
            {
                continue;
            }

            var keys = KeyColumnNames(index);

            // 複合鍵不算：那裡面的每一個資料行單獨都可能是外來鍵。
            if (keys.Count == 1)
            {
                result.Add(keys[0]);
            }
        }

        return result;
    }

    /// <summary>索引鍵資料行的名稱，依索引鍵順序；不含 INCLUDE 的那些。</summary>
    public static List<string> KeyColumnNames(SqlIndexInfo index)
    {
        var names = new List<string>();

        foreach (var column in index.Columns)
        {
            if (!column.IsIncluded)
            {
                names.Add(column.Name);
            }
        }

        return names;
    }

    /// <summary>資料行名稱換成資料行；查不到時為 null。</summary>
    public static Dictionary<string, SqlColumnInfo> ByName(SqlObjectStructure structure)
    {
        var result = new Dictionary<string, SqlColumnInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in structure.Columns)
        {
            result[column.Name] = column;
        }

        return result;
    }

    /// <summary>
    /// 名稱最後一個駝峰詞，例如 <c>CreateUser</c> 得到 <c>User</c>。
    /// </summary>
    /// <remarks>
    /// 用來把同語意的資料行分在一起。全大寫的縮寫（<c>LoanID</c>）取到的是
    /// <c>ID</c> 而不是 <c>D</c>：連續的大寫算同一個詞。
    /// 分不出詞（全小寫、全大寫）時整個名稱就是它自己的後綴。
    /// </remarks>
    public static string SuffixOf(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        for (var index = name.Length - 1; index > 0; index--)
        {
            if (!char.IsUpper(name[index]))
            {
                continue;
            }

            // 連續大寫往前收：ID、No、URL 這一類縮寫不該被切開。
            var start = index;

            while (start > 0 && char.IsUpper(name[start - 1]))
            {
                start--;
            }

            return name.Substring(start);
        }

        return name;
    }
}
