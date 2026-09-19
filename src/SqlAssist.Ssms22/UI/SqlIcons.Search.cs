using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Ssms22.UI;

internal static partial class SqlIcons
{
    /// <remarks>
    /// 影像目錄只有 <c>CHECK</c> 那一顆條件約束。主索引鍵、唯一鍵、外來鍵與 <c>DEFAULT</c> 共用它，
    /// 因為分類本身就把四種併成一顆 pill——分成四種圖示會讓同一顆 pill 篩出來的結果
    /// 看起來是四種不同的東西。
    /// </remarks>
    private static readonly Definition Constraint = new(KnownMonikers.CheckConstraint, "條件約束");

    /// <summary>
    /// 搜尋分類識別字對應的原生目錄圖示。
    /// </summary>
    /// <remarks>
    /// 以 <see cref="SqlCatalogSearchCategories.IdFor"/> 反推，而不是把 <c>"catalog.table"</c>
    /// 這種字串再抄一份：抄的那一份不會報錯，只會在分類 Id 改過之後安靜地少一顆圖示，
    /// 而畫面上看不出是漏了對照還是這一類本來就沒有圖示。
    ///
    /// 圖示本身走 <see cref="GetDefinition(SqlObjectKind)"/>，所以補全清單、QuickInfo、
    /// 結構預覽與這份結果清單是同一顆圖示、同一組語意配色。
    ///
    /// <b>不從 <c>SearchHit.ActivatePayload</c> 認這一筆是什麼。</b>那條紅線一破，清單就只
    /// 畫得出目錄物件，而加一個 provider 的代價會從「多一支啟動器」變成「改整份樣板」。
    /// </remarks>
    private static readonly Dictionary<string, ImageMoniker> CategoryMonikers = BuildCategoryMonikers();

    /// <summary>這個分類要畫哪一顆圖示；認不得時 false，呼叫端留空插槽而不是畫一顆不相干的。</summary>
    public static bool TryGetCategoryMoniker(string categoryId, out ImageMoniker moniker)
    {
        if (categoryId is null) throw new ArgumentNullException(nameof(categoryId));
        return CategoryMonikers.TryGetValue(categoryId, out moniker);
    }

    private static Dictionary<string, ImageMoniker> BuildCategoryMonikers()
    {
        var map = new Dictionary<string, ImageMoniker>(StringComparer.Ordinal);

        foreach (SqlObjectKind kind in Enum.GetValues(typeof(SqlObjectKind)))
        {
            if (SqlCatalogSearchCategories.IdFor(kind) is not { } id) continue;

            // 收納桶那幾種對到同一個 Id，只取先出現的那一顆。
            if (!map.ContainsKey(id)) map[id] = CategoryDefinition(kind).Moniker;
        }

        // SQL Agent 作業不是目錄物件，沒有 SqlObjectKind 可以反推，所以它的兩顆
        // 單獨接在這裡。不接的代價只是那幾列的圖示插槽留空（樣板本來就容許），
        // 但一排空插槽夾在有圖示的列中間，看起來像是那幾列還沒載入完。
        map[SqlAgentJobSearchCategories.JobCategoryId] = KnownMonikers.Calendar;
        map[SqlAgentJobSearchCategories.StepCategoryId] = KnownMonikers.Run;

        return map;
    }

    private static Definition CategoryDefinition(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Constraint => Constraint,

        // 收納桶收的是序列、同義字與資料表型別三種。挑其中任何一顆，另外兩種看起來都像被歸錯了，
        // 所以用「其他」本來的省略號——它回答的正是「這一顆不單獨佔一種」。
        SqlObjectKind.Synonym or SqlObjectKind.Sequence or SqlObjectKind.TableType => Other,
        _ => GetDefinition(kind)
    };
}
