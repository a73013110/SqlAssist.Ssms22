using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Model;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把物件結構重建成 T-SQL。
/// </summary>
/// <remarks>
/// 全擴充只有這一份組 <c>CREATE TABLE</c> 的地方。浮動預覽的指令碼分頁與 F12
/// 兩條路徑都走這裡，差別只在傳進來的 <see cref="SqlScriptOptions"/>。
/// 各自組一份的症狀已經發生過：同一張資料表在預覽裡與按下 F12 之後長得不一樣，
/// 而使用者會以為其中一條壞了。
///
/// 每一個選項都收斂成「要不要多寫這幾個字」，不依風格分支：分成三支 <c>if</c>
/// 的話，新增一個選項要改三個地方，而漏掉的那一支不會有任何徵兆。
/// </remarks>
public sealed class TSqlScriptRenderer : ISqlScriptRenderer
{
    /// <summary>共用實例；這個型別沒有狀態。</summary>
    public static readonly TSqlScriptRenderer Default = new();

    public string Format => "sql";

    public string Render(IReadOnlyList<SqlObjectStructure> objects, SqlScriptContext context)
    {
        if (objects is null)
        {
            throw new ArgumentNullException(nameof(objects));
        }

        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var statements = new List<Statement>();

        foreach (var structure in objects)
        {
            AppendObject(statements, structure, context);
        }

        return Join(statements, context);
    }

    /// <summary>一個敘述，以及它算不算一個要用 <c>GO</c> 隔開的批次。</summary>
    /// <remarks>
    /// 資料不齊時輸出的那一整段註解<b>不</b>算：後面接一個 <c>GO</c> 雖然合法，
    /// 卻讓「這份輸出從頭到尾都是註解」不再成立，而那正是那一段唯一的保證。
    /// </remarks>
    private readonly struct Statement
    {
        public Statement(string text, bool batched)
        {
            Text = text;
            Batched = batched;
        }

        public string Text { get; }

        public bool Batched { get; }
    }

    /// <summary>單一物件；多物件是它的清單版本。</summary>
    public string Render(SqlObjectStructure structure, SqlScriptContext context) =>
        Render(new[] { structure ?? throw new ArgumentNullException(nameof(structure)) }, context);

    private void AppendObject(List<Statement> statements, SqlObjectStructure structure, SqlScriptContext context)
    {
        // 資料不齊時整段換成註解，不輸出半份可以執行的東西：少了資料行的
        // CREATE TABLE 只剩一對空括號，卻仍然貼得上去。判斷與說明只有模型那一份。
        if (structure.TryBuildUnavailableScript(out var unavailable))
        {
            statements.Add(new Statement(unavailable, batched: false));
            return;
        }

        // SET 選項在這裡加，不在下面三支各加一次：漏掉其中一支的症狀是同一份
        // 指令碼裡有的物件前面有那兩行、有的沒有，而那不是任何一個選項說的。
        AppendSetOptions(statements, context.Options);

        if (structure.Object.Kind.ScriptsFromDefinition())
        {
            AppendModule(statements, structure, context);
            return;
        }

        if (structure.Object.Kind == SqlObjectKind.TableType)
        {
            statements.Add(new Statement(BuildCreateType(structure, context), batched: true));
            return;
        }

        AppendTable(statements, structure, context);
    }

