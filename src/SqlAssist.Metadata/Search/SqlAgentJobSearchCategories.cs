using System;
using System.Collections.Generic;
using SqlAssist.Core.Search;

namespace SqlAssist.Metadata.Search;

/// <summary>
/// SQL Agent 作業來源自己的搜尋分類：作業與作業步驟。
/// </summary>
/// <remarks>
/// <b>刻意不併進目錄物件那幾顆 pill。</b>作業不是目錄物件——它不在 <c>sys.objects</c> 上、
/// 沒有結構描述、沒有 <c>object_id</c>，而且跨的是伺服器不是資料庫。塞進
/// <see cref="SqlCatalogSearchCategories.OtherCategoryId"/> 那個收納桶的症狀是使用者勾
/// 「Other」時同時拿到同義字、序列、資料表型別與作業，而那四種之間沒有任何關係；
/// 勾掉它則會連同義字一起消失。
///
/// 兩顆而不是一顆：作業與步驟是兩種不同的東西，而使用者要找的常常只有其中一種
/// （「哪一個作業叫這個名字」與「哪一個步驟在跑這段 SQL」）。併成一顆之後，
/// 一個有二十個步驟的作業會把那一顆 pill 的結果佔滿。
///
/// Id 是<b>穩定字串</b>，理由與目錄那一份相同：它會被寫進使用者偏好（記住上次勾了哪幾顆），
/// 改名等於使用者的過濾選擇整批失效。
/// </remarks>
public static class SqlAgentJobSearchCategories
{
    /// <summary>作業本身。</summary>
    public const string JobCategoryId = "agent-job.job";

    /// <summary>作業步驟。</summary>
    public const string StepCategoryId = "agent-job.step";

    /// <summary>
    /// 這兩顆 pill 屬於同一群。
    /// </summary>
    /// <remarks>
    /// 與 <see cref="SqlCatalogSearchCategories.ObjectGroupId"/> 分開，UI 才分得出
    /// 「資料庫物件」與「伺服器層級」是兩段。混在同一群的症狀是使用者以為作業也跟著
    /// 資料庫範圍走，而它不跟。
    /// </remarks>
    public const string GroupId = "agent-job";

    /// <summary>宣告給 UI 的分類清單，依顯示順序。</summary>
    public static IReadOnlyList<SearchCategory> Create(string providerId)
    {
        if (providerId is null)
        {
            throw new ArgumentNullException(nameof(providerId));
        }

        return new[]
        {
            new SearchCategory(providerId, JobCategoryId, "作業", 0, GroupId),
            new SearchCategory(providerId, StepCategoryId, "作業步驟", 1, GroupId)
        };
    }
}
