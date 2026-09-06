using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Querying;
using Xunit;

namespace SqlAssist.Metadata.Tests.Querying;

public sealed class SqlMetadataReaderTests
{
    /// <remarks>
    /// <c>object_id</c> 只在自己那個資料庫裡唯一。物件不記得自己從哪個資料庫來的話，
    /// 下游拿它回頭要欄位時會問到目前連線裡剛好同號的那一個——欄位清單看起來
    /// 完全正常，卻屬於另一張表。
    /// </remarks>
    [Fact]
    public void 讀取物件時記下所屬資料庫()
    {
        var record = new FakeDataRecord(42, "dbo", "Loan", "U");

        var scoped = SqlMetadataReader.ReadObject(record, "LibArchive");
        var local = SqlMetadataReader.ReadObject(record);

        Assert.Equal("LibArchive", scoped.DatabaseName);
        Assert.Null(local.DatabaseName);
        Assert.Equal(42, scoped.ObjectId);
        Assert.Equal(SqlObjectKind.Table, scoped.Kind);
    }

    /// <remarks>
    /// 四個界限值在伺服器端就 CONVERT 成字串：用 GetValue 收 sql_variant 拿到的是
    /// 裝箱的原生型別，一個 decimal(38,0) 的序列會讓任何一種整數轉型當場溢位，
    /// 而那會讓整份中繼資料被降級成「這一輪沒有資料」。
    /// </remarks>
    [Fact]
    public void 讀取序列()
    {
        // type_name, precision, scale, start, increment, min, max, cycling, cached, cache_size
        var record = new FakeDataRecord(
            "decimal", (byte)18, (byte)0, "1", "1", "1", "999999999999999999",
            false, true, 50);

        var sequence = SqlMetadataReader.ReadSequence(record);

        Assert.Equal("decimal(18,0)", sequence.DataType);
        Assert.Equal("1", sequence.StartValue);
        Assert.Equal("999999999999999999", sequence.MaximumValue);
        Assert.False(sequence.IsCycling);
        Assert.True(sequence.IsCached);
        Assert.Equal(50, sequence.CacheSize);
    }

    /// <remarks>
    /// <c>cache_size</c> 為 NULL 是「開著快取但大小交給引擎決定」，
    /// 那要寫成不帶數字的 <c>CACHE</c>，寫成 <c>CACHE 0</c> 會被拒絕。
    /// </remarks>
    [Fact]
    public void 讀取沒有指定快取大小的序列()
    {
        var record = new FakeDataRecord(
            "int", (byte)10, (byte)0, "1", "1", "1", "2147483647", true, true, null);

        var sequence = SqlMetadataReader.ReadSequence(record);

        Assert.Equal("int", sequence.DataType);
        Assert.True(sequence.IsCycling);
        Assert.Null(sequence.CacheSize);
    }

    [Fact]
    public void 讀取物件()
    {
        var record = new FakeDataRecord(1234, "dbo", "Lib_Reader", "U");

        var info = SqlMetadataReader.ReadObject(record);

        Assert.Equal(1234, info.ObjectId);
        Assert.Equal("dbo", info.SchemaName);
        Assert.Equal("Lib_Reader", info.Name);
        Assert.Equal(SqlObjectKind.Table, info.Kind);
        Assert.Equal("[dbo].[Lib_Reader]", info.QualifiedName);
    }

