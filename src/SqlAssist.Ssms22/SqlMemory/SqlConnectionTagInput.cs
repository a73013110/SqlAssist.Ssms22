using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlAssist.Core.SqlMemory;
using SqlAssist.Ssms22.UI;

namespace SqlAssist.Ssms22.SqlMemory;

/// <summary>
/// 伺服器／資料庫名稱欄位：可自由輸入，下拉列出紀錄中出現過的名稱，減少一字之差篩不到的情形。
/// </summary>
/// <remarks>收藏編輯器與清除紀錄對話框共用；精確比對，留空表示不限。</remarks>
internal static class SqlConnectionTagInput
{
    public static TextBox CreateInput()
    {
        var input = SqlAssistChrome.CreateTextBox(SqlAssistChrome.DefaultMetrics);
        input.MaxLength = SqlFavoriteSave.MaxTagLength;
        input.ToolTip = "精確名稱；留空表示不限。";
        return input;
    }

    /// <param name="server">列資料庫時限定的伺服器；回傳 null 表示所有伺服器。</param>
    /// <param name="includeFavorites">是否把只在收藏標註出現過的名稱補在後面。</param>
    public static System.Windows.Controls.Border CreateBar(SqlAssistPackage package, SqlIcon icon, TextBox input, string label,
        bool databases, Func<string?> server, bool includeFavorites, Action<string> report)
    {
        var dropDown = SqlAssistChrome.CreateDropDownButton("選擇出現過的" + label);
        var menu = new ContextMenu { PlacementTarget = input, Placement = PlacementMode.Bottom };
        VsThemeBrushes.Apply(menu);
        dropDown.Click += (_, _) => _ = SqlMemoryActions.RunAsync(
            () => OpenSuggestionsAsync(package, menu, input, label, databases, server(), includeFavorites, report), report);
        return SqlAssistChrome.CreateInputBar(icon, input, dropDown);
    }

    private static async Task OpenSuggestionsAsync(SqlAssistPackage package, ContextMenu menu, TextBox input, string label,
        bool databases, string? server, bool includeFavorites, Action<string> report)
    {
        if (!SqlMemoryHost.Runtime.IsAvailable) { report("SQL Memory 尚未就緒，無法列出名稱。"); return; }
        var names = new List<string>();
        // 最近使用的連線在前，只在收藏標註出現過的名稱補在後面；同名只列一次。
        foreach (var favorites in includeFavorites ? new[] { false, true } : new[] { false })
            foreach (var name in await SqlMemoryHost.Runtime.ReadConnectionFacetsAsync(
                new SqlConnectionFacetRequest(favorites, databases, server), package.DisposalToken))
                if (!names.Contains(name)) names.Add(name);

        menu.Items.Clear();
        var clear = new MenuItem { Header = "不限（清除" + label + "）", Icon = SqlAssistChrome.CreateIcon(SqlIcon.Clear) };
        clear.Click += (_, _) => input.Clear();
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = "沒有記錄過的" + label, IsEnabled = false });
        foreach (var name in names)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = string.Equals(name, input.Text.Trim(), StringComparison.Ordinal) };
            item.Click += (_, _) => { input.Text = name; input.CaretIndex = name.Length; };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
