using System;
using System.IO;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>探測器的判定：產物由它決定，錯一條就是整份目錄跟著錯。</summary>
public sealed class KeywordProberTests : IDisposable
{
    // 腳本從剖析器的訊息資源撈出來的那一組。
    internal static readonly int[] Rejecting =
    {
        46001, 46005, 46006, 46007, 46010, 46014, 46015, 46018, 46019, 46020, 46022, 46023, 46024, 46025, 46026, 46057,
    };

    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "SqlAssist.KeywordGenerator.Tests." + Guid.NewGuid() + ".cache");

    public void Dispose()
    {
        File.Delete(_cachePath);
    }

    [Fact]
    public void 字面值對得回原成員才收_camelCase補底線()
    {
        var literals = Create().ReadLiterals();

        Assert.Contains("SELECT", literals);
        Assert.Contains("CURRENT_TIMESTAMP", literals);
        Assert.DoesNotContain("COMMA", literals);
    }

    [Fact]
    public void 當名字寫會被拒的才是保留字()
    {
        var rejected = Create().RejectedAsIdentifiers(
            new[] { "ORDER", "OUTPUT", "SELECT", "APPLY" },
            new[] { "SELECT ", "SELECT * FROM ", "CREATE TABLE t (" },
            new[] { " FROM t", string.Empty, " int)" });

        Assert.Equal(new[] { "ORDER", "SELECT" }, rejected);
    }

    [Fact]
    public void 接得上的字順序同候選字()
    {
        var words = Create().Probe(
            "SET NOCOUNT ", new[] { "OFF", "SELECT", "ON" }, new[] { "OFF", "SELECT", "ON" }, new[] { string.Empty, " x" }, "Lib_Reader");

        Assert.Equal(new[] { "OFF", "ON" }, words);
    }

    [Fact]
    public void 第一條撐過界線的續尾_照順序取()
    {
        var prober = Create();
        const string probe = "CREATE LOGIN l WITH PASSWORD = ";

        Assert.Equal("'x'", prober.FirstEndingPast(probe, new[] { "1", "'x'" }, probe.Length));
        Assert.Null(prober.FirstEndingPast(probe, new[] { "1", ")" }, probe.Length));
    }

    [Fact]
    public void 整條續尾撐得過才算_後綴可以被拒()
    {
        var prober = Create();

        Assert.Equal(" WHERE a = 1", prober.FirstEndingThrough("SELECT a FROM t", new[] { " )", " WHERE a = 1" }, string.Empty));
        Assert.Equal(" ORDER BY a", prober.FirstEndingThrough("SELECT a FROM t", new[] { " )", " ORDER BY a" }, ","));
    }

    [Fact]
    public void 寫完一項的字不含寫完一句的字()
    {
        var endings = Create().ItemEndings(
            new[] { "BREAK", "DESC", "NULL", "SELECT" },
            new bool[4],
            new[] { new[] { "SELECT " }, new[] { "SELECT * FROM t ORDER BY a " }, new[] { "WHILE 1 = 1 " } },
            "Lib_Reader");

        Assert.Equal(new[] { "DESC", "NULL" }, endings);
    }

    [Fact]
    public void 寫完一句之後只接得了下一句的位置才算收得乾淨()
    {
        var keywords = new[] { "BREAK", "NULL", "SELECT" };
        var endings = Create().StatementEndings(
            keywords,
            new bool[3],
            new[] { "StatementStart", "WhileBody" },
            new[] { new[] { "SELECT 1; " }, new[] { "WHILE 1 = 1 " } },
            new[] { new[] { true, false }, new[] { false, false }, new[] { true, false } },
            keywords,
            new[] { string.Empty, " 1" },
            "Lib_Reader");

        var ending = Assert.Single(endings);
        Assert.Equal("BREAK", ending.Word);
        Assert.Contains("WhileBody", ending.Closes);
    }

    [Fact]
    public void 快取存回再讀_同一段文字不再剖析()
    {
        var first = Create();
        var rejection = first.FirstRejection("SELECT FROM");
        first.SaveCache(true);

        var second = Create();

        Assert.Equal(rejection, second.FirstRejection("SELECT FROM"));
        Assert.StartsWith("剖析 0 次；快取沿用 1 筆、新增 0 筆", second.CacheSummary());
    }

    private KeywordProber Create()
    {
        return new KeywordProber(Rejecting, _cachePath, loadCache: true);
    }
}
