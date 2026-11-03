using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using SqlAssist.Metadata.Dictionary;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Dictionary;

public sealed class SqlDataDictionaryLoaderTests
{
    /// <summary>
    /// 兩段結果收成物件與資料行：型別照第二層同一個格式化、檢視認得出（<c>char(2)</c> 帶尾端空白）、
    /// 說明的前後空白不算、沒有資料行的物件照樣留著。
    /// </summary>
    [Fact]
    public void 兩段結果收成物件與資料行()
    {
        using var set = new DataSet();
        var objects = set.Tables.Add("objects");
        objects.Columns.Add("object_id", typeof(int));
        objects.Columns.Add("schema_name", typeof(string));
        objects.Columns.Add("object_name", typeof(string));
        objects.Columns.Add("type", typeof(string));
        objects.Columns.Add("description", typeof(string));
        objects.Rows.Add(10, "dbo", "Loan", "U ", " 借閱 ");
        objects.Rows.Add(20, "rpt", "LoanByReader", "V ", DBNull.Value);
        objects.Rows.Add(30, "dbo", "Branch", "U ", DBNull.Value);

        var columns = set.Tables.Add("columns");
        columns.Columns.Add("object_id", typeof(int));
        columns.Columns.Add("column_id", typeof(int));
        columns.Columns.Add("column_name", typeof(string));
        columns.Columns.Add("type_name", typeof(string));
        columns.Columns.Add("max_length", typeof(short));
        columns.Columns.Add("precision", typeof(byte));
        columns.Columns.Add("scale", typeof(byte));
        columns.Columns.Add("is_nullable", typeof(bool));
        columns.Columns.Add("is_identity", typeof(bool));
        columns.Columns.Add("is_computed", typeof(bool));
        columns.Columns.Add("is_primary_key", typeof(bool));
        columns.Columns.Add("description", typeof(string));
        columns.Columns.Add("vector_dimensions", typeof(int));
        columns.Columns.Add("vector_base_type_desc", typeof(string));
        columns.Rows.Add(10, 1, "LoanNo", "int", (short)4, (byte)10, (byte)0, false, true, false, true, DBNull.Value, DBNull.Value, DBNull.Value);
        columns.Rows.Add(10, 2, "ReaderNo", "nvarchar", (short)20, (byte)0, (byte)0, true, false, false, false, "讀者編號", DBNull.Value, DBNull.Value);
        columns.Rows.Add(20, 1, "Embedding", "vector", (short)0, (byte)0, (byte)0, true, false, false, false, DBNull.Value, 3, "float32");

        using var reader = set.CreateDataReader();
        var dictionary = SqlDataDictionaryLoader.Read(reader, "Library", CancellationToken.None);

        Assert.Equal("Library", dictionary.DatabaseName);
        Assert.Equal(3, dictionary.Objects.Count);
        Assert.Equal(3, dictionary.ColumnCount);

        var loan = dictionary.Objects[0];
        Assert.Equal(SqlDictionaryObjectKind.Table, loan.Kind);
        Assert.Equal("借閱", loan.Description);
        Assert.Equal(new[] { "int", "nvarchar(10)" }, new[] { loan.Columns[0].DataType, loan.Columns[1].DataType });
        Assert.True(loan.Columns[0].IsPrimaryKey);
        Assert.True(loan.Columns[0].IsIdentity);
        Assert.True(loan.Columns[1].IsNullable);
        Assert.Equal("讀者編號", loan.Columns[1].Description);
        Assert.Equal("", loan.Columns[0].Description);

        Assert.Equal(SqlDictionaryObjectKind.View, dictionary.Objects[1].Kind);
        Assert.Equal("vector(3)", dictionary.Objects[1].Columns[0].DataType);
        Assert.Empty(dictionary.Objects[2].Columns);
    }

    /// <summary>連不上時回 null，不是空字典：空字典是「這個資料庫沒有任何資料表」。</summary>
    [Fact]
    public void 連不上時回null()
    {
        Assert.Null(SqlDataDictionaryLoader.TryLoad(new UnreachableSource(), CancellationToken.None));
    }

    private sealed class UnreachableSource : ISqlConnectionSource
    {
        public string CacheKey => "LIBSQL01|Library";

        public string ServerCacheKey => "LIBSQL01";

        public string DatabaseName => "Library";

        public IDbConnection OpenConnection() => throw new UnreachableException();
    }

    private sealed class UnreachableException : DbException
    {
        public UnreachableException()
            : base("連不上伺服器。")
        {
        }
    }
}
