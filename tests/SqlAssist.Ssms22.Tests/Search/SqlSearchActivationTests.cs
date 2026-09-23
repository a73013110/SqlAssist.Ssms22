using System;
using System.Collections.Generic;
using SqlAssist.Core.Search;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Search;
using Xunit;

namespace SqlAssist.Ssms22.Tests.Search;

/// <summary>
/// 一筆結果屬於哪一台，由酬載自己說；接線層不照目前的範圍代答。
/// </summary>
public sealed class SqlSearchActivationTests
{
    private static readonly SqlSearchOrigin Origin = new("LIBSQL02");

    [Fact]
    public void 目錄物件交出搜到它的那一台()
    {
        var hit = Hit(new SqlCatalogSearchTarget(
            Origin, "Library", "dbo", "Cat_BookCopy", SqlObjectKind.Constraint, 42));

        Assert.Same(Origin, SqlSearchActivation.OriginOf(hit));
    }

    [Fact]
    public void 作業交出連線那一台而不是自報的名字()
    {
        var hit = Hit(new SqlAgentJobSearchTarget(
            Origin, "LIBSQL01", Guid.Empty, "Lib_Loan 夜間維護", isEnabled: true));

        Assert.Same(Origin, SqlSearchActivation.OriginOf(hit));
    }

    /// <summary>預覽只對目錄物件讀定義；作業與沒有酬載的來源只剩片段。</summary>
    [Fact]
    public void 只有目錄物件有定義可以預覽()
    {
        var catalog = CatalogHit();

        Assert.Same(catalog.ActivatePayload, SqlSearchActivation.DefinitionOf(catalog));
        Assert.Null(SqlSearchActivation.DefinitionOf(JobHit(step: 1)));
        Assert.Null(SqlSearchActivation.DefinitionOf(Hit("片段")));
        Assert.Null(SqlSearchActivation.DefinitionOf(null));
    }

    /// <summary>不是伺服器上的東西就沒有伺服器，不回一個猜的。</summary>
    [Fact]
    public void 不是伺服器上的東西沒有伺服器()
    {
        Assert.Null(SqlSearchActivation.OriginOf(null));
        Assert.Null(SqlSearchActivation.OriginOf(Hit("片段")));
        Assert.Null(SqlSearchActivation.OriginOf(Hit(null)));
    }

    [Fact]
    public void 同一台沿用查詢視窗的連線_名稱大小寫不算不同台()
    {
        Assert.False(SqlSearchActivation.OpensUnconnected(CatalogHit(), "libsql02"));
    }

    [Fact]
    public void 別台或沒有查詢視窗時開未連線的視窗()
    {
        Assert.True(SqlSearchActivation.OpensUnconnected(CatalogHit(), "LIBSQL01"));
        Assert.True(SqlSearchActivation.OpensUnconnected(CatalogHit(), null));
        Assert.True(SqlSearchActivation.OpensUnconnected(JobHit(step: 2), "LIBSQL01"));
    }

    /// <summary>沒有東西可開的那一列不說「會開未連線的視窗」：它根本不開視窗。</summary>
    [Fact]
    public void 沒有東西可開時不說會開未連線的視窗()
    {
        Assert.False(SqlSearchActivation.OpensUnconnected(Hit("片段"), null));
        Assert.False(SqlSearchActivation.OpensUnconnected(null, null));
    }

    [Fact]
    public void 未連線時名稱與描述在按下去之前就說出來自哪一台()
    {
        var hit = CatalogHit();

        Assert.Equal("移至定義", SqlSearchActivation.ActivateLabel(false));
        Assert.Equal("移至定義（未連線）", SqlSearchActivation.ActivateLabel(true));
        Assert.DoesNotContain("未連線", SqlSearchActivation.Describe(hit));

        var unconnected = SqlSearchActivation.Describe(hit, unconnected: true);
        Assert.StartsWith("在未連線的新查詢視窗開啟 Library 的 Cat_BookCopy 定義", unconnected);
        Assert.Contains("LIBSQL02", unconnected);

        Assert.StartsWith("在未連線的新查詢視窗開啟 LIBSQL01 上 Lib_Loan 夜間維護 第 2 步的命令",
            SqlSearchActivation.Describe(JobHit(step: 2), unconnected: true));
    }

