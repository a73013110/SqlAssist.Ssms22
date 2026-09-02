

namespace SqlAssist.Core.Settings;

/// <summary>
/// 把 <see cref="ISettingValueSource"/> 的原始值組成一份 <see cref="SqlAssistSettings"/>。
/// </summary>
/// <remarks>
/// 這裡是 moniker 與強型別屬性之間唯一的對應處，也是列舉字串解析與
/// 數值收斂發生的地方。<see cref="SqlAssistSettings"/> 保持成純粹的
/// 資料容器，讀取規則全部集中在這一支。
///
/// 新增設定時漏掉這裡的對應，功能端會安靜地永遠拿到預設值；
/// <c>SqlAssistSettingsReaderTests</c> 以假來源把每一個 moniker 都試過一次，
/// 讓那種遺漏變成測試失敗。
/// </remarks>
public static class SqlAssistSettingsReader
{
    /// <summary>
    /// 讀出一份完整的設定快照。
    /// </summary>
    /// <remarks>
    /// 任何一個值讀不到就用 <see cref="SqlAssistSettings"/> 的屬性預設值補上，
    /// 因此永遠會回傳一份可用的設定，不會丟例外。
    /// </remarks>
    public static SqlAssistSettings Read(ISettingValueSource source)
    {
        if (source is null)
        {
            return new SqlAssistSettings();
        }

        var defaults = new SqlAssistSettings();

        return new SqlAssistSettings
        {
            Enabled = Value(source, SqlAssistMonikers.Enabled, defaults.Enabled),
            UppercaseKeywordsOnType = Value(
                source,
                SqlAssistMonikers.UppercaseKeywordsOnType,
                defaults.UppercaseKeywordsOnType),
            AutoPairDelimiters = Value(
                source,
                SqlAssistMonikers.AutoPairDelimiters,
                defaults.AutoPairDelimiters),

            SuggestionsEnabled = Value(
                source,
                SqlAssistMonikers.SuggestionsEnabled,
                defaults.SuggestionsEnabled),
            SuppressNativeMemberList = Value(
                source,
                SqlAssistMonikers.SuppressNativeMemberList,
                defaults.SuppressNativeMemberList),
            TriggerAfterCharacters = SqlAssistLimits.ClampTriggerCharacters(
                Value(source, SqlAssistMonikers.TriggerAfterCharacters, defaults.TriggerAfterCharacters)),
            ShowCategoryFilters = Value(
                source,
                SqlAssistMonikers.ShowCategoryFilters,
                defaults.ShowCategoryFilters),
            IncludeSnippets = Value(source, SqlAssistMonikers.IncludeSnippets, defaults.IncludeSnippets),
            IncludeDatabaseObjects = Value(
                source,
                SqlAssistMonikers.IncludeDatabaseObjects,
                defaults.IncludeDatabaseObjects),

            QualifyObjectNames = Value(
                source,
                SqlAssistMonikers.QualifyObjectNames,
                defaults.QualifyObjectNames),
            UseSquareBrackets = Value(
                source,
                SqlAssistMonikers.UseSquareBrackets,
                defaults.UseSquareBrackets),
            ExpandWildcardOnTab = Value(
                source,
                SqlAssistMonikers.ExpandWildcardOnTab,
                defaults.ExpandWildcardOnTab),
            WildcardLayout = ParseWildcardLayout(
                Value(source, SqlAssistMonikers.WildcardLayout, string.Empty),
                defaults.WildcardLayout),
            ExpandAlterDefinition = Value(
                source,
                SqlAssistMonikers.ExpandAlterDefinition,
                defaults.ExpandAlterDefinition),
            ExpandInsertStatement = Value(
                source,
                SqlAssistMonikers.ExpandInsertStatement,
                defaults.ExpandInsertStatement),
            ExpandMergeStatement = Value(
                source,
                SqlAssistMonikers.ExpandMergeStatement,
                defaults.ExpandMergeStatement),
            ExpandProcedureCall = Value(
                source,
                SqlAssistMonikers.ExpandProcedureCall,
                defaults.ExpandProcedureCall),
            IncludeOptionalParameters = Value(
                source,
                SqlAssistMonikers.IncludeOptionalParameters,
                defaults.IncludeOptionalParameters),
            ExpandFunctionCall = Value(
                source,
                SqlAssistMonikers.ExpandFunctionCall,
                defaults.ExpandFunctionCall),

            HoverEnabled = Value(source, SqlAssistMonikers.HoverEnabled, defaults.HoverEnabled),
            PreviewMode = ParsePreviewMode(
                Value(source, SqlAssistMonikers.PreviewMode, string.Empty),
                defaults.PreviewMode),
            PreviewDelayMilliseconds = SqlAssistLimits.ClampPreviewDelay(
                Value(source, SqlAssistMonikers.PreviewDelay, defaults.PreviewDelayMilliseconds)),
            PreviewPlacement = ParsePlacement(
                Value(source, SqlAssistMonikers.PreviewPlacement, string.Empty),
                defaults.PreviewPlacement),
            PreviewFontSize = SqlAssistLimits.ClampPreviewFontSize(
                Value(source, SqlAssistMonikers.PreviewFontSize, (int)defaults.PreviewFontSize)),

            VerboseLogging = Value(source, SqlAssistMonikers.VerboseLogging, defaults.VerboseLogging)
        };
    }

    private static T Value<T>(ISettingValueSource source, string moniker, T fallback)
        where T : notnull =>
        source.TryGetValue<T>(moniker, out var value) ? value : fallback;

    /// <summary>無法辨識的值一律當成預設值，而不是列舉的第一個成員。</summary>
    private static SqlPreviewMode ParsePreviewMode(string value, SqlPreviewMode fallback)
    {
        return value switch
        {
            "off" => SqlPreviewMode.Off,
            "delay" => SqlPreviewMode.Delay,
            "rightArrow" => SqlPreviewMode.RightArrow,
            _ => fallback
        };
    }

    private static SqlWildcardLayout ParseWildcardLayout(string value, SqlWildcardLayout fallback)
    {
        return value switch
        {
            "onePerLine" => SqlWildcardLayout.OnePerLine,
            "oneLineWhenShort" => SqlWildcardLayout.OneLineWhenShort,
            "fillWidth" => SqlWildcardLayout.FillWidth,
            _ => fallback
        };
    }

    private static SqlPreviewPlacement ParsePlacement(string value, SqlPreviewPlacement fallback)
    {
        return value switch
        {
            "beside" => SqlPreviewPlacement.Beside,
            "stacked" => SqlPreviewPlacement.Stacked,
            _ => fallback
        };
    }
}
