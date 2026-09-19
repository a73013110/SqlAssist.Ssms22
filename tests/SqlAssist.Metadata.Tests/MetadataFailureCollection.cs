using Xunit;

namespace SqlAssist.Metadata.Tests;

/// <summary>
/// 會換掉 <see cref="Metadata.Caching.SqlMetadataFailure.Reporter"/> 的測試類別都放這一個集合。
/// </summary>
/// <remarks>
/// <see cref="Metadata.Caching.SqlMetadataFailure.Reporter"/> 是<b>行程共用的一個靜態委派</b>
/// ——那正是它的設計（Metadata 不能參照 Ssms22，所以用委派接線）。代價是：一個測試把它換成
/// 自己的收集器時，同時在別的執行緒上跑的測試所觸發的失敗也會寫進那一份收集器。
///
/// 症狀是<b>間歇性</b>的，而且錯得很有說服力：斷言會說「這裡應該只有一行，卻有兩行」，
/// 而多出來的那一行是另一條完全合理的失敗訊息。重跑一次多半就過了，
/// 於是很容易被當成環境問題放過去。
///
/// 停用平行化而不是只把這幾個類別放進同一個集合：同一個集合只保證這幾個<b>彼此</b>不同時跑，
/// 擋不住第三個類別在收集器開著的時候剛好觸發一次查詢失敗。
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetadataFailureCollection
{
    public const string Name = "中繼資料失敗回報";
}
