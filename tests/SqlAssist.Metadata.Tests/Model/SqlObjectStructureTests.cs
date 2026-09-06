using System.Collections.Generic;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using Xunit;

namespace SqlAssist.Metadata.Tests.Model;

/// <summary>
/// 結構模型自己答得出來的那幾件事：多列結果怎麼合併、這一次的資料夠不夠，
/// 以及不夠時要說什麼。
/// </summary>
/// <remarks>
/// 排版不在這裡：那是 <see cref="TSqlScriptRenderer"/> 的事，測試在
/// <c>Formatting/TSqlScriptRendererTests</c>。兩邊都驗排版的話，改一個選項
/// 要改兩份期望值，而漏掉的那一份會擋在無關的測試上。
/// </remarks>
public sealed class SqlObjectStructureTests
{
    /// <summary>換行固定成 <c>\n</c>，期望值才寫得出來。</summary>
    private static SqlScriptContext Context() =>
        new(SqlScriptOptions.Fidelity, newLine: "\n");

    private static SqlObjectInfo Table() => new(1, "dbo", "Lib_Reader", SqlObjectKind.Table);

    private static SqlColumnInfo Column(
        int ordinal,
        string name,
        string type,
        bool nullable,
        bool identity = false,
        bool primaryKey = false,
        string? defaultDefinition = null,
        bool computed = false,
        string? computedDefinition = null)
    {
        return new SqlColumnInfo(
            ordinal,
            name,
            type,
            nullable,
            identity,
            computed,
            primaryKey,
            defaultDefinition,
            computedDefinition);
    }

    [Fact]
    public void 索引的多列結果依索引合併()
    {
        var rows = new List<SqlIndexRow>
        {
            new(1, "PK_Lib_Reader", true, true, false, "CLUSTERED", null, "Id", false, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "Last", false, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "First", true, false),
            new(2, "IX_Name", false, false, false, "NONCLUSTERED", null, "Email", false, true)
        };

        var indexes = SqlIndexInfo.FromRows(rows);

        Assert.Equal(2, indexes.Count);
        Assert.Equal("PK_Lib_Reader", indexes[0].Name);
        Assert.True(indexes[0].IsPrimaryKey);
        Assert.Equal("Last ASC, First DESC", indexes[1].DescribeKeyColumns());
        Assert.Equal("Email", indexes[1].DescribeIncludedColumns());
    }

    [Fact]
    public void 以index_id分界而不是名稱()
    {
        // 查詢是依 index_id 排序的，合併也必須以它為準；
        // 改用名稱分界的話，只要有兩個索引恰好同名就會被併成一個。
        var rows = new List<SqlIndexRow>
        {
            new(1, "IX_A", false, false, false, "CLUSTERED", null, "Id", false, false),
            new(2, "IX_A", false, false, false, "NONCLUSTERED", null, "Id", false, false)
        };

        Assert.Equal(2, SqlIndexInfo.FromRows(rows).Count);
    }

    [Fact]
    public void 外來鍵的多列結果依名稱合併()
    {
        var rows = new List<SqlForeignKeyRow>
        {
            new("FK_Loan_Reader", "dbo", "Lib_Reader", "UserId", "Id", "CASCADE", "NO_ACTION"),
            new("FK_Loan_Copy", "dbo", "Copy", "CopyId", "Id", "NO_ACTION", "NO_ACTION"),
            new("FK_Loan_Copy", "dbo", "Copy", "CopyKind", "Kind", "NO_ACTION", "NO_ACTION")
        };

        var keys = SqlForeignKeyInfo.FromRows(rows);

        Assert.Equal(2, keys.Count);
        Assert.Equal("ON DELETE CASCADE", keys[0].DescribeActions());
        Assert.Equal(2, keys[1].Columns.Count);
        Assert.Equal("CopyId, CopyKind → [dbo].[Copy].Id, Kind", keys[1].DescribeColumns());
    }

    /// <remarks>
    /// 「有沒有參考動作」只有這一份判斷，清單顯示與指令碼都問它。各自比對字面值的話，
    /// 其中一份漏掉 <c>NO_ACTION</c> 以外的新動作就會寫出兩種說法。
    /// </remarks>
    [Fact]
    public void 沒有參考動作時不顯示動作()
    {
        var key = new SqlForeignKeyInfo(
            "FK_Loan_Copy",
            "dbo",
            "Copy",
            new[] { new SqlForeignKeyColumn("CopyId", "Id") });

        Assert.Equal(string.Empty, key.DescribeActions());
        Assert.False(key.HasDeleteAction);
        Assert.False(key.HasUpdateAction);
    }

    [Fact]
    public void 有參考動作時兩個問法一致()
    {
        var key = new SqlForeignKeyInfo(
            "FK_Loan_Reader",
            "dbo",
            "Lib_Reader",
            new[] { new SqlForeignKeyColumn("UserId", "Id") },
            deleteAction: "CASCADE",
            updateAction: "SET_NULL");

        Assert.True(key.HasDeleteAction);
        Assert.True(key.HasUpdateAction);
        Assert.Contains("ON DELETE CASCADE", key.DescribeActions());
    }

