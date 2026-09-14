using System;

namespace SqlAssist.Core.SqlMemory;

/// <summary>
/// 宿主一次開啟的一份儲存：擷取、收藏、維護與租約四個契約共用同一個生命週期。
/// </summary>
/// <remarks>
/// 只是把四個聚合的契約綁在同一個可釋放的物件上，不是泛型 repository；
/// <see cref="IDisposable.Dispose"/> 必須等進行中的操作離開才釋放，呼叫端先取消自己的操作。
/// </remarks>
public interface ISqlMemoryStore : ISqlHistoryStore, ISqlFavoriteStore,
    ISqlMemoryMaintenanceStore, ISqlMemoryLeaseStore, IDisposable
{
}
