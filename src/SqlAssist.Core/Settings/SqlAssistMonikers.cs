using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SqlAssist.Core.Settings;

/// <summary>
/// <c>SqlAssist.registration.json</c> 裡每一個設定的 moniker。
/// </summary>
/// <remarks>
/// Unified Settings 以字串定址，打錯字不會有編譯錯誤，只會在執行期
/// 安靜地回退到預設值——所以字串只在這裡出現一次。
///
/// 放在 Core 而不是 SSMS 專案：真正屬於平台的是
/// <c>ISettingsReader</c> 與 <c>SVsUnifiedSettingsManager</c>，
/// 不是這些字串。moniker 是設定模型的一部分，跟著
/// <see cref="SqlAssistSettingsReader"/> 走才能被測到。
/// </remarks>
public static class SqlAssistMonikers
{
    /// <summary>整個分類的前綴；「設定…」命令用它定位設定頁，也是 <see cref="All"/> 的篩選條件。</summary>
    public const string Category = "sqlAssist";

    public const string Enabled = "sqlAssist.general.enabled";
    public const string UppercaseKeywordsOnType = "sqlAssist.general.uppercaseKeywordsOnType";
    public const string ExpandWildcardOnTab = "sqlAssist.general.expandWildcardOnTab";
    public const string WildcardLayout = "sqlAssist.general.wildcardLayout";
    public const string AutoPairDelimiters = "sqlAssist.general.autoPairDelimiters";

    public const string SuggestionsEnabled = "sqlAssist.suggestions.enabled";
    public const string SuppressNativeMemberList = "sqlAssist.suggestions.suppressNativeMemberList";
    public const string TriggerAfterCharacters = "sqlAssist.suggestions.triggerAfterCharacters";
    public const string IncludeSnippets = "sqlAssist.suggestions.includeSnippets";
    public const string IncludeDatabaseObjects = "sqlAssist.suggestions.includeDatabaseObjects";
    public const string ShowCategoryFilters = "sqlAssist.suggestions.showCategoryFilters";
    public const string QualifyObjectNames = "sqlAssist.suggestions.qualifyObjectNames";
    public const string UseSquareBrackets = "sqlAssist.suggestions.useSquareBrackets";
    public const string ExpandInsertStatement = "sqlAssist.suggestions.expandInsertStatement";

    public const string ExpandMergeStatement = "sqlAssist.suggestions.expandMergeStatement";
    public const string ExpandProcedureCall = "sqlAssist.suggestions.expandProcedureCall";
    public const string ExpandFunctionCall = "sqlAssist.suggestions.expandFunctionCall";

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
    /// 由 SSMS 自己的 <c>RadLangSvc.registration.json</c> 註冊，不是我們的設定，
    /// 前綴也不是 <see cref="Category"/>，所以不會被 <see cref="All"/> 收進去——
    /// 本來就不該訂閱別人的設定。
    ///
    /// 這個擴充<b>不會</b>去動它，只讀來顯示在「關於與診斷」裡。它是總開關：
    /// 同一份註冊檔裡的 <c>underlineErrors</c>（紅色錯誤波浪線）與
    /// <c>autoOutlining</c> 都以 <c>enableWhen</c> 掛在它底下，關掉它等於
    /// 連錯誤檢查一起關掉。要擋的只是它自動彈出的那份清單，那走
    /// <see cref="SuppressNativeMemberList"/>。
    /// </remarks>
    public const string NativeIntelliSenseEnabled = "languages.sql.intelliSense.enableIntellisense";

    /// <summary>
    /// 這個擴充自己的全部 moniker，訂閱變更時要監看的就是這一份。
    /// </summary>
    /// <remarks>
    /// 由上面的常數反射產生，而不是再手寫一次清單：手寫的版本漏掉一個
    /// 不會有任何徵兆，只會變成「改了設定要重開查詢視窗才生效」。
    /// 只在型別初始化時跑一次。
    /// </remarks>
    public static readonly string[] All = Discover();

    private static string[] Discover()
    {
        var prefix = Category + ".";

        return typeof(SqlAssistMonikers)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(moniker => moniker is not null && moniker.StartsWith(prefix, StringComparison.Ordinal))
            .Select(moniker => moniker!)
            .OrderBy(moniker => moniker, StringComparer.Ordinal)
            .ToArray();
    }
}
