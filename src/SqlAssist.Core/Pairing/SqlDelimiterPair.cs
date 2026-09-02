namespace SqlAssist.Core.Pairing;

/// <summary>一組成對的分隔字元。</summary>
public readonly struct SqlDelimiterPair
{
    public SqlDelimiterPair(char open, char close)
    {
        Open = open;
        Close = close;
    }

    public char Open { get; }

    public char Close { get; }
}

/// <summary>
/// 自動配對認得的分隔字元。
/// </summary>
/// <remarks>
/// 刻意<b>不含</b> <c>&lt;</c> 與 <c>&gt;</c>：它們在 T-SQL 裡絕大多數時候是比較運算子，
/// <c>a &lt; b</c> 每打一次就補一個右角括號是純粹的干擾。方括號則相反——它是識別字的
/// 跳脫寫法，而且建議清單不會在方括號裡開啟（詞元起點的語彙狀態不是程式碼），
/// 所以補上的 <c>]</c> 不會與提交建議時寫進去的名稱互相踩到。
///
/// 引號的兩端是同一個字元，因此「這一個是要開還是要關」無法只看字元本身，
/// 必須問語彙狀態；<c>SqlAutoPairAnalyzer</c> 的每一條規則都因此要從頭掃過來。
/// </remarks>
public static class SqlDelimiterPairs
{
    private static readonly SqlDelimiterPair[] Pairs =
    {
        new('(', ')'),
        new('\'', '\''),
        new('[', ']'),
        new('"', '"')
    };

    public static bool TryFromOpen(char value, out SqlDelimiterPair pair)
    {
        foreach (var candidate in Pairs)
        {
            if (candidate.Open == value)
            {
                pair = candidate;
                return true;
            }
        }

        pair = default;
        return false;
    }

    /// <summary>這個字元是某一組配對的其中一端。</summary>
    /// <remarks>
    /// 按鍵路徑上的第一道篩選：不是配對字元的按鍵連游標、選取範圍與設定都不必問，
    /// 而打字時絕大多數按鍵都在這一步就結束。
    /// </remarks>
    public static bool IsPairCharacter(char value)
    {
        foreach (var candidate in Pairs)
        {
            if (candidate.Open == value || candidate.Close == value)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsClose(char value)
    {
        foreach (var candidate in Pairs)
        {
            if (candidate.Close == value)
            {
                return true;
            }
        }

        return false;
    }
}
