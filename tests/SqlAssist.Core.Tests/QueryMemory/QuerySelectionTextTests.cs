using System;
using SqlAssist.Core.QueryMemory;
using Xunit;

namespace SqlAssist.Core.Tests.QueryMemory;

public sealed class QuerySelectionTextTests
{
    /// <summary>方塊選取只執行每一行被框住的部分；欄外文字不得混進歷程。</summary>
    [Fact]
    public void BoxSelectionLinesAreJoinedInsteadOfSpanningTheUnselectedColumns()
    {
        var selection = QuerySelectionText.Combine(new IQueryTextSnapshot[]
        {
            new QueryTextSnapshot("SELECT CopyNo"), new QueryTextSnapshot(""), new QueryTextSnapshot("FROM Loan"),
        }, "\r\n")!;

        Assert.Equal("SELECT CopyNo\r\n\r\nFROM Loan", selection.GetText());
        Assert.Equal(selection.GetText().Length, selection.Length);
    }

    [Fact]
    public void ASingleSpanIsUsedAsIsAndNoSpansMeansNoSelection()
    {
        var only = new QueryTextSnapshot("SELECT * FROM Lib_Reader;");
        Assert.Same(only, QuerySelectionText.Combine(new IQueryTextSnapshot[] { only }, "\n"));
        Assert.Null(QuerySelectionText.Combine(Array.Empty<IQueryTextSnapshot>(), "\n"));
        Assert.Throws<ArgumentException>(() => QuerySelectionText.Combine(new IQueryTextSnapshot[] { null! }, "\n"));
    }
}
