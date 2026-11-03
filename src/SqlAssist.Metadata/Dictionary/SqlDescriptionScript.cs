using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Scripting;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Dictionary;

/// <summary>
/// 一批說明變更寫成 T-SQL：直接執行與「在查詢視窗開啟」是同一份本文。
/// </summary>
/// <remarks>
/// 每一項各自用 <see cref="SqlExtendedPropertyScript"/>：有值時 add-or-update、清空時 drop，
/// 兩種都包存在性判斷，所以同一份指令碼重跑一次是同一個結果。兩條路各寫一份的症狀是
/// 預覽裡看到的那一份與真的執行的那一份不一樣，而使用者以為自己審過了。
/// </remarks>
public static class SqlDescriptionScript
{
    /// <summary>
    /// 只有變更本身，沒有交易與 <c>USE</c>：寫入端在自己開的交易裡執行它。
    /// </summary>
    public static string Statements(IReadOnlyList<SqlDescriptionChange> changes, string newLine)
    {
        if (changes is null) throw new ArgumentNullException(nameof(changes));

        var builder = new StringBuilder();

        foreach (var change in changes)
        {
            if (builder.Length > 0) builder.Append(newLine);

            var target = change.Target;
            var property = target.ToProperty(change.Value);
            builder.Append(change.IsRemoval
                    ? SqlExtendedPropertyScript.BuildDrop(property, target.SchemaName, target.ObjectName, newLine, target.ParentType)
                    : SqlExtendedPropertyScript.Build(property, target.SchemaName, target.ObjectName,
                        SqlExtendedPropertyProcedure.AddOrUpdate, newLine, target.ParentType))
                .Append(';').Append(newLine);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 給人看、也能貼到查詢視窗執行的完整指令碼：檔頭、<c>USE</c>、交易與全部變更。
    /// </summary>
    /// <remarks>
    /// <c>USE</c> 是必要的：查詢視窗開在哪個資料庫由 SSMS 決定，而擴充屬性的 level 引數只有結構描述與物件名稱，
    /// 在別的資料庫上同名的那張表會被默默改掉。<c>XACT_ABORT</c> 讓任何一項失敗都整批退回，
    /// 與直接執行那條路的語意相同。
    /// </remarks>
    public static string Build(string databaseName, IReadOnlyList<SqlDescriptionChange> changes, string newLine)
    {
        if (changes is null) throw new ArgumentNullException(nameof(changes));

        var builder = new StringBuilder();
        SqlScriptComment.AppendLine(builder, DictionaryScriptText.Heading(databaseName, changes.Count), newLine);
        builder.Append(newLine);

        if (!string.IsNullOrEmpty(databaseName))
        {
            builder.Append("USE ").Append(SqlIdentifier.Quote(databaseName)).Append(';').Append(newLine).Append(newLine);
        }

        builder.Append("SET XACT_ABORT ON;").Append(newLine)
            .Append("BEGIN TRANSACTION;").Append(newLine).Append(newLine)
            .Append(Statements(changes, newLine)).Append(newLine)
            .Append("COMMIT TRANSACTION;").Append(newLine);

        return builder.ToString();
    }

    /// <summary>
    /// 寫入前的併發檢查：每一項目標現在在伺服器上的值。
    /// </summary>
    /// <remarks>
    /// 欄位順序：change_index、current_value。沒有屬性的目標不會有列，讀取端當成空字串。
    /// 一次 <c>UNION ALL</c> 回來，不是每一項一輪來回；值在伺服器端轉成 <c>nvarchar(max)</c>，
    /// 理由同 <c>SqlMetadataQueries.Columns</c>——<c>sql_variant</c> 用 <c>GetValue</c> 收到的是裝箱的原生型別。
    /// </remarks>
    [Localizable(false)]
    public static string CurrentValuesQuery(IReadOnlyList<SqlDescriptionChange> changes, string newLine)
    {
        if (changes is null) throw new ArgumentNullException(nameof(changes));
        if (changes.Count == 0) throw new ArgumentException("至少要有一項變更。", nameof(changes));

        var builder = new StringBuilder();

        for (var index = 0; index < changes.Count; index++)
        {
            if (index > 0) builder.Append(newLine).Append("UNION ALL").Append(newLine);

            var target = changes[index].Target;
            builder.Append("SELECT ").Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(" AS change_index, CONVERT(nvarchar(max), value) AS current_value FROM ")
                .Append(SqlExtendedPropertyScript.Lookup(
                    target.ToProperty(string.Empty), target.SchemaName, target.ObjectName, target.ParentType));
        }

        return builder.Append(';').ToString();
    }
}