    [Fact]
    public void 讀取欄位()
    {
        // column_id, name, type, max_length, precision, scale, nullable, identity,
        // computed, primary_key, default, computed_definition, generated_always
        var record = new FakeDataRecord(
            1, "UserId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.Equal(1, column.Ordinal);
        Assert.Equal("UserId", column.Name);
        Assert.Equal("int", column.DataType);
        Assert.False(column.IsNullable);
        Assert.True(column.IsIdentity);
        Assert.False(column.IsComputed);
        Assert.True(column.IsPrimaryKey);
        Assert.Null(column.DefaultDefinition);
    }

    [Fact]
    public void 讀取欄位時套用型別格式化與預設值()
    {
        var record = new FakeDataRecord(
            2, "UserName", "nvarchar", (short)100, (byte)0, (byte)0,
            true, false, false, false, "('')", null, false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.Equal("nvarchar(50)", column.DataType);
        Assert.True(column.IsNullable);
        Assert.Equal("('')", column.DefaultDefinition);
    }

    [Fact]
    public void 欄位可組出接近CREATE_TABLE的描述()
    {
        var record = new FakeDataRecord(
            1, "UserId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        var line = SqlMetadataReader.ReadColumn(record).ToScriptLine();

        Assert.Equal("[UserId] int IDENTITY NOT NULL -- PK", line);
    }

    /// <summary>
    /// 資料行的說明跟著資料行一起回來。
    /// </summary>
    /// <remarks>
    /// 說明掛在 <c>sys.columns</c> 那條查詢的最後一欄（<c>LEFT JOIN</c> 多一欄，
    /// 不是多一輪來回）。順序對不上的症狀不是編譯錯誤，而是提示上出現另一個欄位的
    /// 說明——那比沒有說明糟。
    /// </remarks>
    [Fact]
    public void 讀取欄位說明()
    {
        var record = new FakeDataRecord(
            1, "Id", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false,
            null, null, null, null, false, false, false, false,
            "讀者編號");

        Assert.Equal("讀者編號", SqlMetadataReader.ReadColumn(record).Description);
    }

    /// <remarks>
    /// 指令碼宣告的資料表與這裡的假資料列都只組得出前面那幾欄。多讀一欄拿到的是
    /// <c>IndexOutOfRangeException</c>——那不是 <c>DbException</c>，不會被降級成
    /// 「這一輪沒有資料」，而會一路冒到平台邊界去。
    /// </remarks>
    [Fact]
    public void 沒有說明那一欄時不當機()
    {
        var record = new FakeDataRecord(
            1, "Id", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        Assert.Null(SqlMetadataReader.ReadColumn(record).Description);
    }

    [Fact]
    public void 讀取計算欄位的運算式()
    {
        var record = new FakeDataRecord(
            3, "FullName", "nvarchar", (short)200, (byte)0, (byte)0,
            true, false, true, false, null, "([First]+' '+[Last])", false);

        var column = SqlMetadataReader.ReadColumn(record);

        Assert.True(column.IsComputed);
        Assert.Equal("([First]+' '+[Last])", column.ComputedDefinition);
    }

    /// <summary>
    /// 插不進去的四種欄位。
    /// </summary>
    /// <remarks>
    /// 展開 INSERT 骨架時漏掉任何一種，症狀不是少幾個欄位，而是整句一執行就錯——
    /// 因此四種各測一次，而不是只測 IDENTITY 與計算欄位這兩個明顯的。
    /// </remarks>
    [Theory]
    [InlineData("CopyNo", "varchar", false, false, false, true)]
    [InlineData("CopyId", "int", true, false, false, false)]
    [InlineData("Barcode", "nvarchar", false, true, false, false)]
    [InlineData("RowVer", "timestamp", false, false, false, false)]
    [InlineData("ValidFrom", "datetime2", false, false, true, false)]
    public void 判斷欄位插不插得進去(
        string name,
        string typeName,
        bool isIdentity,
        bool isComputed,
        bool isGeneratedAlways,
        bool expected)
    {
        var record = new FakeDataRecord(
            1, name, typeName, (short)8, (byte)10, (byte)0,
            false, isIdentity, isComputed, false, null, null, isGeneratedAlways);

        Assert.Equal(expected, SqlMetadataReader.ReadColumn(record).CanInsert);
    }

    [Fact]
    public void 讀取索引列()
    {
        // index_id, name, is_primary_key, is_unique, is_unique_constraint,
        // type_desc, filter_definition, column_name, is_descending, is_included
        var record = new FakeDataRecord(
            2, "IX_Lib_Reader_Name", false, true, false,
            "NONCLUSTERED", "([IsDeleted]=(0))", "UserName", true, false);

        var row = SqlMetadataReader.ReadIndexRow(record);

        Assert.Equal(2, row.IndexId);
        Assert.Equal("IX_Lib_Reader_Name", row.Name);
        Assert.True(row.IsUnique);
        Assert.False(row.IsPrimaryKey);
        Assert.Equal("NONCLUSTERED", row.TypeDescription);
        Assert.Equal("([IsDeleted]=(0))", row.FilterDefinition);
        Assert.Equal("UserName", row.ColumnName);
        Assert.True(row.IsDescending);
        Assert.False(row.IsIncluded);
    }

    [Fact]
    public void 索引的篩選條件為NULL時不擲例外()
    {
        var record = new FakeDataRecord(
            1, "PK_Lib_Reader", true, true, false,
            "CLUSTERED", null, "Id", false, false);

        Assert.Null(SqlMetadataReader.ReadIndexRow(record).FilterDefinition);
    }

    [Fact]
    public void 讀取外來鍵列()
    {
        var record = new FakeDataRecord(
            "FK_Loan_Reader", "dbo", "Lib_Reader", "UserId", "Id", "CASCADE", "NO_ACTION");

        var row = SqlMetadataReader.ReadForeignKeyRow(record);

        Assert.Equal("FK_Loan_Reader", row.Name);
        Assert.Equal("dbo", row.ReferencedSchemaName);
        Assert.Equal("Lib_Reader", row.ReferencedObjectName);
        Assert.Equal("UserId", row.ColumnName);
        Assert.Equal("Id", row.ReferencedColumnName);
        Assert.Equal("CASCADE", row.DeleteAction);
    }

    [Fact]
    public void 讀取參數()
    {
        // parameter_id, name, type, max_length, precision, scale, is_output
        var record = new FakeDataRecord(
            1, "@UserId", "int", (short)4, (byte)10, (byte)0, false);

        var parameter = SqlMetadataReader.ReadParameter(record);

        Assert.Equal(1, parameter.Ordinal);
        Assert.Equal("@UserId", parameter.Name);
        Assert.Equal("int", parameter.DataType);
        Assert.False(parameter.IsOutput);
        Assert.Equal("@UserId int", parameter.ToScriptLine());
    }

    [Fact]
    public void 輸出參數標示OUTPUT()
    {
        var record = new FakeDataRecord(
            2, "@Total", "decimal", (short)9, (byte)18, (byte)2, true);

        Assert.Equal("@Total decimal(18,2) OUTPUT", SqlMetadataReader.ReadParameter(record).ToScriptLine());
    }

    [Fact]
    public void 純量函式的傳回值名稱為空字串時給予替代名稱()
    {
        var record = new FakeDataRecord(
            0, "", "int", (short)4, (byte)10, (byte)0, false);

        Assert.Equal("(傳回值)", SqlMetadataReader.ReadParameter(record).Name);
    }

    [Fact]
    public void 讀取索引選項與檔案群組()
    {
        // 前 10 欄與舊查詢相同，之後是選項、統計資料、壓縮與資料空間。
        var record = new FakeDataRecord(
            2, "IX_Loan_1", false, false, false, "NONCLUSTERED", null, "Status", false, false,
            (byte)80, true, false, true, false, false, true, "PAGE", "FG_Loan", "FG", null);

        var row = SqlMetadataReader.ReadIndexRow(record);

        Assert.Equal((byte)80, row.Options.FillFactor);
        Assert.True(row.Options.IsPadded);
        Assert.False(row.Options.AllowPageLocks);
        Assert.True(row.Options.NoRecompute);
        Assert.Equal("PAGE", row.Options.DataCompression);
        Assert.Equal("FG_Loan", row.DataSpace?.Name);
        Assert.False(row.DataSpace?.IsPartitionScheme);
    }

    /// <remarks>
    /// ALLOW_ROW_LOCKS 與 ALLOW_PAGE_LOCKS 的預設是 ON。讀不到時給錯的話，
    /// 每一個索引都會多出一個 = OFF，而那會靜靜地改掉那張表的鎖定行為。
    /// </remarks>
    [Fact]
    public void 只給得出舊欄位的索引列拿到預設選項()
    {
        var record = new FakeDataRecord(
            2, "IX_Loan_1", false, false, false, "NONCLUSTERED", null, "Status", false, false);

        var row = SqlMetadataReader.ReadIndexRow(record);

        Assert.True(row.Options.AllowRowLocks);
        Assert.True(row.Options.AllowPageLocks);
        Assert.False(row.Options.IsPadded);
        Assert.Empty(row.Options.DescribeNonDefaults());
        Assert.Null(row.DataSpace);
    }

    [Fact]
    public void 讀取資料表的檔案群組與LOB檔案群組()
    {
        var record = new FakeDataRecord("PRIMARY", "FG", "PRIMARY", true, null, true);

        var storage = SqlMetadataReader.ReadTableStorage(record);

        Assert.Equal("PRIMARY", storage.DataSpace?.Name);
        Assert.Equal("PRIMARY", storage.LobFilegroupName);
        Assert.True(storage.UsesAnsiNulls);
        Assert.True(storage.UsesQuotedIdentifier);
    }

    [Fact]
    public void 讀取分割配置時一併帶回分割資料行()
    {
        var record = new FakeDataRecord("ps_Loan", "PS", null, true, "LoanTime", true);

        var storage = SqlMetadataReader.ReadTableStorage(record);

        Assert.True(storage.DataSpace?.IsPartitionScheme);
        Assert.Equal("LoanTime", storage.DataSpace?.PartitionColumnName);
        Assert.Null(storage.LobFilegroupName);
    }

    /// <remarks>
    /// 猜一個 [PRIMARY] 出來是指令碼在說謊：那張表可能建在別的檔案群組上。
    /// </remarks>
    [Fact]
    public void 查不到檔案群組時整個為null()
    {
        var record = new FakeDataRecord(null, null, null, false, null, false);

        var storage = SqlMetadataReader.ReadTableStorage(record);

        Assert.Null(storage.DataSpace);
        Assert.Null(storage.LobFilegroupName);
        Assert.False(storage.UsesAnsiNulls);
    }

    [Theory]
    [InlineData(0, null, SqlExtendedPropertyLevel.Table)]
    [InlineData(1, "LoanUser", SqlExtendedPropertyLevel.Column)]
    [InlineData(2, "IX_Loan_1", SqlExtendedPropertyLevel.Index)]
    [InlineData(3, "DF_Loan_IsActive", SqlExtendedPropertyLevel.Constraint)]
    public void 擴充屬性的層級依查詢自己編的號對應(
        int level, string? target, SqlExtendedPropertyLevel expected)
    {
        var record = new FakeDataRecord(level, "MS_Description", "借閱主表", 0, target);

        var property = SqlMetadataReader.ReadExtendedProperty(record);

        Assert.Equal(expected, property.Level);
        Assert.Equal("MS_Description", property.Name);
        Assert.Equal(target, property.TargetName);
    }

    /// <remarks>
    /// 沒見過的類別號一律當成資料表層級：多寫一筆掛在資料表上的說明，
    /// 比整份指令碼因為一個新類別而失敗好。
    /// </remarks>
    [Fact]
    public void 認不得的擴充屬性層級退回資料表()
    {
        var record = new FakeDataRecord(99, "MS_Description", "說明", 0, null);

        Assert.Equal(
            SqlExtendedPropertyLevel.Table,
            SqlMetadataReader.ReadExtendedProperty(record).Level);
    }

    [Fact]
    public void 沒有值的擴充屬性讀成空字串而不是null()
    {
        var record = new FakeDataRecord(0, "MS_Description", null, 0, null);

        Assert.Equal(string.Empty, SqlMetadataReader.ReadExtendedProperty(record).Value);
    }

    [Fact]
    public void 讀取識別資料行的種子與遞增量以及預設值條件約束的名稱()
    {
        // 前 13 欄與舊查詢相同，之後是定序、識別值、預設值名稱與三個旗標。
        var record = new FakeDataRecord(
            1, "LoanId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false,
            null, "1", "1", "DF_Loan_LoanId", false, false, false, false);

        var script = SqlMetadataReader.ReadColumn(record).Script;

        Assert.Equal("1", script.IdentitySeed);
        Assert.Equal("1", script.IdentityIncrement);
        Assert.Equal("DF_Loan_LoanId", script.DefaultConstraintName);
        Assert.False(script.DefaultIsSystemNamed);
    }

    [Fact]
    public void 讀取資料行定序與稀疏及唯一識別旗標()
    {
        var record = new FakeDataRecord(
            4, "BorrowerName", "nvarchar", (short)100, (byte)0, (byte)0,
            true, false, false, false, null, null, false,
            "Chinese_Taiwan_Stroke_CI_AS", null, null, null, false, false, true, true);

        var script = SqlMetadataReader.ReadColumn(record).Script;

        Assert.Equal("Chinese_Taiwan_Stroke_CI_AS", script.CollationName);
        Assert.True(script.IsSparse);
        Assert.True(script.IsRowGuidCol);
        Assert.Equal("nvarchar", script.TypeName);
        Assert.Equal((short)100, script.MaxLength);
    }

    /// <remarks>
    /// 指令碼宣告的資料表與舊的假資料列只組得出前 13 欄。多讀一欄拿到的是
    /// IndexOutOfRangeException，那不是 DbException，不會被降級成
    /// 「這一輪沒有資料」，而會一路冒到平台邊界去。
    /// </remarks>
    [Fact]
    public void 只給得出舊欄位的資料列讀得到空的指令碼細節()
    {
        var record = new FakeDataRecord(
            1, "LoanId", "int", (short)4, (byte)10, (byte)0,
            false, true, false, true, null, null, false);

        var script = SqlMetadataReader.ReadColumn(record).Script;

        Assert.Null(script.CollationName);
        Assert.Null(script.IdentitySeed);
        Assert.False(script.IsSparse);
        Assert.Equal("int", script.TypeName);
    }

    [Fact]
    public void 系統配的預設值名稱看得出來是系統配的()
    {
        var record = new FakeDataRecord(
            5, "IsReturned", "bit", (short)1, (byte)1, (byte)0,
            false, false, false, false, "((0))", null, false,
            null, null, null, "DF__Loan__IsRet__2A4B", true, false, false, false);

        var script = SqlMetadataReader.ReadColumn(record).Script;

        Assert.True(script.DefaultIsSystemNamed);
        Assert.Equal("DF__Loan__IsRet__2A4B", script.DefaultConstraintName);
    }
}
