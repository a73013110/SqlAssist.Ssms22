using System;
using System.ComponentModel;
using System.Text;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.ResultGrid;

namespace SqlAssist.Metadata.Formatting;

/// <summary>
/// 把一筆擴充屬性寫成 <c>sp_addextendedproperty</c>，或拿掉它（<c>sp_dropextendedproperty</c>）。
/// </summary>
/// <remarks>
/// 單獨一份而不是塞進 <see cref="TSqlScriptRenderer"/>：這一段的規則與資料表定義
/// 完全無關——八個位置引數、哪幾個要 <c>N</c> 前綴、哪幾個是 <c>NULL</c>，
/// 而填錯的症狀不是報錯，是屬性掛到另一個東西上。
///
/// 型別引數（<c>'SCHEMA'</c>、<c>'TABLE'</c>、<c>'COLUMN'</c>）刻意<b>不</b>加
/// <c>N</c>，名稱引數則一律加：SSMS 產生的指令碼就是這樣寫，而那不是巧合——
/// 型別是固定的關鍵字，名稱則可能有中文。
/// </remarks>
public static class SqlExtendedPropertyScript
{
    private const string AddProcedure = "sp_addextendedproperty";
    private const string UpdateProcedure = "sp_updateextendedproperty";
    private const string DropProcedure = "sp_dropextendedproperty";

    /// <summary>level1 的型別：資料表。</summary>
    public const string TableParent = "TABLE";

    /// <summary>level1 的型別：檢視；資料字典改檢視與它的資料行說明時用。</summary>
    public const string ViewParent = "VIEW";

    /// <param name="parentType">
    /// level1 的型別：<see cref="TableParent"/> 或 <see cref="ViewParent"/>。重建資料表的指令碼只用前者，
    /// 檢視與模組的指令碼是定義原文，本來就不重建擴充屬性；資料字典兩種都改。
    /// </param>
    public static string Build(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        SqlScriptOptions options,
        string newLine,
        string parentType = TableParent)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return Build(property, schemaName, parentName, options.ExtendedPropertyProcedure, newLine, parentType);
    }

    /// <summary>照指定的寫法加上（或改寫）一筆擴充屬性。</summary>
    /// <remarks>
    /// 程序的選擇由呼叫端給，不一定來自指令碼選項：資料字典寫回說明時一律
    /// <see cref="SqlExtendedPropertyProcedure.AddOrUpdate"/>，因為它改的是一個已經在用的資料庫，
    /// 「打開視窗之後別人先加了說明」只該變成 update，不該變成 add 失敗。
    /// </remarks>
    public static string Build(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        SqlExtendedPropertyProcedure procedure,
        string newLine,
        string parentType = TableParent)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        var arguments = BuildArguments(property, schemaName, parentName, parentType);

        if (procedure == SqlExtendedPropertyProcedure.Add)
        {
            return "EXEC " + AddProcedure + " " + arguments;
        }

        // 存在與否要在執行時才知道：同一份指令碼會套到已經有說明的資料庫，
        // 而 sp_addextendedproperty 對已存在的屬性是失敗，不是覆寫。
        var builder = new StringBuilder();
        builder.Append("IF NOT EXISTS (SELECT 1 FROM ")
            .Append(Lookup(property, schemaName, parentName, parentType))
            .Append(')').Append(newLine)
            .Append("    EXEC ").Append(AddProcedure).Append(' ').Append(arguments).Append(newLine)
            .Append("ELSE").Append(newLine)
            .Append("    EXEC ").Append(UpdateProcedure).Append(' ').Append(arguments);

        return builder.ToString();
    }

    /// <summary>拿掉一筆擴充屬性；不存在時什麼都不做。</summary>
    /// <remarks>
    /// 包一層存在性判斷：<c>sp_dropextendedproperty</c> 對不存在的屬性是失敗，而「清空說明」
    /// 的本意是「之後沒有說明」，別人先刪掉了也是同一個結果。<paramref name="property"/> 的值不寫進去。
    /// </remarks>
    public static string BuildDrop(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        string newLine,
        string parentType = TableParent)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        return "IF EXISTS (SELECT 1 FROM " + Lookup(property, schemaName, parentName, parentType) + ")" + newLine +
               "    EXEC " + DropProcedure + " " + BuildLookupArguments(property, schemaName, parentName, parentType);
    }

    /// <summary>
    /// 問這一筆屬性現在的值：<c>sys.fn_listextendedproperty(...)</c>，有屬性時一列（<c>value</c> 是
    /// <c>sql_variant</c>），沒有時零列。
    /// </summary>
    /// <remarks>寫入前的併發檢查與存在性判斷共用這一份，level 引數只組一次。</remarks>
    public static string Lookup(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        string parentType = TableParent)
    {
        if (property is null)
        {
            throw new ArgumentNullException(nameof(property));
        }

        return "sys.fn_listextendedproperty(" + BuildLookupArguments(property, schemaName, parentName, parentType) + ")";
    }

    /// <summary>名稱、值，以及三組 level 引數。</summary>
    private static string BuildArguments(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        string parentType)
    {
        var builder = new StringBuilder();
        builder.Append(SqlValueLiteral.Text(property.Name)).Append(", ")
            .Append(SqlValueLiteral.Text(property.Value)).Append(", ");
        AppendLevels(builder, property, schemaName, parentName, parentType);
        return builder.ToString();
    }

    /// <remarks>
    /// <c>fn_listextendedproperty</c> 收的是同一組 level 引數，只是把值換掉——
    /// 兩份各自組的話，其中一份漏掉 level2 就會判斷成「資料表上沒有這個屬性」，
    /// 於是每一個資料行的說明都走到 add 那一支去。
    /// </remarks>
    private static string BuildLookupArguments(
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        string parentType)
    {
        var builder = new StringBuilder();
        builder.Append(SqlValueLiteral.Text(property.Name)).Append(", ");
        AppendLevels(builder, property, schemaName, parentName, parentType);
        return builder.ToString();
    }

    private static void AppendLevels(
        StringBuilder builder,
        SqlExtendedProperty property,
        string schemaName,
        string parentName,
        string parentType)
    {
        builder.Append("'SCHEMA', ").Append(SqlValueLiteral.Text(schemaName)).Append(", ")
            .Append('\'').Append(parentType).Append("', ")
            .Append(SqlValueLiteral.Text(parentName)).Append(", ");

        if (property.Level == SqlExtendedPropertyLevel.Table || property.TargetName is null)
        {
            builder.Append("NULL, NULL");
            return;
        }

        builder.Append('\'').Append(LevelKeyword(property.Level)).Append("', ")
            .Append(SqlValueLiteral.Text(property.TargetName));
    }

    [Localizable(false)]
    private static string LevelKeyword(SqlExtendedPropertyLevel level)
    {
        switch (level)
        {
            case SqlExtendedPropertyLevel.Column:
                return "COLUMN";
            case SqlExtendedPropertyLevel.Index:
                return "INDEX";
            case SqlExtendedPropertyLevel.Constraint:
                return "CONSTRAINT";
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "未知的擴充屬性層級。");
        }
    }
}
