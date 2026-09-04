using System;
using System.Collections.Generic;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Metadata.Model;

/// <summary>
/// 認出限定字最左邊那一段是結構描述、資料庫還是連結伺服器。
/// </summary>
/// <remarks>
/// 只看文字時 <c>dbo.</c>、<c>LibArchive.</c> 與 <c>SQL209.</c> 是同一個形狀，
/// 右對齊只能一律先當成結構描述。猜錯沒有徵兆：清單一筆都比不中，
/// 而使用者看到的只是「沒有建議」，分不出是打錯字還是這個功能不支援。
///
/// 分辨要的是這條連線上的三份名單，所以判斷放在中繼資料這一層；段位怎麼跟著挪
/// 是 <see cref="SqlObjectPath.TryRealign"/> 的事。兩邊各算一次的話，
/// 症狀是清單列得出來、Tab 下去卻少一段。
/// </remarks>
public static class SqlQualifierResolver
{
    /// <summary>
    /// 回傳重新對齊過的限定字；認不出來時原樣回傳。
    /// </summary>
    /// <remarks>
    /// 比對順序是結構描述、資料庫、連結伺服器，理由是「越近的越可信」：
    /// 結構描述就在眼前這個資料庫裡，而右對齊本來就猜它，猜對了不必動。
    /// 名稱撞在一起時（有人把連結伺服器取成本機某個資料庫的名字）選近的那一個
    /// ——選遠的會安靜地把清單換成另一台伺服器的內容，而畫面上看不出來。
    /// </remarks>
    public static SqlObjectPath Resolve(SqlObjectPath qualifier, SqlDatabaseSnapshot local)
    {
        if (qualifier is null)
        {
            throw new ArgumentNullException(nameof(qualifier));
        }

        if (local is null || qualifier.LeftmostQualifier is not { } head)
        {
            return qualifier;
        }

        if (Contains(local.Schemas, head))
        {
            return qualifier;
        }

        if (Contains(local.Databases, head))
        {
            return qualifier.TryRealign(SqlQualifierSlot.Database, out var database)
                ? database
                : qualifier;
        }

        if (Contains(local.LinkedServers, head))
        {
            return qualifier.TryRealign(SqlQualifierSlot.Server, out var server)
                ? server
                : qualifier;
        }

        // 三份名單都沒有，就維持右對齊的原判。這裡刻意不猜：猜出來的目標會讓
        // 下游真的去開一條連線，而使用者打到一半的名稱本來就什麼都不是。
        return qualifier;
    }

    private static bool Contains(IReadOnlyList<string> names, string value)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
