using System;
using System.Collections;
using System.Data.Common;
using System.Reflection;
using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Querying;

/// <summary>
/// 伺服器說的那句話是不是「權限不足」。
/// </summary>
/// <remarks>
/// 連不上、逾時與權限不足都是 <see cref="DbException"/>，都被降級成「這一輪沒有資料」，
/// 而呈現那一層要說的話完全不同：前兩者叫使用者重試或去看連線，最後一個叫他去要權限。
/// 分得出來的唯一線索是錯誤碼——訊息本文會隨伺服器的語言變，比對它等於在英文以外的
/// 執行個體上永遠分不出來。
///
/// <b>靠反射讀錯誤碼，不參照 SqlClient。</b>兩個理由，缺一都還是要反射：
/// 護欄寫明 Metadata 只依賴 <c>System.Data</c>（而 netstandard2.0 的
/// <see cref="DbException"/> 上沒有錯誤碼）；而且執行期真正丟出來的是
/// <c>Microsoft.Data.SqlClient.SqlException</c>，它與 <c>System.Data.SqlClient</c> 那一份
/// 是兩個不同的型別——即使參照了後者，<c>is</c> 也一次都不會成立，症狀是這一層安靜地
/// 永遠回 <see cref="SearchUnavailableKind.Unknown"/>。連線本來就一路宣告成
/// <see cref="System.Data.IDbConnection"/>（見 <see cref="ISqlConnectionSource"/>），
/// 這裡照同一條走。
///
/// 反射的代價只在失敗的那一次付：成功的路徑一次都不碰它。
/// </remarks>
internal static class SqlServerErrorCodes
{
    /// <summary>
    /// 判定「就是權限」的錯誤碼。
    /// </summary>
    /// <remarks>
    /// 名單刻意短而且只收斷言得起的那幾個：229／230 是物件與資料行的權限被拒，
    /// 262 是缺少陳述式層級權限，297／300 是缺少伺服器層級權限，
    /// 916／4060 是這個登入進不了那個資料庫。
    ///
    /// <b>18456（登入失敗）不在名單裡。</b>它是認證不是授權，下一步是去看帳號密碼或
    /// Entra 權杖，不是去要 <c>SELECT</c>；歸成「權限不足」會把使用者送到查不出問題的
    /// 那一邊。同理，逾時與連不上一個都不收——它們沒有錯誤碼，或給的是網路層的那一族。
    /// </remarks>
    private static readonly int[] Denied = { 229, 230, 262, 297, 300, 916, 4060 };

    /// <summary>
    /// 這個例外是不是權限問題；分不出來一律 <see cref="SearchUnavailableKind.Unknown"/>。
    /// </summary>
    /// <remarks>
    /// 一批裡只要有一個權限碼就算權限：被拒的那一條查詢失敗之後，伺服器常常再附上
    /// 幾句衍生訊息，而只看頂層那一個（＝第一個）會被它們擋掉。
    ///
    /// 反射失敗不擲出。認不得的例外型別（另一種驅動程式、測試的替身）只是分不出來，
    /// 而分不出來已經有一個值可以回；在降級路徑上再丟一次例外，正是這一族要避免的事。
    /// </remarks>
    internal static SearchUnavailableKind Classify(DbException exception)
    {
        if (exception is null) return SearchUnavailableKind.Unknown;

        try
        {
            if (ReadProperty(exception.GetType(), "Errors")?.GetValue(exception) is IEnumerable errors)
            {
                foreach (var error in errors)
                {
                    if (error is not null && IsDenied(ReadNumber(error))) return SearchUnavailableKind.Denied;
                }

                // 有錯誤清單而裡面一個權限碼都沒有：那就是別的原因。再問頂層的 Number
                // 沒有意義——它就是清單裡的第一個。
                return SearchUnavailableKind.Unknown;
            }

            return IsDenied(ReadNumber(exception)) ? SearchUnavailableKind.Denied : SearchUnavailableKind.Unknown;
        }
        catch (Exception)
        {
            return SearchUnavailableKind.Unknown;
        }
    }

    private static bool IsDenied(int? number)
    {
        if (number is not { } value) return false;

        foreach (var denied in Denied)
        {
            if (denied == value) return true;
        }

        return false;
    }

    /// <summary><c>Number</c> 不是 <see cref="int"/> 就當成沒有；不轉型也不猜。</summary>
    private static int? ReadNumber(object instance) =>
        ReadProperty(instance.GetType(), "Number")?.GetValue(instance) is int number ? number : null;

    private static PropertyInfo? ReadProperty(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property is not null && property.CanRead && property.GetIndexParameters().Length == 0
            ? property
            : null;
    }
}
