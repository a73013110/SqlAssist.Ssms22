using System;
using SqlAssist.Core.SqlMemory;
using Xunit;

namespace SqlAssist.Core.Tests.SqlMemory;

public sealed class SqlSelectionTextTests
{
    /// <summary>方塊選取只執行每一行被框住的部分；欄外文字不得混進歷程。</summary>
    [Fact]
    public void BoxSelectionLinesAreJoinedInsteadOfSpanningTheUnselectedColumns()
    {
        var selection = SqlSelectionText.Combine(new ISqlTextSnapshot[]
        {
            new SqlTextSnapshot("SELECT CopyNo"), new SqlTextSnapshot(""), new SqlTextSnapshot("FROM Loan"),
        }, "\r\n")!;

        Assert.Equal("SELECT CopyNo\r\n\r\nFROM Loan", selection.GetText());
        Assert.Equal(selection.GetText().Length, selection.Length);
    }

    [Fact]
    public void ASingleSpanIsUsedAsIsAndNoSpansMeansNoSelection()
    {
        var only = new SqlTextSnapshot("SELECT * FROM Lib_Reader;");
        Assert.Same(only, SqlSelectionText.Combine(new ISqlTextSnapshot[] { only }, "\n"));
        Assert.Null(SqlSelectionText.Combine(Array.Empty<ISqlTextSnapshot>(), "\n"));
        Assert.Throws<ArgumentException>(() => SqlSelectionText.Combine(new ISqlTextSnapshot[] { null! }, "\n"));
    }
}