    private void AppendTable(List<Statement> statements, SqlObjectStructure structure, SqlScriptContext context)
    {
        var options = context.Options;
        var name = QualifiedName(structure.Object, options);

        statements.Add(new Statement(BuildCreateTable(structure, context, name), batched: true));

        if (options.PrimaryKeyPlacement == SqlConstraintPlacement.SeparateStatement &&
            structure.PrimaryKey is { } primaryKey)
        {
            statements.Add(new Statement(BuildConstraint(primaryKey, name, context), batched: true));
        }

        if (options.IncludeIndexes)
        {
            foreach (var index in structure.Indexes)
            {
                if (index.IsPrimaryKey)
                {
                    continue;
                }

                if (index.IsUniqueConstraint)
                {
                    if (options.UniqueConstraintPlacement == SqlConstraintPlacement.SeparateStatement)
                    {
                        statements.Add(new Statement(BuildConstraint(index, name, context), batched: true));
                    }

                    continue;
                }

                statements.Add(new Statement(BuildCreateIndex(index, name, context), batched: true));
            }
        }

        if (options.IncludeForeignKeys)
        {
            foreach (var foreignKey in structure.ForeignKeys)
            {
                statements.Add(new Statement(BuildForeignKey(foreignKey, name, context), batched: true));
            }
        }
    }

    /// <summary>模組、同義字與序列：定義原文就是它的指令碼。</summary>
    /// <remarks>
    /// 只有模組改寫得成 <c>ALTER</c>。同義字與序列沒有 <c>ALTER</c> 的整體寫法，
    /// <c>ALTER SEQUENCE</c> 則改不了型別，所以它們一律維持 <c>CREATE</c>——
    /// 改寫的判斷交給 <see cref="SqlModuleScript.TryConvertCreateToAlter"/>，
    /// 它認不出開頭的關鍵字時回報失敗，於是原樣保留。
    ///
    /// 改寫一定要在<b>定義本身</b>上做，不能等整份組完再改：SET 選項開著時
    /// 那一份的開頭是 <c>SET ANSI_NULLS ON</c>，改寫會一律失敗而沒有任何徵兆。
    /// </remarks>
    private static void AppendModule(
        List<Statement> statements,
        SqlObjectStructure structure,
        SqlScriptContext context)
    {
        var definition = structure.Definition!;

        statements.Add(new Statement(
            context.Options.ModuleStatement == SqlModuleStatement.Alter &&
            structure.Object.Kind.IsModule() &&
            SqlModuleScript.TryConvertCreateToAlter(definition, out var altered)
                ? altered
                : definition,
            batched: true));
    }

    /// <remarks>
    /// 這兩行不是裝飾：計算資料行、篩選索引與索引檢視對這兩個選項的值有要求，
    /// 少了它們的 <c>CREATE TABLE</c> 在某些連線設定下會直接失敗。
    /// </remarks>
    private static void AppendSetOptions(List<Statement> statements, SqlScriptOptions options)
    {
        if (options.SetOptions == SqlSetOptionOutput.None)
        {
            return;
        }

        statements.Add(new Statement("SET ANSI_NULLS ON", batched: true));
        statements.Add(new Statement("SET QUOTED_IDENTIFIER ON", batched: true));
    }

    private string BuildCreateTable(SqlObjectStructure structure, SqlScriptContext context, string name)
    {
        var options = context.Options;
        var builder = new StringBuilder(EstimateCapacity(structure));
        builder.Append("CREATE TABLE ").Append(name);
        AppendOpenBrace(builder, context);
        AppendColumns(builder, structure, context, BuildInlineConstraints(structure, context));
        builder.Append(')');
        return Terminate(builder, options).ToString();
    }

    /// <remarks>
    /// 資料表型別的主索引鍵寫成<b>不具名</b>的內嵌條件約束：<c>CREATE TYPE</c> 的
    /// 括號裡不收 <c>CONSTRAINT 名稱</c>，查到的那個名字本來就是引擎自己配的。
    /// 其餘索引整組寫不進去，數量寫在結尾的註解裡——否則這份文字看起來就像
    /// 那個型別只有主索引鍵。
    /// </remarks>
    private string BuildCreateType(SqlObjectStructure structure, SqlScriptContext context)
    {
        var options = context.Options;
        var builder = new StringBuilder(EstimateCapacity(structure));
        builder.Append("CREATE TYPE ").Append(QualifiedName(structure.Object, options)).Append(" AS TABLE");
        AppendOpenBrace(builder, context);

        var constraints = new List<string>();

        if (structure.PrimaryKey is { } primaryKey)
        {
            constraints.Add(options.Indent + "PRIMARY KEY " + primaryKey.TypeDescription +
                            " (" + KeyColumns(primaryKey, options) + ")");
        }

        AppendColumns(builder, structure, context, constraints);
        builder.Append(')');
        Terminate(builder, options);

        var skipped = CountSecondaryIndexes(structure);

        if (skipped > 0)
        {
            builder.Append(context.NewLine).Append(context.NewLine)
                .Append("-- 另有 ").Append(skipped)
                .Append(" 個索引沒有寫進來：CREATE INDEX 與 ALTER TABLE 對型別都不合法，")
                .Append(context.NewLine)
                .Append("-- 而 CREATE TYPE 的括號裡只放得下不具名的條件約束。");
        }

        return builder.ToString();
    }

