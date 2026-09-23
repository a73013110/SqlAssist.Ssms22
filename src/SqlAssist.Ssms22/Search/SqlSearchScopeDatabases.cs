using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Metadata.Caching;
using SqlAssist.Metadata.Querying;
using SqlAssist.Metadata.Search;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 搜尋範圍那顆下拉要列哪幾個資料庫；問一次、留一份。
/// </summary>
/// <remarks>
/// 清單走 <see cref="SqlCatalogSearchDatabases.TryList"/>，不讀
/// <see cref="SqlMetadataCatalog.CachedSnapshot"/>：那一份是補全在按鍵路徑上載入的，
/// 選物件總管上一台伺服器時它從頭到尾是空的——症狀是使用者選了伺服器，資料庫下拉
/// 一個選項都沒有，而那台上明明有幾十個庫。
///
/// <b>禁止</b>在沒有人打開下拉時先問：一條輕查詢仍然是一條連線，而且十個查詢視窗
/// 各開一次等於十輪。這一支不建任何索引；範圍是「全部」時要搜哪幾個由 provider 每一輪
/// 自己問，不讀這一份。
///
/// 只留<b>目前這條連線</b>那一份。換一台再換回來要多付一條查詢，而照鍵留好幾份的
/// 代價是清單會過期得看不出來，以及一份永遠不會被丟掉的字典。
///
/// 連線來源只在呼叫的那一刻從目錄上取，不留欄位：所有權在
/// <see cref="SqlMetadataCatalogRegistry"/>，留著的症狀是之後每一輪都以
/// <see cref="ObjectDisposedException"/> 收場（見 <see cref="SqlSearchCatalogs"/>）。
///
/// 除了 <see cref="SqlCatalogSearchDatabases.TryList"/> 本身，其餘都只在 UI 執行緒上跑。
/// </remarks>
internal sealed class SqlSearchScopeDatabases : IDisposable
{
    /// <summary>
    /// 取清單用的那條查詢；測試換掉它就不必真的連資料庫。
    /// </summary>
    /// <remarks>
    /// 留這個接縫而不是讓測試自己造一台伺服器：這個型別要驗的是「問幾次、換連線怎麼算、
    /// 問不到留哪一份」，那幾條與伺服器回什麼無關。
    /// </remarks>
    private readonly Func<ISqlConnectionSource, CancellationToken, IReadOnlyList<SqlCatalogSearchDatabase>?> _list;

    private readonly CancellationTokenSource _cancellation = new();

    private string _key = "";
    private IReadOnlyList<SqlCatalogSearchDatabase> _items = Array.Empty<SqlCatalogSearchDatabase>();
    private Task<IReadOnlyList<SqlCatalogSearchDatabase>?>? _loading;
    private bool _loaded;
    private bool _unavailable;
    private bool _disposed;

    internal SqlSearchScopeDatabases(
        Func<ISqlConnectionSource, CancellationToken, IReadOnlyList<SqlCatalogSearchDatabase>?>? list = null) =>
        _list = list ?? ((source, token) => SqlCatalogSearchDatabases.TryList(source, token));

    /// <summary>手上已經有的那一份；還沒問過或換了連線時是空的。</summary>
    public IReadOnlyList<SqlCatalogSearchDatabase> Items => _items;

    /// <summary>這條連線的清單已經問到了；問不到不算，下一次展開要再試。</summary>
    public bool IsLoaded => _loaded;

    /// <summary>上一次問不到（連不上、逾時或沒有權限）；手上那一份可能是舊的。</summary>
    public bool IsUnavailable => _unavailable;

    /// <summary>正在問；面板據此顯示那一行轉圈。</summary>
    public bool IsLoading => _loading is not null;

    /// <summary>
    /// 把手上這一份標成舊的，下一次展開重問。
    /// </summary>
    /// <remarks>
    /// 「重新整理」要的正是這個：剛建好的資料庫不在上一次的清單裡，而那正是使用者按它的理由。
    /// 名稱<b>不</b>當場清掉——重問失敗的話，清掉等於因為按了一下重新整理而讓清單消失，
    /// 而它本來是對的。換連線走 <see cref="Reset"/>，那一份要清。
    /// </remarks>
    public void Invalidate()
    {
        _loaded = false;
        _unavailable = false;
        _loading = null;
    }

    /// <summary>換連線：上一台的名稱一個都不適用，連手上那一份一起丟。</summary>
    private void Reset()
    {
        Invalidate();
        _items = Array.Empty<SqlCatalogSearchDatabase>();
    }

    /// <summary>
    /// 這一份清單屬於哪一條連線；換了就整份丟掉。
    /// </summary>
    /// <remarks>
    /// 比對獨立成一支、而且由<b>連線觀測</b>呼叫，不留在 <see cref="EnsureAsync"/> 裡：宿主在
    /// 面板展開時會先畫手上這一份（已經勾起來的條件必須看得見），只有在它需要重問時才走
    /// <see cref="EnsureAsync"/>。比對藏在後面的症狀是換一台伺服器之後 <see cref="IsLoaded"/>
    /// 仍是上一台的 true，宿主據此提早收工，下拉從此畫著上一台的資料庫，而且不會自己好。
    /// </remarks>
    public void SyncTo(SqlMetadataCatalog? catalog)
    {
        // 沒有目錄不算換連線：切到沒有連線的查詢視窗時清掉清單，回來還要再付一條查詢，
        // 而它本來就是對的。與 <see cref="EnsureAsync"/> 同一條規則。
        if (_disposed || catalog is null) return;

        var key = catalog.CacheKey;
        if (string.Equals(key, _key, StringComparison.Ordinal)) return;

        _key = key;
        Reset();
    }

    /// <summary>
    /// 這條連線進得去哪幾個資料庫；沒有連線時是空的。
    /// </summary>
    /// <remarks>
    /// 同一輪還在飛的時候再打開一次下拉不會再送一條查詢：兩條一模一樣的查詢只是把同一份
    /// 答案讀兩遍，而其中一條回來得晚，畫面會先補上清單再閃一次。
    ///
    /// 問不到時<b>留著上一份</b>並把 <see cref="IsUnavailable"/> 舉起來：清空等於說
    /// 「這台伺服器上一個都進不去」，而那與「這一次問不到」是兩件事。
    /// </remarks>
    public async Task<IReadOnlyList<SqlCatalogSearchDatabase>> EnsureAsync(SqlMetadataCatalog? catalog)
    {
        if (_disposed || catalog is null) return _items;

        // 換了連線：上一台的名稱一個都不適用，而正在飛的那一輪答的也是上一台。宿主在連線
        // 觀測時已經同步過，這一道是給沒有經過那條路的呼叫端（測試、直接展開）收尾的。
        SyncTo(catalog);
        var key = _key;

        if (_loaded) return _items;

        if (_loading is null)
        {
            var source = catalog.ConnectionSource;
            var token = _cancellation.Token;
            _loading = Task.Run(() => _list(source, token), token);
        }

        var loading = _loading;
        var databases = await loading.ConfigureAwait(true);

        // 晚到的答案不得蓋掉現在這一份：使用者可能在等的期間換過伺服器，或按過重新整理。
        if (_disposed || !ReferenceEquals(loading, _loading) || !string.Equals(key, _key, StringComparison.Ordinal))
        {
            return _items;
        }

        _loading = null;
        _unavailable = databases is null;

        if (databases is not null)
        {
            _items = databases;
            _loaded = true;
        }

        return _items;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