    [Fact]
    public void 成功那一句說得出視窗有沒有連線()
    {
        var hit = CatalogHit();

        Assert.Equal("已在新查詢視窗開啟 Cat_BookCopy 的定義。", SqlSearchActivation.DescribeOpened(hit, false));
        Assert.Equal("已在未連線的新查詢視窗開啟 Cat_BookCopy 的定義；按 F5 時請連到 LIBSQL02。",
            SqlSearchActivation.DescribeOpened(hit, true));
        Assert.EndsWith("的命令；按 F5 時請連到 LIBSQL02。", SqlSearchActivation.DescribeOpened(JobHit(step: 2), true));
    }

    /// <summary>檔頭整段都是註解，按 F5 不會多執行任何一句；伺服器寫的是連線對話框要填的那一個。</summary>
    [Fact]
    public void 未連線檔頭寫明來源伺服器與資料庫且整段是註解()
    {
        var header = SqlSearchActivation.UnconnectedHeader(CatalogHit(), "\n");

        Assert.Equal(
            "-- 這個查詢視窗沒有連線。定義來自 LIBSQL02 上的 Library 資料庫。\n" +
            "-- 按 F5 時 SSMS 會要求連線：請連到 LIBSQL02，並把資料庫切到 Library 再執行。\n\n",
            header);
        Assert.All(header.TrimEnd('\n').Split('\n'), line => Assert.StartsWith("--", line));
    }

    [Fact]
    public void 作業步驟的檔頭寫連線那一台與步驟的資料庫()
    {
        Assert.Equal(
            "-- 這個查詢視窗沒有連線。命令來自 LIBSQL02 上的作業 Lib_Loan 夜間維護 第 2 步。\n" +
            "-- 按 F5 時 SSMS 會要求連線：請連到 LIBSQL02，並把資料庫切到 Library 再執行。\n\n",
            SqlSearchActivation.UnconnectedHeader(JobHit(step: 2, database: "Library"), "\n"));

        // 整個作業沒有單一的資料庫可以說，只說伺服器。
        Assert.Equal(
            "-- 這個查詢視窗沒有連線。命令來自 LIBSQL02 上的作業 Lib_Loan 夜間維護。\n" +
            "-- 按 F5 時 SSMS 會要求連線：請連到 LIBSQL02 再執行。\n\n",
            SqlSearchActivation.UnconnectedHeader(JobHit(step: null), "\n"));
    }

    [Fact]
    public void 不是伺服器上的東西沒有檔頭()
    {
        Assert.Equal("", SqlSearchActivation.UnconnectedHeader(Hit("片段"), "\n"));
        Assert.Equal("", SqlSearchActivation.UnconnectedHeader(null, "\n"));
    }

    /// <summary>
    /// 列上的答案跟著查詢視窗重算，而右鍵選單與停駐那一顆讀的是同一組屬性。
    /// </summary>
    [Fact]
    public void 結果列跟著查詢視窗重算並通知名稱與提示()
    {
        var row = new SqlSearchRow(CatalogHit(), "Table", "LIBSQL01");

        Assert.True(row.OpensUnconnected);
        Assert.True(row.CanActivate);
        Assert.Equal("移至定義（未連線）", row.ActivateLabel);
        Assert.Equal(row.ActivateDescription, row.ActivateToolTip);
        Assert.Contains("LIBSQL02", row.ActivateToolTip);

        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.ObserveActiveEditor("LIBSQL02");

        Assert.False(row.OpensUnconnected);
        Assert.Equal("移至定義", row.ActivateLabel);
        Assert.Equal("移至定義", row.ActivateToolTip);
        Assert.DoesNotContain("未連線", row.ActivateDescription);
        Assert.Contains(nameof(SqlSearchRow.ActivateLabel), changed);
        Assert.Contains(nameof(SqlSearchRow.ActivateToolTip), changed);
        Assert.Contains(nameof(SqlSearchRow.ActivateDescription), changed);

        // 答案沒變就不通知：換分頁卻還是同一台時，整份清單不必重畫提示。
        changed.Clear();
        row.ObserveActiveEditor("libsql02");
        Assert.Empty(changed);
    }

    private static SearchHit CatalogHit() => Hit(new SqlCatalogSearchTarget(
        Origin, "Library", "dbo", "Cat_BookCopy", SqlObjectKind.Table, 42));

    private static SearchHit JobHit(int? step, string database = "") => Hit(new SqlAgentJobSearchTarget(
        Origin, "LIBSQL01", Guid.Empty, "Lib_Loan 夜間維護", isEnabled: true,
        stepId: step, stepName: step is null ? null : "續借", subsystem: step is null ? "" : "TSQL",
        databaseName: database));

    private static SearchHit Hit(object? payload) =>
        new("catalog", "catalog.table", SearchMatchTarget.Name, "Cat_BookCopy", "Cat_BookCopy", 10,
            activatePayload: payload);
}