    private static void AppendOpenBrace(StringBuilder builder, SqlScriptContext context)
    {
        if (context.Options.BracePlacement == SqlBracePlacement.NewLine)
        {
            builder.Append(context.NewLine);
        }

        builder.Append('(').Append(context.NewLine);
    }

    /// <summary>要寫在 <c>CREATE TABLE</c> 括號裡的條件約束。</summary>
    private static List<string> BuildInlineConstraints(SqlObjectStructure structure, SqlScriptContext context)
    {
        var options = context.Options;
        var constraints = new List<string>();

        if (options.PrimaryKeyPlacement == SqlConstraintPlacement.Inline &&
            structure.PrimaryKey is { } primaryKey)
        {
            constraints.Add(options.Indent + InlineConstraintBody(primaryKey, options));
        }

        if (options.IncludeIndexes && options.UniqueConstraintPlacement == SqlConstraintPlacement.Inline)
        {
            foreach (var index in structure.Indexes)
            {
                if (index.IsUniqueConstraint)
                {
                    constraints.Add(options.Indent + InlineConstraintBody(index, options));
                }
            }
        }

        return constraints;
    }

    private static string InlineConstraintBody(SqlIndexInfo index, SqlScriptOptions options)
    {
        var builder = new StringBuilder();
        AppendConstraintName(builder, index.Name, systemNamed: false, options);
        builder.Append(index.IsPrimaryKey ? "PRIMARY KEY " : "UNIQUE ")
            .Append(index.TypeDescription)
            .Append(" (").Append(KeyColumns(index, options)).Append(')');
        return builder.ToString();
    }

    /// <remarks>
    /// 最後一個資料行後面要不要逗號，看的是它後面還有沒有條件約束那幾行。
    /// </remarks>
    private static void AppendColumns(
        StringBuilder builder,
        SqlObjectStructure structure,
        SqlScriptContext context,
        IReadOnlyList<string> trailing)
    {
        var columns = structure.Columns;

        for (var index = 0; index < columns.Count; index++)
        {
            builder.Append(context.Options.Indent);
            AppendColumn(builder, columns[index], context);

            if (index < columns.Count - 1 || trailing.Count > 0)
            {
                builder.Append(',');
            }

            builder.Append(context.NewLine);
        }

        for (var index = 0; index < trailing.Count; index++)
        {
            builder.Append(trailing[index]);

            if (index < trailing.Count - 1)
            {
                builder.Append(',');
            }

            builder.Append(context.NewLine);
        }
    }

