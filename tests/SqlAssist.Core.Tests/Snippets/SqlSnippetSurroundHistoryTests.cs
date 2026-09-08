using System;
using System.Collections.Generic;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

/// <remarks>
/// 這一份守的是「開起來按 Enter 會拿到哪一筆」。原本的答案是設定檔裡排最前面的
/// 那一筆，於是任何人（內建新增、或使用者把自己片段的一格命名成 surround）在前面
/// 的分類多加一筆，就會換掉別人按慣的動作——而清單本身看起來完全正常。
/// </remarks>
public sealed class SqlSnippetSurroundHistoryTests : IDisposable
{
    public void Dispose() => SqlSnippetSurroundHistory.Clear();

    [Fact]
    public void 還沒用過時預選第一筆()
    {
        SqlSnippetSurroundHistory.Clear();

        Assert.Equal(0, SqlSnippetSurroundHistory.PreferredIndex(Candidates("a", "b", "c")));
    }

    [Fact]
    public void 用過之後預選上一次那一筆()
    {
        var candidates = Candidates("a", "b", "c");

        SqlSnippetSurroundHistory.Record(candidates[2]);

        Assert.Equal(2, SqlSnippetSurroundHistory.PreferredIndex(candidates));
    }

    /// <remarks>
    /// 清單前面多一筆不會換掉預選項——這正是 <c>cp</c> 進包夾清單時，把
    /// <c>be</c> 從第一順位擠掉的那一種變化。
    /// </remarks>
    [Fact]
    public void 清單前面多一筆也不會換掉預選項()
    {
        SqlSnippetSurroundHistory.Record(Snippet("b"));

        Assert.Equal(2, SqlSnippetSurroundHistory.PreferredIndex(Candidates("z", "a", "b", "c")));
    }

    /// <remarks>
    /// 記的是識別碼不是捷徑：使用者在管理介面把捷徑改掉之後，那仍然是同一筆片段，
    /// 預選也應該還在它身上。
    /// </remarks>
    [Fact]
    public void 改掉捷徑之後仍然認得同一筆()
    {
        SqlSnippetSurroundHistory.Record(Snippet("b"));

        var renamed = new[]
        {
            Snippet("a"),
            new SqlSnippet("bb", "BEGIN\n    $surround$\nEND", id: "builtin.b")
        };

        Assert.Equal(1, SqlSnippetSurroundHistory.PreferredIndex(renamed));
    }

    /// <remarks>
    /// 記著的那一筆被停用或刪掉之後回到第一筆：預選一個看不見的項目等於沒有預選，
    /// 而使用者按下 Enter 時什麼都不會發生。
    /// </remarks>
    [Fact]
    public void 那一筆不在清單裡時回到第一筆()
    {
        SqlSnippetSurroundHistory.Record(Snippet("gone"));

        Assert.Equal(0, SqlSnippetSurroundHistory.PreferredIndex(Candidates("a", "b")));
    }

    [Fact]
    public void 沒有候選時不指向任何一筆()
    {
        Assert.Equal(-1, SqlSnippetSurroundHistory.PreferredIndex(Array.Empty<SqlSnippet>()));
        Assert.Equal(-1, SqlSnippetSurroundHistory.PreferredIndex(null));
    }

    /// <remarks>沒有識別碼的片段退回捷徑，不是整個不記。</remarks>
    [Fact]
    public void 沒有識別碼的片段以捷徑記()
    {
        var candidates = new[]
        {
            new SqlSnippet("a", "SELECT 1;"),
            new SqlSnippet("b", "SELECT 2;")
        };

        SqlSnippetSurroundHistory.Record(candidates[1]);

        Assert.Equal(1, SqlSnippetSurroundHistory.PreferredIndex(candidates));
    }

    private static IReadOnlyList<SqlSnippet> Candidates(params string[] names)
    {
        var result = new List<SqlSnippet>(names.Length);

        foreach (var name in names)
        {
            result.Add(Snippet(name));
        }

        return result;
    }

    private static SqlSnippet Snippet(string name)
    {
        return new SqlSnippet(name, "BEGIN\n    $surround$\nEND", id: "builtin." + name);
    }
}
