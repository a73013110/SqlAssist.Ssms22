using System.Linq;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Parsing;
using Xunit;

namespace SqlAssist.Core.Tests.Parsing;

/// <summary>
/// <c>ON</c> 後面接的是資料表還是述詞。
/// </summary>
/// <remarks>
/// 兩個方向都要守，而且反方向比正方向重要：把 JOIN 條件誤判成資料來源，
/// 症狀不是「多幾個候選」而是 <c>ON b.|</c> 完全列不出欄位——那是每天都會走到的
/// 路徑，而索引 DDL 一天寫不到一次。
///
/// 建議目標與範圍分析共用同一份判斷（<see cref="SqlDdlTarget"/>），
/// 所以這裡兩邊一起比：各寫一份的症狀是清單列得出資料表、欄位卻一個都沒有。
/// </remarks>
public sealed class SqlDdlTargetTests
{
    [Theory]
    [InlineData("CREATE NONCLUSTERED INDEX IX_TableName_ColumnName\nON ")]
    [InlineData("CREATE UNIQUE INDEX IX_TableName_ColumnName ON ")]
    [InlineData("CREATE INDEX IX_TableName_ColumnName ON dbo.")]
    [InlineData("DROP INDEX IX_TableName_ColumnName ON ")]

    // ALTER INDEX ALL ON t 的 ALL 是關鍵字，但那個位置仍然是索引的名稱格。
    [InlineData("ALTER INDEX ALL ON ")]
    [InlineData("CREATE STATISTICS st_Name ON ")]
    [InlineData("CREATE TRIGGER dbo.tr_Name\nON ")]
    [InlineData("CREATE OR ALTER TRIGGER dbo.tr_Name\nON ")]
    public void DDL的ON後面是資料表(string textBeforeCaret)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataSource, context.Target);

        // 只要名稱，不要把整份定義或 INSERT 骨架塞進去。
        Assert.Equal(CompletionIntent.Reference, context.Intent);
    }

    /// <remarks>
    /// 反方向。<c>GRANT SELECT ON t</c> 的 <c>ON</c> 前面是關鍵字而不是名稱單位，
    /// 因此不必特別排除；<c>CREATE INDEX … (a) ON [PRIMARY]</c> 的檔案群組前面是
    /// 右括號，同樣由「前面必須是一個名稱單位」擋掉。
    /// </remarks>
    [Theory]
    [InlineData("SELECT * FROM dbo.Loan a\nINNER JOIN dbo.Copy b\nON ")]
    [InlineData("MERGE INTO dbo.Loan AS target\nUSING dbo.Copy AS source\nON ")]
    [InlineData("GRANT SELECT ON ")]
    [InlineData("CREATE INDEX IX_TableName_ColumnName ON dbo.Loan (LoanId)\nON ")]
    public void 述詞與檔案群組的ON不是資料表(string textBeforeCaret)
    {
        Assert.NotEqual(
            CompletionTarget.DataSource,
            SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <remarks>
    /// JOIN 條件裡的限定字仍然要解析成資料表的欄位。誤判成 DDL 的 <c>ON</c> 之後
    /// 這裡會退回「<c>b</c> 是結構描述」的解讀，而沒有任何物件屬於名為 <c>b</c>
    /// 的結構描述——清單是空的。
    /// </remarks>
    [Fact]
    public void JOIN條件的限定字照樣列欄位()
    {
        const string sql = "SELECT * FROM dbo.Loan a INNER JOIN dbo.Copy b ON b.";
        var context = SqlCompletionContextAnalyzer.Analyze(sql, sql.Length);

        Assert.Equal(CompletionTarget.Column, context.Target);
        Assert.Equal("Copy", Assert.Single(context.ColumnSources!).Table!.ObjectName);
    }

    /// <remarks>
    /// 範圍分析這一半：資料表格有清單還不夠，資料行格要列得出<b>那張表</b>的欄位。
    /// 這一格沒有限定字也推不出目標，因此與 <c>SELECT |</c> 一樣打了字才有清單，
    /// 而打了字之後敘述範圍必須交得出 <c>ON</c> 後面那張表。
    /// </remarks>
    [Theory]
    [InlineData("CREATE NONCLUSTERED INDEX IX_TableName_ColumnName\nON dbo.Lib_Reader (C")]
    [InlineData("CREATE NONCLUSTERED INDEX IX_TableName_ColumnName\nON dbo.Lib_Reader (ReaderId, C")]
    [InlineData("CREATE STATISTICS st_Name ON dbo.Lib_Reader (C")]
    public void 索引與統計資料的資料行格看得到那張表(string sql)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(sql, sql.Length);

        Assert.True(context.IsValid);
        Assert.Equal("Lib_Reader", Assert.Single(context.ScopeSources).Table!.ObjectName);
    }

    /// <remarks>
    /// 範圍分析不能因此把 JOIN 條件的 <c>ON</c> 也收成資料來源：那會讓同一張表
    /// 出現兩次，而重複的來源會讓「敘述裡有幾個相異限定字」數錯，插入的欄位
    /// 就會多補一個其實不需要的別名。
    /// </remarks>
    [Fact]
    public void JOIN的ON不會讓資料來源重複()
    {
        // 前綴不能是空的：沒有限定字又推不出目標的位置本來就要打了字才參與，
        // 而不參與時整趟範圍解析會被跳過。
        const string sql = "SELECT * FROM dbo.Loan a INNER JOIN dbo.Copy b ON b.LoanId = a.LoanId WHERE L";
        var context = SqlCompletionContextAnalyzer.Analyze(sql, sql.Length);

        Assert.Equal(
            new[] { "Loan", "Copy" },
            context.ScopeSources.Select(source => source.Table!.ObjectName).ToArray());
    }
}
