using System;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SqlAssist.Core.Notifications;
using SqlAssist.Metadata.Formatting;
using SqlAssist.Metadata.Model;
using SqlAssist.Metadata.Search;
using SqlAssist.Ssms22.Completion;
using SqlAssist.Ssms22.Connections;
using SqlAssist.Ssms22.Editor;
using SqlAssist.Ssms22.Settings;

namespace SqlAssist.Ssms22.Search;

/// <summary>
/// 為預覽面板按需取回一個物件的完整定義。
/// </summary>
/// <remarks>
/// 與 <see cref="SqlSearchActivation"/> 同一條路徑、同一組出處：
/// <see cref="SqlMetadataService.GetStructureAsync"/> 取第三／四層，
/// <see cref="SqlObjectScript"/> 組指令碼（排版仍只有 <see cref="TSqlScriptRenderer"/> 一份）。
/// 差別只在選項——這裡是唯讀的閱讀表面，用預覽那一組；F12 那一條要拿去執行，多蓋三項。
///
/// <b>禁止</b>持有 <c>ISqlConnectionSource</c>：所有權在 <c>SqlMetadataCatalogRegistry</c>，
/// 換資料庫由 <see cref="SqlMetadataService"/> 照 <see cref="SqlObjectInfo.DatabaseName"/> 換目錄。
/// 查不到就說查不到，<b>禁止</b>退回拿目前連線裡同名的物件回答。
/// </remarks>
internal sealed class SqlSearchDefinitionLoader
{
    private readonly IServiceProvider _services;
    private readonly SqlSearchDefinitionCache _cache = new();

    internal SqlSearchDefinitionLoader(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    internal void Clear() => _cache.Clear();

    /// <summary>
    /// 取回這一筆指向的那個物件的完整定義。
    /// </summary>
    /// <remarks>
    /// 在 UI 執行緒上呼叫，也在 UI 執行緒上回來；查詢與組指令碼一律在背景。
    /// <b>禁止</b>在 UI 執行緒同步等待——第四層是五次查詢，而使用者只是按了一下方向鍵。
    /// </remarks>
    internal async Task<SqlSearchDefinitionText> LoadAsync(
        SqlCatalogSearchTarget target, CancellationToken cancellationToken)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // object_id 只在它自己那個資料庫裡唯一；快取鍵少了資料庫名，跨資料庫的兩個物件
        // 剛好同號時會互相冒充，而那份定義看起來完全正常。
        var key = target.DatabaseName + "\u0001" +
            target.ObjectId.ToString(CultureInfo.InvariantCulture);

        if (_cache.TryGet(key, out var cached)) return new SqlSearchDefinitionText(cached, null);

        // 中繼資料服務是每個查詢視窗一份，連線也在那裡；沒有查詢視窗就沒有連線可以問。
        var view = ActiveSqlEditor.Current;

        if (view is null)
        {
            return new SqlSearchDefinitionText("", "請先開啟一個已連線的 SQL 查詢視窗，才讀得到物件定義。");
        }

        var metadataService = SqlCompletionServices.GetMetadataService(view, _services);
        var objectInfo = new SqlObjectInfo(
            target.ObjectId, target.SchemaName, target.Name, target.Kind, target.DatabaseName);

        SqlObjectStructure? structure;

        try
        {
            // Task.Run 而不是直接 await：GetStructureAsync 在第一個 await 之前是同步跑的，
            // 留在 UI 執行緒上就是第四層查詢的準備工作卡住畫面。
            structure = await Task
                .Run(
                    () => metadataService.GetStructureAsync(
                        objectInfo, cancellationToken, NotificationOrigin.Typing),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbException error)
        {
            // 中繼資料那一層已經把查詢失敗降級成 null，這一道接的是降級接不到的那幾種
            // （連線在途中被關掉）。冒出去的話，平台邊界每選一列就記一份完整堆疊。
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            return new SqlSearchDefinitionText(
                "", $"取不到 {objectInfo.QualifiedName} 的定義：{error.Message}");
        }

        if (structure is null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(CancellationToken.None);
            return new SqlSearchDefinitionText(
                "",
                $"在 {target.DatabaseName} 取不到 {objectInfo.QualifiedName} 的定義，可能是連線已中斷或權限不足。");
        }

        // 種類與這一次的資料齊不齊由 SqlObjectScript 那一支問；任一道不過它就整段換成註解，
        // 這裡不再自己判斷，也不自己組任何一段 T-SQL。
        var script = await Task
            .Run(
                () => SqlObjectScript
                    .BuildEditable(structure, SqlScriptPreferences.Create(Environment.NewLine, structure.Object))
                    .Text,
                cancellationToken)
            .ConfigureAwait(false);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        _cache.Add(key, script);
        return new SqlSearchDefinitionText(script, null);
    }
}