    /// <summary>
    /// 一個資料行的定義。
    /// </summary>
    /// <remarks>
    /// 子句順序照 SQL Server 接受的寫法排：型別、定序、SPARSE、ROWGUIDCOL，
    /// 然後是可否為 NULL 與 IDENTITY（兩者的先後由選項決定，Fidelity 寫
    /// <c>NOT NULL IDENTITY(1, 1)</c>，SSMS 寫 <c>IDENTITY(1,1) NOT NULL</c>），
    /// 最後才是預設值。
    ///
    /// 計算資料行不寫型別：寫了整段指令碼貼上去就會失敗。
    /// </remarks>
    private static void AppendColumn(StringBuilder builder, SqlColumnInfo column, SqlScriptContext context)
    {
        var options = context.Options;
        builder.Append(Identifier(column.Name, options)).Append(' ');

        if (column.IsComputed)
        {
            AppendComputedColumn(builder, column, options);
            return;
        }

        builder.Append(DataType(column, options));
        AppendCollation(builder, column, context);

        if (options.IncludeSparse && column.Script.IsSparse)
        {
            builder.Append(" SPARSE");
        }

        if (options.IncludeRowGuidCol && column.Script.IsRowGuidCol)
        {
            builder.Append(" ROWGUIDCOL");
        }

        if (options.NullabilityOrder == SqlNullabilityOrder.AfterIdentity)
        {
            AppendIdentity(builder, column, options);
            builder.Append(column.IsNullable ? " NULL" : " NOT NULL");
        }
        else
        {
            builder.Append(column.IsNullable ? " NULL" : " NOT NULL");
            AppendIdentity(builder, column, options);
        }

        AppendDefault(builder, column, options);
    }

    /// <remarks>
    /// 可否為 NULL 只有在 <c>PERSISTED</c> 後面才寫得出來：沒有存下來的計算資料行
    /// 根本不接受那個宣告，硬寫是一段跑不動的指令碼。
    /// </remarks>
    private static void AppendComputedColumn(
        StringBuilder builder,
        SqlColumnInfo column,
        SqlScriptOptions options)
    {
        builder.Append("AS ").Append(column.ComputedDefinition ?? "(/* 無法取得運算式 */)");

        if (!options.IncludePersisted || !column.Script.IsPersisted)
        {
            return;
        }

        builder.Append(" PERSISTED");

        if (!column.IsNullable)
        {
            builder.Append(" NOT NULL");
        }
    }

    private static void AppendIdentity(StringBuilder builder, SqlColumnInfo column, SqlScriptOptions options)
    {
        if (!column.IsIdentity)
        {
            return;
        }

        builder.Append(" IDENTITY");

        var seed = column.Script.IdentitySeed;
        var increment = column.Script.IdentityIncrement;

        // 種子查不到時只寫關鍵字。猜一組 (1, 1) 出來是指令碼在說謊：
        // 那張表可能是從別處匯入的，種子根本不是 1。
        if (!options.IncludeIdentitySeed || string.IsNullOrEmpty(seed) || string.IsNullOrEmpty(increment))
        {
            return;
        }

        builder.Append('(').Append(seed)
            .Append(options.SpaceAfterArgumentComma ? ", " : ",")
            .Append(increment).Append(')');
    }

