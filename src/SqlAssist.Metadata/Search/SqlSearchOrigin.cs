using System;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// 一筆搜尋結果是在哪一台伺服器上搜到的；provider 建立命中的那一刻就定下來。
/// </summary>
/// <remarks>
/// 結果清單會比搜尋範圍活得久：使用者換了查詢視窗或指名另一台之後，舊的那幾列還在畫面上。
/// 點下去時才去問「現在的範圍是哪一台」的話，答案是換過之後的那一台，而那一台上同名的物件、
/// 同號的 <c>object_id</c> 與這一筆毫無關係——導航看起來完全正常，只是跳到另一台伺服器上。
/// 所以伺服器跟著命中走，下游一律以它為準，不以當下的範圍代答。
///
/// <see cref="ServerName"/> 是<b>連線字串裡</b>的伺服器名稱，與物件總管節點的
/// <c>ServerName</c>、查詢視窗連線的伺服器同一種寫法；比對規則只有接線層的
/// <c>SqlSearchCatalogs.IsSameServer</c> 一份，這個型別不自己比、也不正規化。
/// 它<b>不是</b> <c>SERVERPROPERTY('ServerName')</c>：具名執行個體與別名連線上兩者不同，
/// 拿伺服器自報的名字去樹上找，找不到任何一台。
///
/// 目錄物件與作業共用這一個型別，不各帶一個字串：各帶一個的話，下一個來源會再發明一種
/// 寫法，而接線層得分辨每一種字串是哪一種名稱。
/// </remarks>
public sealed class SqlSearchOrigin
{
    public SqlSearchOrigin(string serverName)
    {
        if (string.IsNullOrEmpty(serverName))
        {
            // 說不出是哪一台就不該有結果：沒有伺服器的命中，下游只剩「照目前範圍猜」這條退路。
            throw new ArgumentException("伺服器名稱不可為空。", nameof(serverName));
        }

        ServerName = serverName;
    }

    /// <summary>連線字串裡的伺服器名稱（<c>Data Source</c>）。</summary>
    public string ServerName { get; }

    public override string ToString() => ServerName;
}