    [Fact]
    public void 模組類物件的指令碼就是定義本文()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(2, "dbo", "usp_GetBook", SqlObjectKind.Procedure),
                parameters: new[] { new SqlParameterInfo(1, "@Id", "int", false) },
                definition: "CREATE PROCEDURE dbo.usp_GetBook @Id int AS SELECT 1;"));

        Assert.StartsWith(
            "CREATE PROCEDURE dbo.usp_GetBook @Id int AS SELECT 1;",
            structure.BuildScript(Context()));
    }

    /// <summary>
    /// 檢視同時是模組也有欄位。定義取不到時原本會掉進 CREATE TABLE 那一支，
    /// 於是一個檢視被寫成一張同名的資料表——照著執行就真的多出一張表。
    /// </summary>
    /// <remarks>
    /// OBJECT_DEFINITION 傳回 NULL 的兩個原因（WITH ENCRYPTION、沒有
    /// VIEW DEFINITION 權限）要寫在輸出裡，否則使用者查不出為什麼沒有指令碼。
    /// </remarks>
    [Fact]
    public void 取不到定義的檢視不會被寫成資料表()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(3, "dbo", "v_Loan", SqlObjectKind.View),
                new[]
                {
                    Column(1, "LoanId", "int", nullable: false),
                    Column(2, "CopyNo", "varchar(10)", nullable: true)
                }));

        var script = structure.BuildScript(Context());

        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.Contains("取不到 [dbo].[v_Loan] 的定義", script);
        Assert.Contains("VIEW DEFINITION", script);

        // 查得到的欄位仍然要看得到，只是整段都是註解——這裡沒有一行執行得動。
        Assert.Contains("--     [LoanId] int NOT NULL", script);

        AssertEveryLineIsComment(script);
    }

    /// <summary>
    /// 查詢成功卻一個欄位都沒有回來：組出來的 CREATE TABLE 只剩一對空括號，
    /// 而那仍然貼得上去——執行下去建出一張沒有欄位的資料表。
    /// </summary>
    /// <remarks>
    /// 這一格與模組取不到定義是同一類問題，因此走同一份輸出：整段註解、
    /// 寫明缺什麼與兩個可能的原因。原因不寫進去的話，使用者查不出該去看權限、
    /// 看物件還在不在，還是看連線。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Table)]
    [InlineData(SqlObjectKind.TableType)]
    public void 取不到欄位時整段註解(SqlObjectKind kind)
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(7, "dbo", "Lib_Tag", kind)));

        var script = structure.BuildScript(Context());

        Assert.False(structure.CanBuildExecutableScript);
        Assert.Contains("取不到 [dbo].[Lib_Tag] 的欄位", script);
        Assert.Contains("sys.columns", script);
        Assert.DoesNotContain("CREATE TABLE", script);
        Assert.DoesNotContain("CREATE TYPE", script);

        AssertEveryLineIsComment(script);
    }

    /// <summary>
    /// 「這一次的資料夠不夠」只有一份判斷，組指令碼與問這個屬性走的是同一條。
    /// </summary>
    /// <remarks>
    /// 分成兩份的症狀是屬性說寫得出來、組出來的卻是一段註解——新的表面要問
    /// 「這份結構能不能給使用者一段可以執行的東西」時，問的就是這個屬性。
    /// </remarks>
    [Fact]
    public void 資料夠不夠與組出來的東西一致()
    {
        var ready = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[] { Column(1, "Id", "int", nullable: false) }));

        var missingColumns = new SqlObjectStructure(new SqlObjectDetail(Table()));

        var missingDefinition = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(8, "dbo", "v_Loan", SqlObjectKind.View),
                new[] { Column(1, "LoanId", "int", nullable: false) }));

        // 同義字的定義是 sys.synonyms 上的一個欄位，查不到那一列就跟模組
        // 取不到定義一樣，是「這一輪的資料不齊」而不是「這一類寫不出來」。
        var synonym = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(9, "dbo", "syn_Loan", SqlObjectKind.Synonym)));

        Assert.True(ready.CanBuildExecutableScript);
        Assert.False(missingColumns.CanBuildExecutableScript);
        Assert.False(missingDefinition.CanBuildExecutableScript);
        Assert.False(synonym.CanBuildExecutableScript);

        Assert.StartsWith("CREATE TABLE", ready.BuildScript(Context()));
        Assert.StartsWith("-- 取不到", missingColumns.BuildScript(Context()));
        Assert.StartsWith("-- 取不到", missingDefinition.BuildScript(Context()));
    }

    /// <remarks>
    /// 同義字與序列的定義由 <c>SqlCatalogScript</c> 從目錄檢視組出來，放進
    /// <c>Definition</c>；到了這裡與模組拿到定義原文走的是同一條路。
    /// </remarks>
    [Theory]
    [InlineData(SqlObjectKind.Synonym, "CREATE SYNONYM [dbo].[syn_Loan]\nFOR [Lib].[dbo].[Loan];")]
    [InlineData(SqlObjectKind.Sequence, "CREATE SEQUENCE [dbo].[seq_LoanNo]\n    AS int;")]
    public void 目錄檢視的定義就是指令碼(SqlObjectKind kind, string definition)
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(10, "dbo", "obj", kind),
                definition: definition));

        Assert.True(structure.CanBuildExecutableScript);
        Assert.StartsWith(definition, structure.BuildScript(Context()));
    }

    /// <remarks>
    /// 缺定義的原因要說對：同義字的定義從來不經過加密與 VIEW DEFINITION 權限
    /// 那兩關，照模組的說法寫會讓使用者去查一個不存在的原因。
    /// </remarks>
    [Fact]
    public void 查不到同義字那一列時說的是目錄檢視()
    {
        var script = new SqlObjectStructure(
            new SqlObjectDetail(new SqlObjectInfo(11, "dbo", "syn_Loan", SqlObjectKind.Synonym)))
            .BuildScript(Context());

        Assert.Contains("取不到 [dbo].[syn_Loan] 的定義", script);
        Assert.Contains("sys.synonyms", script);
        Assert.DoesNotContain("OBJECT_DEFINITION", script);
    }

    /// <summary>取不到定義的程序列出參數，理由與檢視列出欄位相同。</summary>
    [Fact]
    public void 取不到定義的程序列出參數()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(4, "dbo", "usp_Renew", SqlObjectKind.Procedure),
                parameters: new[] { new SqlParameterInfo(1, "@LoanId", "int", false) }));

        var script = structure.BuildScript(Context());

        Assert.Contains("取不到 [dbo].[usp_Renew] 的定義", script);
        Assert.Contains("--     @LoanId int", script);
    }

    /// <summary>
    /// 第四層查詢失敗與「查詢成功卻沒有索引」必須分得開。
    /// </summary>
    /// <remarks>
    /// 兩者在模型上長得一模一樣——都是空的索引清單——但答案相反：後者的答案就是
    /// 沒有索引，前者是還沒問到。混成同一件事的症狀是一張有五個索引、兩個外來鍵
    /// 與一個觸發程序的資料表被重建成一張什麼都沒有的資料表，而它照樣貼得上去。
    /// 這與少了欄位的 <c>CREATE TABLE</c> 是同一條理由。
    /// </remarks>
    [Fact]
    public void 第四層失敗時整段註解()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[] { Column(1, "ReaderId", "int", nullable: false) }),
            structureUnavailable: true);

        var script = structure.BuildScript(Context());

        Assert.True(structure.IsStructureUnavailable);
        Assert.False(structure.CanBuildExecutableScript);
        Assert.Contains("取不到 [dbo].[Lib_Reader] 的索引與條件約束", script);
        Assert.DoesNotContain("CREATE TABLE", script);

        // 查得到的部分照樣列出來：那是這一輪唯一真的問到的東西。
        Assert.Contains("ReaderId", script);

        AssertEveryLineIsComment(script);
    }

    /// <remarks>
    /// 空的索引清單本身不是失敗。沒有這一條的話，把「不完整」判成預設值就沒有
    /// 任何徵兆——每一張沒有索引的資料表都會變成一段註解。
    /// </remarks>
    [Fact]
    public void 沒有索引的資料表照樣寫得出指令碼()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                Table(),
                new[] { Column(1, "ReaderId", "int", nullable: false) }));

        Assert.False(structure.IsStructureUnavailable);
        Assert.True(structure.CanBuildExecutableScript);
        Assert.StartsWith("CREATE TABLE", structure.BuildScript(Context()));
    }

    /// <remarks>
    /// 檢視與模組的指令碼是定義原文，第四層在那一族身上只帶擴充屬性。
    /// 讓它們也掉進「資料不齊」那一支，等於一條查詢失敗就連定義都不給了，
    /// 而那份文字明明已經在手上。
    /// </remarks>
    [Fact]
    public void 第四層失敗不影響以定義為指令碼的那一族()
    {
        var structure = new SqlObjectStructure(
            new SqlObjectDetail(
                new SqlObjectInfo(8, "dbo", "v_Loan", SqlObjectKind.View),
                new[] { Column(1, "LoanId", "int", nullable: false) },
                definition: "CREATE VIEW dbo.v_Loan AS SELECT 1 AS LoanId;"),
            structureUnavailable: true);

        Assert.True(structure.CanBuildExecutableScript);
    }

    private static void AssertEveryLineIsComment(string script)
    {
        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim();
            Assert.True(trimmed.Length == 0 || trimmed.StartsWith("--"), line);
        }
    }
}