    /// <remarks>
    /// 資料庫定序查不到時<b>一定要</b>寫出資料行定序。省略等於讓目的地用自己的
    /// 資料庫定序，而排序、比較與唯一索引的行為會跟著換，畫面上卻看不出差別。
    /// </remarks>
    private static void AppendCollation(StringBuilder builder, SqlColumnInfo column, SqlScriptContext context)
    {
        var collation = column.Script.CollationName;

        if (string.IsNullOrEmpty(collation) || context.Options.Collation == SqlCollationOutput.Never)
        {
            return;
        }

        if (context.Options.Collation == SqlCollationOutput.WhenDifferentFromDatabase &&
            context.DatabaseCollation is { Length: > 0 } databaseCollation &&
            string.Equals(collation, databaseCollation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        builder.Append(" COLLATE ").Append(collation);
    }

    private static void AppendDefault(StringBuilder builder, SqlColumnInfo column, SqlScriptOptions options)
    {
        if (string.IsNullOrEmpty(column.DefaultDefinition))
        {
            return;
        }

        builder.Append(' ');
        AppendConstraintName(
            builder,
            column.Script.DefaultConstraintName,
            column.Script.DefaultIsSystemNamed,
            options);
        builder.Append("DEFAULT ").Append(column.DefaultDefinition);
    }

    /// <summary>寫出 <c>CONSTRAINT [名稱] </c>（含結尾空白），或什麼都不寫。</summary>
    private static void AppendConstraintName(
        StringBuilder builder,
        string? name,
        bool systemNamed,
        SqlScriptOptions options)
    {
        if (string.IsNullOrEmpty(name) || options.ConstraintNaming == SqlConstraintNaming.Never)
        {
            return;
        }

        if (options.ConstraintNaming == SqlConstraintNaming.OnlyUserNamed && systemNamed)
        {
            return;
        }

        builder.Append("CONSTRAINT ").Append(Identifier(name!, options)).Append(' ');
    }

    private static string BuildConstraint(SqlIndexInfo index, string tableName, SqlScriptContext context)
    {
        var options = context.Options;
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ").Append(tableName).Append(" ADD ");
        AppendConstraintName(builder, index.Name, systemNamed: false, options);
        builder.Append(index.IsPrimaryKey ? "PRIMARY KEY " : "UNIQUE ")
            .Append(index.TypeDescription)
            .Append(" (").Append(KeyColumns(index, options)).Append(')');
        return Terminate(builder, options).ToString();
    }

    private static string BuildCreateIndex(SqlIndexInfo index, string tableName, SqlScriptContext context)
    {
        var options = context.Options;
        var builder = new StringBuilder();
        builder.Append("CREATE ");

        if (index.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append(index.TypeDescription).Append(" INDEX ").Append(Identifier(index.Name, options))
            .Append(" ON ").Append(tableName)
            .Append(" (").Append(KeyColumns(index, options)).Append(')');

        var included = IncludedColumns(index, options);

        if (included.Length > 0)
        {
            builder.Append(" INCLUDE (").Append(included).Append(')');
        }

        if (!string.IsNullOrWhiteSpace(index.FilterDefinition))
        {
            builder.Append(" WHERE ").Append(index.FilterDefinition);
        }

        return Terminate(builder, options).ToString();
    }

    private static string BuildForeignKey(
        SqlForeignKeyInfo foreignKey,
        string tableName,
        SqlScriptContext context)
    {
        var options = context.Options;
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ").Append(tableName).Append(' ');

        if (options.ForeignKeysWithNoCheck)
        {
            builder.Append("WITH NOCHECK ");
        }

        builder.Append("ADD ");
        AppendConstraintName(builder, foreignKey.Name, systemNamed: false, options);
        builder.Append("FOREIGN KEY (").Append(ForeignKeyColumns(foreignKey, options, referenced: false))
            .Append(") REFERENCES ").Append(ReferencedName(foreignKey, options))
            .Append(" (").Append(ForeignKeyColumns(foreignKey, options, referenced: true)).Append(')');

        if (foreignKey.HasDeleteAction)
        {
            builder.Append(" ON DELETE ").Append(foreignKey.DeleteAction.Replace('_', ' '));
        }

        if (foreignKey.HasUpdateAction)
        {
            builder.Append(" ON UPDATE ").Append(foreignKey.UpdateAction.Replace('_', ' '));
        }

        return Terminate(builder, options).ToString();
    }

    private static string KeyColumns(SqlIndexInfo index, SqlScriptOptions options)
    {
        var builder = new StringBuilder();

        foreach (var column in index.Columns)
        {
            if (column.IsIncluded)
            {
                continue;
            }

            Separate(builder);
            builder.Append(Identifier(column.Name, options));

            if (column.IsDescending)
            {
                builder.Append(" DESC");
            }
            else if (!options.OmitAscendingKeyword)
            {
                builder.Append(" ASC");
            }
        }

        return builder.ToString();
    }

    private static string IncludedColumns(SqlIndexInfo index, SqlScriptOptions options)
    {
        var builder = new StringBuilder();

        foreach (var column in index.Columns)
        {
            if (!column.IsIncluded)
            {
                continue;
            }

            Separate(builder);
            builder.Append(Identifier(column.Name, options));
        }

        return builder.ToString();
    }

    private static string ForeignKeyColumns(
        SqlForeignKeyInfo foreignKey,
        SqlScriptOptions options,
        bool referenced)
    {
        var builder = new StringBuilder();

        foreach (var column in foreignKey.Columns)
        {
            Separate(builder);
            builder.Append(Identifier(referenced ? column.ReferencedName : column.Name, options));
        }

        return builder.ToString();
    }

    private static void Separate(StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            builder.Append(", ");
        }
    }

    private static string DataType(SqlColumnInfo column, SqlScriptOptions options)
    {
        var script = column.Script;

        // 型別的原始欄位查不到時（指令碼宣告的資料表）只剩已經組好的字串，
        // 那一份改寫不了排版，原樣帶出去比猜一個好。
        return script.TypeName.Length == 0
            ? column.DataType
            : SqlTypeFormatter.Format(
                script.TypeName,
                script.MaxLength,
                script.Precision,
                script.Scale,
                options.QuoteDataTypes,
                options.SpaceBeforeTypeArguments,
                options.SpaceAfterArgumentComma);
    }

    private static string ReferencedName(SqlForeignKeyInfo foreignKey, SqlScriptOptions options) =>
        Identifier(foreignKey.ReferencedSchemaName, options) + "." +
        Identifier(foreignKey.ReferencedObjectName, options);

    private static string QualifiedName(SqlObjectInfo info, SqlScriptOptions options) =>
        options.QuoteIdentifiers
            ? info.QualifiedName
            : Identifier(info.SchemaName, options) + "." + Identifier(info.Name, options);

    /// <remarks>
    /// 關掉方括號之後仍然會替保留字與形狀不合法的名稱加括號——那不是風格，
    /// 少了它指令碼執行不了。判斷只有 <c>SqlIdentifier</c> 一份。
    /// </remarks>
    private static string Identifier(string name, SqlScriptOptions options) =>
        options.QuoteIdentifiers ? SqlIdentifier.Quote(name) : SqlIdentifier.QuoteIfNeeded(name);

    private static StringBuilder Terminate(StringBuilder builder, SqlScriptOptions options) =>
        builder.Append(options.StatementTerminator);

    /// <remarks>
    /// 批次分隔字元關掉時敘述之間隔一個空行。全部接在一起的話，
    /// <c>CREATE TABLE</c> 後面緊接著 <c>ALTER TABLE</c> 是一整片看不出段落的文字。
    ///
    /// 敘述本身已經以換行結尾時不再補一個：模組的定義原文來自資料庫，
    /// 而它結不結尾帶換行完全看當初是誰建的，補下去就多出一行空白。
    /// </remarks>
    private static string Join(IReadOnlyList<Statement> statements, SqlScriptContext context)
    {
        var separated = context.Options.BatchSeparation == SqlBatchSeparation.BetweenStatements;
        var builder = new StringBuilder();

        foreach (var statement in statements)
        {
            builder.Append(statement.Text);

            if (!statement.Text.EndsWith(context.NewLine, StringComparison.Ordinal))
            {
                builder.Append(context.NewLine);
            }

            if (separated && statement.Batched)
            {
                builder.Append(context.Options.BatchSeparator).Append(context.NewLine);
            }
            else
            {
                builder.Append(context.NewLine);
            }
        }

        return builder.ToString();
    }

    private static int CountSecondaryIndexes(SqlObjectStructure structure)
    {
        var count = 0;

        foreach (var index in structure.Indexes)
        {
            if (!index.IsPrimaryKey)
            {
                count++;
            }
        }

        return count;
    }

    /// <remarks>
    /// 預估容量而不是讓 StringBuilder 自己長：一張百來個資料行的資料表會讓它
    /// 重新配置七、八次，而每一次都要複製整份已經組好的文字。
    /// </remarks>
    private static int EstimateCapacity(SqlObjectStructure structure) =>
        64 + (structure.Columns.Count * 96);
}
