using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.ResultGrid;

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
        AppendHeaderComment(statements, context);

        foreach (var structure in objects)
        {
            AppendObject(statements, structure, context);
        }

        return Join(statements, context);
    }

    /// <summary>
    /// 檔頭註解：這份指令碼從哪裡來、什麼時候產生的、用哪一組風格。
    /// </summary>
    /// <remarks>
    /// 存檔之後看得出來源是這份文字唯一的用處。少了它，一個資料夾裡的十份
    /// <c>CREATE TABLE</c> 分不出是從測試機還是正式機抓的——而那正是有人會照著
    /// 執行一次的情形。
    ///
    /// 查不到的欄位整行不寫，不留一行「來源：（未知）」：那一行沒有帶任何資訊，
    /// 只是讓檔頭長一點。
    /// </remarks>
    private static void AppendHeaderComment(List<Statement> statements, SqlScriptContext context)
    {
        if (!context.Options.IncludeHeaderComment)
        {
            return;
        }

        var builder = new StringBuilder();
        AppendCommentLine(builder, context, "來源", JoinSource(context));
        AppendCommentLine(builder, context, "產生時間", context.GeneratedAt?.ToString("u"));
        AppendCommentLine(builder, context, "工具", context.ToolVersion);
        AppendCommentLine(builder, context, "風格", context.Options.Style.ToString());

        if (builder.Length > 0)
        {
            statements.Add(new Statement(builder.ToString().TrimEnd(), batched: false));
        }
    }

    private static string? JoinSource(SqlScriptContext context)
    {
        if (context.ServerName is not { Length: > 0 } server)
        {
            return context.DatabaseName;
        }

        return context.DatabaseName is { Length: > 0 } database ? server + "." + database : server;
    }

    private static void AppendCommentLine(
        StringBuilder builder,
        SqlScriptContext context,
        string label,
        string? value)
    {
        if (value is { Length: > 0 })
        {
            builder.Append("-- ").Append(label).Append('：').Append(value).Append(context.NewLine);
        }
    }

    /// <summary>
    /// 健檢的發現，寫成這個物件前面的一段註解。
    /// </summary>
    /// <remarks>
    /// 排在物件的第一個敘述之前而不是整份的最前面：多物件時每一張表的發現要跟著
    /// 自己那張表，否則一份三十張表的指令碼開頭會是一整頁分不出屬於誰的警告。
    ///
    /// 這一段不參與批次分隔——全是註解的東西後面接一個 <c>GO</c> 沒有意義。
    /// </remarks>
    private static void AppendAnalyzerComments(
        List<Statement> statements,
        SqlObjectStructure structure,
        SqlScriptContext context)
    {
        if (!context.Options.IncludeAnalyzerComments || context.Analyzer is not { } analyzer)
        {
            return;
        }

        var findings = analyzer.Analyze(structure);

        if (findings.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();

        foreach (var finding in findings)
        {
            builder.Append("-- ").Append(finding.Describe()).Append(context.NewLine);
        }

        statements.Add(new Statement(builder.ToString().TrimEnd(), batched: false));
    }

    /// <summary>一個敘述，以及它算不算一個要用 <c>GO</c> 隔開的批次。</summary>
    /// <remarks>
    /// 資料不齊時輸出的那一整段註解<b>不</b>算：後面接一個 <c>GO</c> 雖然合法，
    /// 卻讓「這份輸出從頭到尾都是註解」不再成立，而那正是那一段唯一的保證。
    ///
    /// <see cref="RequiresBatch"/> 是另一回事：那幾種敘述<b>必須</b>自己一個批次，
    /// 語言規定如此。批次分隔關掉時它們仍然要有 <c>GO</c>，否則那份指令碼根本
    /// 執行不了——可執行性不是風格偏好。
    /// </remarks>
    private readonly struct Statement
    {
        public Statement(string text, bool batched, bool requiresBatch = false)
        {
            Text = text;
            Batched = batched;
            RequiresBatch = requiresBatch;
        }

        public string Text { get; }

        public bool Batched { get; }

        /// <summary><c>CREATE TRIGGER</c> 這一族：語言規定它必須是批次裡的第一個敘述。</summary>
        public bool RequiresBatch { get; }
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

        AppendAnalyzerComments(statements, structure, context);

        // SET 選項在這裡加，不在下面三支各加一次：漏掉其中一支的症狀是同一份
        // 指令碼裡有的物件前面有那兩行、有的沒有，而那不是任何一個選項說的。
        AppendSetOptions(statements, context.Options, structure.Storage);

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

                if (index.Options.IsDisabled)
                {
                    statements.Add(new Statement(BuildDisableIndex(index, name, context), batched: true));
                }
            }
        }

        if (options.IncludeCheckConstraints)
        {
            foreach (var check in structure.CheckConstraints)
            {
                AppendCheckConstraint(statements, check, name, context);
            }
        }

        if (options.IncludeForeignKeys)
        {
            foreach (var foreignKey in structure.ForeignKeys)
            {
                statements.Add(new Statement(BuildForeignKey(foreignKey, name, context), batched: true));
            }
        }

        AppendTriggers(statements, structure, context);
        AppendExtendedProperties(statements, structure, context);
    }

    /// <summary>
    /// 觸發程序：定義原文，停用時再停回去。
    /// </summary>
    /// <remarks>
    /// 每一個都必須自己一個批次，語言規定 <c>CREATE TRIGGER</c> 是批次裡的第一個
    /// 敘述——所以即使批次分隔關掉，它們後面仍然會有一個 <c>GO</c>。沒有的話，
    /// 那份指令碼在 <c>CREATE TABLE</c> 之後就整段語法錯誤，而使用者看到的是
    /// 「這個工具產不出能跑的東西」。
    ///
    /// 定義取不到的（加密、沒有 <c>VIEW DEFINITION</c> 權限）換成一行註解，
    /// 不是安靜地少一個：那張表在來源上有這個觸發程序，而重建出來的沒有。
    /// </remarks>
    private static void AppendTriggers(
        List<Statement> statements,
        SqlObjectStructure structure,
        SqlScriptContext context)
    {
        if (!context.Options.IncludeTriggers)
        {
            return;
        }

        var options = context.Options;
        var tableName = QualifiedName(structure.Object, options);

        foreach (var trigger in structure.Triggers)
        {
            if (!trigger.CanScript)
            {
                statements.Add(new Statement(
                    "-- 取不到觸發程序 " + Identifier(trigger.Name, options) +
                    " 的定義：它是 WITH ENCRYPTION 建立的，或這個登入沒有它的 VIEW DEFINITION 權限。",
                    batched: false));
                continue;
            }

            statements.Add(new Statement(trigger.Definition!, batched: true, requiresBatch: true));

            if (!trigger.IsDisabled)
            {
                continue;
            }

            var disable = new StringBuilder();
            disable.Append("DISABLE TRIGGER ").Append(Identifier(trigger.Name, options))
                .Append(" ON ").Append(tableName);
            statements.Add(new Statement(Terminate(disable, options).ToString(), batched: true));
        }
    }

    /// <summary>
    /// 一個 <c>CHECK</c> 條件約束，停用時多一個敘述把它停回去。
    /// </summary>
    /// <remarks>
    /// 停用狀態非寫不可：省略的話重建出來的資料表會開始擋掉來源允許的資料，
    /// 而那是在資料匯入到一半才發現的那種差異。
    ///
    /// 停用的條件約束連建立時都要 <c>WITH NOCHECK</c>：<c>ALTER TABLE</c> 預設
    /// 會拿現有的資料驗一次，而那些資料正是當初讓它被停用的原因。
    /// </remarks>
    private static void AppendCheckConstraint(
        List<Statement> statements,
        SqlCheckConstraint check,
        string tableName,
        SqlScriptContext context)
    {
        var options = context.Options;
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ").Append(tableName).Append(' ');

        if (check.IsDisabled)
        {
            builder.Append("WITH NOCHECK ");
        }

        builder.Append("ADD ");
        AppendConstraintName(builder, check.Name, check.IsSystemNamed, options);
        builder.Append("CHECK ");

        if (check.IsNotForReplication)
        {
            builder.Append("NOT FOR REPLICATION ");
        }

        builder.Append(check.Definition);

        var statement = Terminate(builder, options).ToString();

        statements.Add(new Statement(
            GuardsEnabled(options)
                ? Guard(statement, ConstraintMissing(check.Name, tableName, options), context)
                : statement,
            batched: true));

        if (!check.IsDisabled)
        {
            return;
        }

        var disable = new StringBuilder();
        disable.Append("ALTER TABLE ").Append(tableName)
            .Append(" NOCHECK CONSTRAINT ").Append(Identifier(check.Name, options));
        statements.Add(new Statement(Terminate(disable, options).ToString(), batched: true));
    }

    /// <remarks>
    /// 排在最後，而且一定要排在索引與條件約束後面：掛在索引或條件約束上的說明，
    /// 在那個東西還不存在時執行就是一句錯誤。
    /// </remarks>
    private static void AppendExtendedProperties(
        List<Statement> statements,
        SqlObjectStructure structure,
        SqlScriptContext context)
    {
        if (!context.Options.IncludeExtendedProperties)
        {
            return;
        }

        foreach (var property in structure.ExtendedProperties)
        {
            statements.Add(new Statement(
                SqlExtendedPropertyScript.Build(
                    property,
                    structure.Object.SchemaName,
                    structure.Object.Name,
                    context.Options,
                    context.NewLine),
                batched: true));
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
    ///
    /// <see cref="SqlSetOptionOutput.FromCatalog"/> 依 <c>sys.tables</c> 反推建立
    /// 當時的值，而不是一律寫 <c>ON</c>：一張在 <c>OFF</c> 之下建起來的資料表，
    /// 用 <c>ON</c> 重建可能直接失敗——而失敗還算好的，計算資料行的運算式在兩種
    /// 設定下算出不同結果才是真的難查。
    /// </remarks>
    private static void AppendSetOptions(
        List<Statement> statements,
        SqlScriptOptions options,
        SqlTableStorage storage)
    {
        if (options.SetOptions == SqlSetOptionOutput.None)
        {
            return;
        }

        var fromCatalog = options.SetOptions == SqlSetOptionOutput.FromCatalog;
        var ansiNulls = !fromCatalog || storage.UsesAnsiNulls;
        var quotedIdentifier = !fromCatalog || storage.UsesQuotedIdentifier;

        statements.Add(new Statement("SET ANSI_NULLS " + OnOff(ansiNulls), batched: true));
        statements.Add(new Statement("SET QUOTED_IDENTIFIER " + OnOff(quotedIdentifier), batched: true));
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private string BuildCreateTable(SqlObjectStructure structure, SqlScriptContext context, string name)
    {
        var options = context.Options;
        var builder = new StringBuilder(EstimateCapacity(structure));
        builder.Append("CREATE TABLE ").Append(name);
        AppendOpenBrace(builder, context);
        AppendColumns(builder, structure, context, BuildInlineConstraints(structure, context));
        builder.Append(')');
        AppendDataSpace(builder, structure.Storage.DataSpace, options);
        AppendTextImageOn(builder, structure.Storage, options);

        var statement = Terminate(builder, options).ToString();

        return GuardsEnabled(options)
            ? Guard(statement, ObjectMissing(name, "U"), context)
            : statement;
    }

    /// <summary>
    /// <c>ON [檔案群組]</c>，或分割配置的 <c>ON [配置]([資料行])</c>。
    /// </summary>
    /// <remarks>
    /// 兩者在 <c>sys.data_spaces</c> 裡是同一張表、同一個名稱欄位，寫出來的 T-SQL
    /// 卻完全不同。分割配置查不到分割資料行時整段不寫：只寫 <c>ON [ps_Loan]</c>
    /// 是語法錯誤，而少一個 <c>ON</c> 子句只是讓它落到預設檔案群組——
    /// 兩者都不對，但後者至少執行得起來。
    /// </remarks>
    private static void AppendDataSpace(
        StringBuilder builder,
        SqlDataSpace? dataSpace,
        SqlScriptOptions options)
    {
        if (dataSpace is null || !options.IncludeFilegroup)
        {
            return;
        }

        if (!dataSpace.IsPartitionScheme)
        {
            builder.Append(" ON ").Append(Identifier(dataSpace.Name, options));
            return;
        }

        if (!options.IncludePartitionScheme || dataSpace.PartitionColumnName is null)
        {
            return;
        }

        builder.Append(" ON ").Append(Identifier(dataSpace.Name, options))
            .Append('(').Append(Identifier(dataSpace.PartitionColumnName, options)).Append(')');
    }

    /// <remarks>
    /// 有沒有 LOB 資料行一律問 <c>lob_data_space_id</c>，不要自己掃資料行的型別：
    /// <c>xml</c>、CLR 型別與 <c>varchar(max)</c> 都算，漏一種就是一份與來源
    /// 不同的資料表。
    ///
    /// 從屬於 <see cref="SqlScriptOptions.IncludeFilegroup"/>：<c>TEXTIMAGE_ON</c>
    /// 指的也是一個檔案群組，關掉「寫出檔案群組」卻留著它，得到的是一份指名了
    /// 半個儲存位置的指令碼——而那個檔案群組在目的地不一定存在。
    /// </remarks>
    private static void AppendTextImageOn(
        StringBuilder builder,
        SqlTableStorage storage,
        SqlScriptOptions options)
    {
        if (options.IncludeFilegroup &&
            options.IncludeTextImageOn &&
            storage.LobFilegroupName is { Length: > 0 } lob)
        {
            builder.Append(" TEXTIMAGE_ON ").Append(Identifier(lob, options));
        }
    }

    /// <summary>
    /// 在敘述前面包一層存在性判斷，讓整份指令碼可以重複執行。
    /// </summary>
    /// <remarks>
    /// 模組（程序、函式、觸發程序、檢視）<b>不</b>包：那幾種必須是批次裡的第一個
    /// 敘述，包進 <c>IF</c> 之後就不是了，只能改走 <c>EXEC('CREATE …')</c> 的
    /// 動態 SQL——而那會把定義原文變成一個字串，裡面的單引號要全部跳脫，
    /// 存回去的定義從此與來源不同。真的要那個效果的是 <c>CREATE OR ALTER</c>，
    /// 而它是版本相依的另一件事。
    ///
    /// 沒有名稱可以問的條件約束（選項省略了系統配的名稱）也不包：那時本來就
    /// 重複執行不了，包一層問不出答案的 <c>IF</c> 只是讓人以為包好了。
    /// </remarks>
    private static string Guard(string statement, string? condition, SqlScriptContext context) =>
        condition is null ? statement : "IF " + condition + context.NewLine + statement;

    /// <summary>物件不存在的條件；<paramref name="type"/> 是 <c>sys.objects.type</c>。</summary>
    private static string? ObjectMissing(string? quotedName, string type) =>
        quotedName is null
            ? null
            : "OBJECT_ID(" + SqlValueLiteral.Text(quotedName) + ", '" + type + "') IS NULL";

    /// <remarks>
    /// 索引名稱只在它所屬的資料表裡唯一，不在結構描述裡，所以問不了
    /// <c>OBJECT_ID</c>——那個函式對索引一律回傳 NULL，包出來的 <c>IF</c>
    /// 會永遠成立，於是第二次執行照樣失敗。
    /// </remarks>
    private static string IndexMissing(string indexName, string tableName) =>
        "NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(" +
        SqlValueLiteral.Text(tableName) + ") AND name = " +
        SqlValueLiteral.Text(indexName) + ")";

    private static bool GuardsEnabled(SqlScriptOptions options) =>
        options.ExistenceCheck == SqlExistenceCheck.IfNotExists;

    /// <summary><c>WITH (…)</c>，只有與預設值不同的那幾個選項。</summary>
    /// <remarks>
    /// 「預設值是什麼」由 <see cref="SqlIndexOptions.DescribeNonDefaults"/> 一份說了算：
    /// 排版這一層也記一份的話，其中一份忘了某個選項的預設值，那個選項就會在
    /// 每一份指令碼裡出現。
    /// </remarks>
    private static void AppendIndexOptions(
        StringBuilder builder,
        SqlIndexInfo index,
        SqlScriptOptions options)
    {
        if (!options.IncludeNonDefaultIndexOptions)
        {
            return;
        }

        var items = index.Options.DescribeNonDefaults();

        if (items.Count == 0)
        {
            return;
        }

        builder.Append(" WITH (");

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(items[i]);
        }

        builder.Append(')');
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
        AppendIndexOptions(builder, index, options);
        AppendDataSpace(builder, index.DataSpace, options);
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
        AppendIndexOptions(builder, index, options);
        AppendDataSpace(builder, index.DataSpace, options);

        var statement = Terminate(builder, options).ToString();

        return GuardsEnabled(options)
            ? Guard(statement, ConstraintMissing(index.Name, tableName, options), context)
            : statement;
    }

    /// <summary>
    /// 條件約束不存在的條件。
    /// </summary>
    /// <remarks>
    /// 條件約束的名稱在結構描述裡唯一，所以問得了 <c>OBJECT_ID</c>；但選項省略了
    /// 名稱時（系統配的那些）就沒有東西可以問，那時回 null 讓呼叫端整個不包。
    /// </remarks>
    private static string? ConstraintMissing(string name, string tableName, SqlScriptOptions options)
    {
        if (options.ConstraintNaming == SqlConstraintNaming.Never)
        {
            return null;
        }

        var schema = SchemaPrefixOf(tableName);

        return "NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID(" +
               SqlValueLiteral.Text(schema + Quote(name, options)) + "))";
    }

    /// <summary>從已經限定好的資料表名稱取出結構描述那一段（含結尾的點）。</summary>
    private static string SchemaPrefixOf(string qualifiedTableName)
    {
        var separator = qualifiedTableName.LastIndexOf('.');

        return separator < 0 ? string.Empty : qualifiedTableName.Substring(0, separator + 1);
    }

    private static string Quote(string name, SqlScriptOptions options) => Identifier(name, options);

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

        AppendIndexOptions(builder, index, options);
        AppendDataSpace(builder, index.DataSpace, options);

        var statement = Terminate(builder, options).ToString();

        return GuardsEnabled(options)
            ? Guard(statement, IndexMissing(index.Name, tableName), context)
            : statement;
    }

    /// <summary>把索引停回去。</summary>
    /// <remarks>
    /// <c>CREATE INDEX</c> 的 <c>WITH</c> 括號裡沒有「停用」這個選項，只能建完再停。
    /// 不停的話那張表會多出一個來源上不存在的索引——寫入會慢下來，
    /// 而查詢計畫也會跟著改變。
    /// </remarks>
    private static string BuildDisableIndex(SqlIndexInfo index, string tableName, SqlScriptContext context)
    {
        var builder = new StringBuilder();
        builder.Append("ALTER INDEX ").Append(Identifier(index.Name, context.Options))
            .Append(" ON ").Append(tableName).Append(" DISABLE");
        return Terminate(builder, context.Options).ToString();
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

        var statement = Terminate(builder, options).ToString();

        return GuardsEnabled(options)
            ? Guard(statement, ConstraintMissing(foreignKey.Name, tableName, options), context)
            : statement;
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

            if ((separated && statement.Batched) || statement.RequiresBatch)
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
