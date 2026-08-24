using System.Runtime.Serialization;

namespace SqlAssist.Core;

[DataContract]
public sealed class SqlAssistSuggestionSettings
{
    [DataMember(Name = "enabled", Order = 1)]
    public bool Enabled { get; set; } = true;

    [DataMember(Name = "triggerAfterCharacters", Order = 2)]
    public int TriggerAfterCharacters { get; set; } = 1;

    [DataMember(Name = "maximumItems", Order = 3)]
    public int MaximumItems { get; set; } = 100;

    [DataMember(Name = "showPreview", Order = 4)]
    public bool ShowPreview { get; set; } = true;

    [DataMember(Name = "delayMilliseconds", Order = 5)]
    public int DelayMilliseconds { get; set; } = 70;

    [DataMember(Name = "qualifyObjectNames", Order = 6)]
    public bool QualifyObjectNames { get; set; }

    [DataMember(Name = "useSquareBrackets", Order = 7)]
    public bool UseSquareBrackets { get; set; }

    /// <summary>
    /// 設定檔裡的引擎名稱。
    /// </summary>
    /// <remarks>
    /// 直接序列化列舉會被 <c>DataContractJsonSerializer</c> 寫成 0 與 1，
    /// 手動編輯 settings.json 的人無從判斷那是什麼，因此改存字串。
    /// 反序列化會略過建構式，欄位初始值不會執行，所以缺欄位時這裡是 null，
    /// 由 <see cref="Engine"/> 收斂成預設值。
    /// </remarks>
    [DataMember(Name = "engine", Order = 8)]
    private string? EngineName { get; set; }

    /// <summary>由誰負責顯示建議清單。無法辨識的值一律當成預設的原生引擎。</summary>
    [IgnoreDataMember]
    public CompletionEngine Engine
    {
        get => string.Equals(EngineName, "custom", System.StringComparison.OrdinalIgnoreCase)
            ? CompletionEngine.Custom
            : CompletionEngine.Native;
        set => EngineName = value == CompletionEngine.Custom ? "custom" : "native";
    }

    /// <summary>
    /// 顯示自己的清單時，一併關閉 SSMS 內建的 T-SQL IntelliSense 清單。
    /// </summary>
    /// <remarks>
    /// SSMS 的舊版語言服務由它自己的命令篩選器觸發，不會因為有新版建議來源就讓位，
    /// 因此不關掉就會同時看到兩份清單。
    /// 想徹底避免互搶，建議直接在「工具 → 選項 → 文字編輯器 → Transact-SQL →
    /// IntelliSense」關閉 SSMS 內建的 IntelliSense，再把這個選項關掉。
    /// </remarks>
    [DataMember(Name = "suppressNativeIntelliSense", Order = 9)]
    public bool SuppressNativeIntelliSense { get; set; } = true;

    public SqlAssistSuggestionSettings Clone()
    {
        return new SqlAssistSuggestionSettings
        {
            Enabled = Enabled,
            TriggerAfterCharacters = TriggerAfterCharacters,
            MaximumItems = MaximumItems,
            ShowPreview = ShowPreview,
            DelayMilliseconds = DelayMilliseconds,
            QualifyObjectNames = QualifyObjectNames,
            UseSquareBrackets = UseSquareBrackets,
            Engine = Engine,
            SuppressNativeIntelliSense = SuppressNativeIntelliSense
        };
    }
}
