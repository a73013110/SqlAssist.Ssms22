using System;
using System.Collections.Generic;
using System.Text;
using SqlAssist.Metadata.Formatting;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 物件的完整描述：欄位、參數與定義本文。這些是按需載入的第二、三層資料，
/// 只有使用者實際選取或滑鼠停留在該物件上時才會查詢。
/// </summary>
public sealed class SqlObjectDetail
{
    private static readonly SqlColumnInfo[] NoColumns = Array.Empty<SqlColumnInfo>();
    private static readonly SqlParameterInfo[] NoParameters = Array.Empty<SqlParameterInfo>();

    public SqlObjectDetail(
        SqlObjectInfo objectInfo,
        IReadOnlyList<SqlColumnInfo>? columns = null,
        IReadOnlyList<SqlParameterInfo>? parameters = null,
        string? definition = null)
    {
        Object = objectInfo ?? throw new ArgumentNullException(nameof(objectInfo));
        Columns = columns ?? NoColumns;
        Parameters = parameters ?? NoParameters;
        Definition = definition;
    }

    public SqlObjectInfo Object { get; }

    public IReadOnlyList<SqlColumnInfo> Columns { get; }

    public IReadOnlyList<SqlParameterInfo> Parameters { get; }

    /// <summary>
    /// 物件的 T-SQL 定義。
    /// </summary>
    /// <remarks>
    /// 兩個來源：模組來自 <c>OBJECT_DEFINITION</c>，同義字與序列由
    /// <see cref="SqlCatalogScript"/> 從目錄檢視組出來。刻意共用同一個欄位——
    /// 對每一個下游而言它們就是同一件事：一段照著執行就會得到這個物件的 T-SQL。
    ///
    /// 加密的模組、查不到那一列的同義字與序列，以及其餘種類都是 null。
    /// </remarks>
    public string? Definition { get; }

    /// <summary>
    /// 組出給預覽窗格與滑鼠停留提示使用的文字。
    /// 本身就是一組資料行的物件顯示欄位結構，其餘顯示定義本文（同義字與序列的
    /// 定義是本擴充自己從目錄檢視組出來的，見 <see cref="Definition"/>）。
    /// </summary>
    /// <remarks>
    /// 三道判斷的順序都是有理由的：
    ///
    /// 檢視同時有欄位也有定義，欄位優先——那是使用者停在一個檢視上時要看的東西。
    /// 資料表值函式反過來：它的資料行是回傳值的形狀，定義本文才同時說得出它吃
    /// 什麼引數、回傳什麼。順序顛倒的症狀是一個函式的預覽只剩一串資料行，
    /// 而看的人根本不知道要怎麼呼叫它。
    ///
    /// 最後那道 <see cref="Columns"/> 是給認不出來的種類留的：
    /// <see cref="SqlObjectKinds.IsTableShaped"/> 說不出它是什麼，
    /// 但既然真的查到了資料行，列出來仍然勝過一行光禿禿的標題。
    /// </remarks>
    public string BuildPreview()
    {
        if (Object.Kind.IsTableShaped())
        {
            return BuildColumnPreview();
        }

        if (!string.IsNullOrWhiteSpace(Definition))
        {
            return Definition!;
        }

        return Columns.Count > 0 ? BuildColumnPreview() : BuildSignaturePreview();
    }

    private string BuildColumnPreview()
    {
        var builder = new StringBuilder();
        builder.Append(Object.Kind.ToDisplayName()).Append(' ').AppendLine(Object.QualifiedName);

        if (Columns.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("（尚未載入欄位）");
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("(");

        for (var index = 0; index < Columns.Count; index++)
        {
            builder.Append("    ").Append(Columns[index].ToScriptLine());
            builder.AppendLine(index == Columns.Count - 1 ? string.Empty : ",");
        }

        builder.AppendLine(")");
        return builder.ToString();
    }

    private string BuildSignaturePreview()
    {
        var builder = new StringBuilder();
        builder.Append(Object.Kind.ToDisplayName()).Append(' ').AppendLine(Object.QualifiedName);

        if (Parameters.Count == 0)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine("Parameters");

        foreach (var parameter in Parameters)
        {
            builder.Append("    ").AppendLine(parameter.ToScriptLine());
        }

        return builder.ToString();
    }
}
