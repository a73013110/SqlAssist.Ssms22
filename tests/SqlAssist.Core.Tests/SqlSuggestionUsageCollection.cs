using SqlAssist.Core.Completion;
using Xunit;

namespace SqlAssist.Core.Tests;

/// <summary>
/// 會動到 <see cref="SqlSuggestionUsage"/> 的測試類別統一歸這一個集合。
/// </summary>
/// <remarks>
/// xUnit 預設把不同測試類別平行執行，而「最近用過」是行程層級的靜態狀態：
/// 一邊的 <c>Record</c> 或 <c>Clear</c> 會落在另一邊兩次排名之間，斷言於是偶發失敗
/// （實際症狀是排名測試時好時壞，重跑就過）。類別內的 try/finally 清得乾淨，
/// 但擋不住另一個類別同時在寫。同一個集合裡的測試不平行，衝突就消失。
///
/// 沒有改成把使用紀錄注入排名器：那份狀態要同時被 Ssms22 的提交端寫、排名端讀，
/// 拆成可注入之後就得自己維護「兩邊拿到的是同一個實例」，等於把一個唯一出處
/// 換成一條看不見的接線約定。為了測試隔離付這個代價不划算——這裡要的只是不要同時跑。
///
/// 新增會呼叫 <see cref="SqlSuggestionUsage"/> 的測試類別時，記得一併掛上
/// <c>[Collection(nameof(SqlSuggestionUsageCollection))]</c>，否則偶發失敗會再回來。
/// </remarks>
[CollectionDefinition(nameof(SqlSuggestionUsageCollection))]
public sealed class SqlSuggestionUsageCollection
{
}
