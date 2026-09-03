using System;
using System.Collections.Generic;
using System.Globalization;

namespace SqlAssist.Metadata.ResultGrid;

/// <summary>
/// 一欄在 <c>CREATE TABLE #temp</c> 裡的型別，含長度與精確度。
/// </summary>
/// <remarks>
/// 拆成獨立一份的理由是這一段的失敗方式跟別處都不一樣：<b>寫錯了照樣建得起來、
/// 照樣插得進去，只是資料悄悄變短。</b>結果格線回報的型別名稱不帶括號
/// （<c>varchar</c> 而不是 <c>varchar(20)</c>），而 T-SQL 對省略的長度另有預設值，
/// 兩者湊起來就是實測那一句「字串或二進位資料會被截斷」——因為
/// <c>varchar</c> 在 <c>CREATE TABLE</c> 裡是 <c>varchar(1)</c>。
///
/// 一句錯誤訊息還算好的。<c>decimal</c> 省略精確度是 <c>decimal(18,0)</c>，
/// 小數點後面整段被四捨五入掉，沒有錯誤、沒有警告，而這個功能的用途正是
/// 「把線上那一份出問題的資料原封不動搬過來」。
///
/// 所以規則只有一條：<b>寧可放寬，不可猜窄。</b>問得到真正的長度就照抄，
/// 問不到就換成同一族裡裝得下任何值的那一個（<c>varchar(max)</c> 之類）。
/// 放寬的代價是暫存資料表的欄位比原本鬆，而那不會讓任何一個值變樣。
/// </remarks>
internal static class SqlTempTableColumnType
{
    /// <summary>非 Unicode 文字與二進位的長度上限；超過就得寫成 <c>(max)</c>。</summary>
    private const int MaxByteLength = 8000;

    /// <summary>Unicode 文字的長度上限。</summary>
    private const int MaxUnicodeLength = 4000;

    /// <summary><c>decimal</c>／<c>numeric</c> 的總位數上限。</summary>
    private const int MaxNumericPrecision = 38;

    /// <summary>
    /// 這一欄要寫進 <c>CREATE TABLE</c> 的型別；寫不出來時是空字串。
    /// </summary>
    /// <param name="literals">
    /// 這一欄每一列的字面值。只有 <c>decimal</c>／<c>numeric</c> 問不出精確度時
    /// 才會用到——那一族沒有 <c>(max)</c> 可以退，唯一還能放寬的方向是
    /// 「總位數取滿 38，小數位數取實際出現過的最多那一個」。
    /// </param>
    public static string For(ResultGridColumn column, IReadOnlyList<string> literals)
    {
        var baseName = column.BaseTypeName;

        switch (baseName)
        {
            // 型別名稱都問不出來，沒有東西可抄；由呼叫端整段拒絕。
            case "":
                return string.Empty;

            // timestamp／rowversion 由引擎自己產生，明確插值會直接失敗——
            // 建得起來卻插不進去，是這個功能最不該產出的那種指令碼。
            // varbinary(8) 是它實際的儲存形狀，值原封不動搬得過去。
            case "timestamp":
            case "rowversion":
                return "varbinary(8)";
        }

        // 伺服器已經連長度一起報出來時照抄。實測的 SSMS 22 不會，
        // 但這條路徑是「哪一天它開始報了」的正確答案，也是單元測試餵的那一種。
        if (column.ServerDataType.IndexOf('(') >= 0)
        {
            return column.ServerDataType;
        }

        switch (baseName)
        {
            case "char":
            case "varchar":
                return Sized(baseName, column.MaxLength, MaxByteLength, "varchar(max)");

            case "nchar":
            case "nvarchar":
                return Sized(baseName, column.MaxLength, MaxUnicodeLength, "nvarchar(max)");

            case "binary":
            case "varbinary":
                return Sized(baseName, column.MaxLength, MaxByteLength, "varbinary(max)");

            case "decimal":
            case "numeric":
                return Numeric(baseName, column, literals);

            // 其餘型別要嘛沒有長度可言（int、bit、uniqueidentifier），
            // 要嘛省略時的預設值就是最大值（datetime2 與 time 是 7、float 是 53），
            // 兩種都不會截斷。text／ntext／image 也在這裡：它們已經是 2 GB。
            default:
                return column.ServerDataType;
        }
    }

    /// <remarks>
    /// 長度問不出來時退到 <c>(max)</c> 而不是猜一個數字，理由與整段都寫成允許
    /// <c>NULL</c> 相同：格線知道的是「這一次查到的資料」，不是欄位的定義。
    /// 照觀察到的最長那一列開長度，使用者改資料重跑的時候就會莫名其妙被截斷。
    ///
    /// 退的時候一併從 <c>char</c> 換成 <c>varchar</c>：<c>char(max)</c> 不存在，
    /// 而定長字元型別在暫存資料表上唯一的差別是尾端補空白。
    /// </remarks>
    private static string Sized(string baseName, int? length, int max, string widened)
    {
        return length is int value && value >= 1 && value <= max
            ? baseName + "(" + value.ToString(CultureInfo.InvariantCulture) + ")"
            : widened;
    }

    /// <remarks>
    /// <c>decimal</c> 沒有 <c>(max)</c>，所以問不出精確度時只能從資料反推——
    /// 這是整個檔案裡唯一一處看值。安全的理由是同一欄的值來自同一個
    /// <c>decimal(p, s)</c>：每一列的小數位數都不超過 <c>s</c>，
    /// 整數位數都不超過 <c>p - s</c>，所以「總位數取滿 38、小數位數取實際最多的
    /// 那一個」一定裝得下這一欄的每一個值，也裝得下同一來源之後可能出現的值。
    /// </remarks>
    private static string Numeric(string baseName, ResultGridColumn column, IReadOnlyList<string> literals)
    {
        var precision = column.Precision;
        var scale = column.Scale;

        if (precision is int p && p >= 1 && p <= MaxNumericPrecision
            && scale is int s && s >= 0 && s <= p)
        {
            return baseName + "(" + p.ToString(CultureInfo.InvariantCulture)
                + ", " + s.ToString(CultureInfo.InvariantCulture) + ")";
        }

        return baseName + "(" + MaxNumericPrecision.ToString(CultureInfo.InvariantCulture)
            + ", " + ObservedScale(literals).ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>這一欄的字面值裡，小數點後面最多出現過幾位。</summary>
    /// <remarks>
    /// 只認小數點後面的十進位數字。<c>NULL</c> 與任何長得不像數字的東西一律跳過：
    /// 這一欄的基底型別已經是 <c>decimal</c>，出現別的形狀代表值那一層有更根本的
    /// 問題，而在這裡猜一個位數只會把它蓋掉。
    /// </remarks>
    private static int ObservedScale(IReadOnlyList<string> literals)
    {
        var widest = 0;

        foreach (var literal in literals)
        {
            var point = literal.IndexOf('.');

            if (point < 0)
            {
                continue;
            }

            var digits = 0;

            for (var index = point + 1; index < literal.Length; index++)
            {
                if (literal[index] < '0' || literal[index] > '9')
                {
                    digits = 0;
                    break;
                }

                digits++;
            }

            widest = Math.Max(widest, Math.Min(digits, MaxNumericPrecision));
        }

        return widest;
    }
}
