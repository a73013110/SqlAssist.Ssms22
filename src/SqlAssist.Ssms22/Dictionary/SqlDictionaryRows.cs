using System;
using System.ComponentModel;
using SqlAssist.Core.Localization;
using SqlAssist.Metadata.Dictionary;

namespace SqlAssist.Ssms22.Dictionary;

/// <summary>主清單的一列：一張資料表或一個檢視，連同它自己的說明與資料行說明的覆蓋率。</summary>
/// <remarks>
/// 說明的讀寫都經過 <see cref="SqlDictionaryBrowserModel"/>：同一個目標在主從與資料字典兩種檢視裡是同一份編輯，
/// 列只是一個看得到它的地方。列物件跨過濾留著（模型以目標為鍵快取），資料格的編輯狀態才不會在打字過濾時被換掉。
/// </remarks>
internal sealed class SqlDictionaryObjectRow : INotifyPropertyChanged
{
    private readonly SqlDictionaryBrowserModel _model;

    internal SqlDictionaryObjectRow(SqlDictionaryBrowserModel model, SqlDictionaryObject item)
    {
        _model = model;
        Object = item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SqlDictionaryObject Object { get; }

    public SqlDescriptionTarget Target => Object.Target;

    public string Name => Object.DisplayName;

    public string Kind => Object.Kind == SqlDictionaryObjectKind.View ? SqlKindText.View : SqlKindText.Table;

    /// <summary>物件本身的說明（含還沒寫回的編輯）；設下去就是一筆編輯。</summary>
    public string Description
    {
        get => _model.Edits.Current(Target, Object.Description);
        set => _model.SetDescription(Target, Object.Description, value);
    }

    public bool IsChanged => _model.Edits.IsChanged(Target);

    /// <summary>這張表連同資料行有沒有任何還沒寫回的編輯；主清單的變更記號照它。</summary>
    public bool HasChanges => IsChanged || _model.HasColumnChanges(Object);

    /// <summary>變更記號那一格；與 <see cref="SqlDictionaryColumnRow.ChangeMark"/> 同一個符號。</summary>
    public string ChangeMark => HasChanges ? SqlDictionaryColumnRow.ChangedSymbol : "";

    /// <summary>有說明的資料行數／資料行總數。</summary>
    public string Coverage => DictionaryText.Coverage(_model.DescribedColumns(Object), Object.Columns.Count);

    internal void Refresh() =>
        Raise(nameof(Description), nameof(IsChanged), nameof(HasChanges), nameof(ChangeMark), nameof(Coverage));

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>明細與資料字典的一列：一個資料行、它的型別與說明，以及同名資料行的比較。</summary>
internal sealed class SqlDictionaryColumnRow : INotifyPropertyChanged
{
    /// <summary>還沒套用的那一格前面的記號。</summary>
    internal const string ChangedSymbol = "●";

    private readonly SqlDictionaryBrowserModel _model;

    internal SqlDictionaryColumnRow(SqlDictionaryBrowserModel model, SqlDictionaryObject owner, SqlDictionaryColumn column)
    {
        _model = model;
        Owner = owner;
        Column = column;
        Target = owner.TargetOf(column);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SqlDictionaryObject Owner { get; }

    public SqlDictionaryColumn Column { get; }

    public SqlDescriptionTarget Target { get; }

    public string Name => Column.Name;

    public string ObjectName => Owner.DisplayName;

    public string DataType => Column.DataType;

    /// <summary>可為 NULL 時是 <c>NULL</c>，否則留空：一整欄的 NOT NULL 只是噪音，要看的是例外。</summary>
    public string Nullability => Column.IsNullable ? "NULL" : "";

    /// <summary>主索引鍵、識別、計算資料行；沒有時留空。</summary>
    public string Traits => DictionaryText.Traits(Column.IsPrimaryKey, Column.IsIdentity, Column.IsComputed);

    public string Description
    {
        get => _model.Edits.Current(Target, Column.Description);
        set => _model.SetDescription(Target, Column.Description, value);
    }

    public bool IsChanged => _model.Edits.IsChanged(Target);

    public string ChangeMark => IsChanged ? ChangedSymbol : "";

    public SqlColumnNameGroup? Group => _model.Groups.Find(Column.Name);

    /// <summary>同名資料行的摘要（<c>×3 · 型別不一</c>）；只有這一個時留空。</summary>
    public string SameName => DictionaryText.SameNameSummary(Group);

    /// <summary>同名資料行的型別與說明分布；滑鼠停在「同名」那一格時看。</summary>
    public string SameNameDetail => DictionaryText.SameNameDetail(Group);

    public bool IsInconsistent => Group is { IsInconsistent: true };

    /// <summary>這一格空著、而同名的那幾個都寫成同一句時，可以照抄的那一句。</summary>
    public string? Suggestion => Description.Length == 0 ? Group?.Suggestion : null;

    internal void Refresh() => Raise(nameof(Description), nameof(IsChanged), nameof(ChangeMark), nameof(Suggestion));

    internal void RefreshGroup() =>
        Raise(nameof(SameName), nameof(SameNameDetail), nameof(IsInconsistent), nameof(Suggestion));

    private void Raise(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
