namespace SqlAssist.Ssms22.Settings;

/// <summary>
/// <c>SqlAssist.registration.json</c> 裡每一個設定的 moniker。
/// </summary>
/// <remarks>
/// Unified Settings 以字串定址，打錯字不會有編譯錯誤，只會在執行期
/// 安靜地回退到預設值——所以字串只在這裡出現一次。
/// </remarks>
internal static class SqlAssistMonikers
{
    /// <summary>整個分類的前綴；「設定…」命令用它定位設定頁。</summary>
    public const string Category = "sqlAssist";

    public const string Enabled = "sqlAssist.general.enabled";
    public const string UppercaseKeywordsOnType = "sqlAssist.general.uppercaseKeywordsOnType";
    public const string ExpandWildcardOnTab = "sqlAssist.general.expandWildcardOnTab";
    public const string WildcardLayout = "sqlAssist.general.wildcardLayout";

    public const string SuggestionsEnabled = "sqlAssist.suggestions.enabled";
    public const string TriggerAfterCharacters = "sqlAssist.suggestions.triggerAfterCharacters";
    public const string IncludeSnippets = "sqlAssist.suggestions.includeSnippets";
    public const string IncludeDatabaseObjects = "sqlAssist.suggestions.includeDatabaseObjects";
    public const string ShowCategoryFilters = "sqlAssist.suggestions.showCategoryFilters";
    public const string QualifyObjectNames = "sqlAssist.suggestions.qualifyObjectNames";
    public const string UseSquareBrackets = "sqlAssist.suggestions.useSquareBrackets";

    public const string HoverEnabled = "sqlAssist.structure.hoverEnabled";
    public const string PreviewMode = "sqlAssist.structure.previewMode";
    public const string PreviewDelay = "sqlAssist.structure.previewDelay";
    public const string PreviewPlacement = "sqlAssist.structure.previewPlacement";
    public const string PreviewFontSize = "sqlAssist.structure.previewFontSize";

    public const string VerboseLogging = "sqlAssist.diagnostics.verboseLogging";

    /// <summary>
    /// SSMS 內建 T-SQL IntelliSense 的總開關。
    /// </summary>
    /// <remarks>
    /// 由 SSMS 自己的 <c>RadLangSvc.registration.json</c> 註冊，不是我們的設定。
    /// 兩份建議清單同時出現時會互搶，設定頁的警告訊息與「關閉 SSMS 內建的
    /// T-SQL IntelliSense」命令都指向它。
    /// </remarks>
    public const string NativeIntelliSenseEnabled = "languages.sql.intelliSense.enableIntellisense";

    /// <summary>訂閱變更時要監看的 moniker；漏掉任何一個，改了設定就要重開查詢視窗才生效。</summary>
    public static readonly string[] All =
    {
        Enabled,
        UppercaseKeywordsOnType,
        ExpandWildcardOnTab,
        WildcardLayout,
        SuggestionsEnabled,
        TriggerAfterCharacters,
        IncludeSnippets,
        IncludeDatabaseObjects,
        ShowCategoryFilters,
        QualifyObjectNames,
        UseSquareBrackets,
        HoverEnabled,
        PreviewMode,
        PreviewDelay,
        PreviewPlacement,
        PreviewFontSize,
        VerboseLogging
    };
}
