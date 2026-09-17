using System;
using System.ComponentModel;
using System.Globalization;
using SqlAssist.Core.SqlMemory;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>版本時間軸的一列；文字只由版本投影推導，不讀全文。</summary>
internal sealed class SqlFavoriteRevisionRow : INotifyPropertyChanged
{
    private bool _isNew;
    private bool _isFirst;
    private bool _isLast;
    private bool _contentMissing;
    private bool _canRevert;

    public SqlFavoriteRevisionRow(SqlFavoriteRevisionItem item) => Item = item ?? throw new ArgumentNullException(nameof(item));

    public event PropertyChangedEventHandler? PropertyChanged;

    public SqlFavoriteRevisionItem Item { get; }
    public bool IsCurrent => Item.IsCurrent;
    public string Preview => Item.Preview.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    public string RelativeTime => SqlMemoryTimeText.RelativeTime(Item.CreatedAt, DateTimeOffset.Now);
    public string Timestamp => Item.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>收藏自己的版本不分新增、編輯或回溯（三者同一個形狀）；引用自 History 的另外說明來源。</summary>
    public string Origin => Item.Reason == SqlRevisionReason.Favorite ? "收藏版本" : "引用自 History";

    public string Detail => ContentMissing
        ? Origin + " · 內容已清理"
        : Origin + " · " + Item.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字元";

    public string RevertLabel => IsCurrent ? "這已是目前版本" : CanRevert ? "回溯為新版本" : ContentMissing ? "內容已清理，無法回溯" : "內容與目前版本相同";

    /// <summary>剛出現在時間軸（續頁或回溯之後）；播一次進場後由時間軸清掉，捲動重用容器不重播。</summary>
    public bool IsNew { get => _isNew; set => Set(ref _isNew, value, nameof(IsNew)); }

    /// <summary>時間軸最上面一列：軌道從圓點開始，不向上延伸。</summary>
    public bool IsFirst { get => _isFirst; set => Set(ref _isFirst, value, nameof(IsFirst)); }

    /// <summary>已知的最舊一列（沒有下一頁）：軌道停在圓點，不暗示下面還有版本。</summary>
    public bool IsLast { get => _isLast; set => Set(ref _isLast, value, nameof(IsLast)); }

    public bool ContentMissing
    {
        get => _contentMissing;
        set { if (Set(ref _contentMissing, value, nameof(ContentMissing))) { Changed(nameof(Detail)); Changed(nameof(RevertLabel)); } }
    }

    public bool CanRevert
    {
        get => _canRevert;
        set { if (Set(ref _canRevert, value, nameof(CanRevert))) Changed(nameof(RevertLabel)); }
    }

    public void RefreshTime() => Changed(nameof(RelativeTime));

    private bool Set(ref bool field, bool value, string property)
    {
        if (field == value) return false;
        field = value; Changed(property);
        return true;
    }

    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
