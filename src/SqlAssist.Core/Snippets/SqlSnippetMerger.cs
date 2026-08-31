using System;
using System.Collections.Generic;
using System.Linq;
using SqlAssist.Core.Keywords;

namespace SqlAssist.Core.Snippets;

/// <summary>內建定義、override、停用紀錄與 v1 遷移的純文字邏輯。</summary>
public static class SqlSnippetMerger
{
    public static SqlSnippetConfiguration Merge(
        SqlSnippetLibrary defaults,
        SqlSnippetDocument document)
    {
        if (defaults is null)
        {
            throw new ArgumentNullException(nameof(defaults));
        }

        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var records = new Dictionary<string, SqlSnippetOverride>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in document.Snippets)
        {
            if (!string.IsNullOrWhiteSpace(record.Id))
            {
                // 手改檔案撞到相同 ID 時以最後一筆為準，最接近一般設定檔的覆寫直覺。
                records[record.Id] = record;
            }
        }

        var entries = new List<SqlSnippetConfigurationEntry>(defaults.Count + document.Snippets.Count);
        var builtInIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in defaults.Snippets)
        {
            builtInIds.Add(definition.Id);

            if (!records.TryGetValue(definition.Id, out var record))
            {
                entries.Add(new SqlSnippetConfigurationEntry(
                    definition,
                    isBuiltIn: true,
                    isCustomized: false,
                    isDisabled: false));
                continue;
            }

            if (record.Disabled || record.Snippet is null)
            {
                entries.Add(new SqlSnippetConfigurationEntry(
                    definition,
                    isBuiltIn: true,
                    isCustomized: true,
                    isDisabled: true));
                continue;
            }

            entries.Add(new SqlSnippetConfigurationEntry(
                WithId(record.Snippet, definition.Id),
                isBuiltIn: true,
                isCustomized: true,
                isDisabled: false));
        }

        foreach (var record in document.Snippets)
        {
            if (record.Disabled || record.Snippet is null || builtInIds.Contains(record.Id))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(record.Id) &&
                records.TryGetValue(record.Id, out var winner) &&
                !ReferenceEquals(record, winner))
            {
                continue;
            }

            var id = SqlSnippetIdentity.IsValid(record.Id)
                ? record.Id
                : SqlSnippetIdentity.CreateMigratedId(record.Snippet.Shortcut);
            entries.Add(new SqlSnippetConfigurationEntry(
                WithId(record.Snippet, id),
                isBuiltIn: false,
                isCustomized: false,
                isDisabled: false));
        }

        var winners = ChooseShortcutWinners(entries);
        var effective = new List<SqlSnippet>(winners.Count);

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            if (entry.IsDisabled)
            {
                continue;
            }

            if (winners.Contains(entry))
            {
                effective.Add(entry.Snippet);
                continue;
            }

            // 使用者項目優先，被遮住的低優先項目這一輪不進清單——但那是計算結果，
            // 不是使用者停用了它。標成 IsDisabled 的話，存檔就會替它寫下永久的
            // 停用紀錄，之後把撞名的那一筆改名也救不回來。
            entries[index] = new SqlSnippetConfigurationEntry(
                entry.Snippet,
                entry.IsBuiltIn,
                entry.IsCustomized,
                isDisabled: false,
                isShadowed: true);
        }

        return new SqlSnippetConfiguration(
            new SqlSnippetLibrary(effective),
            entries,
            document);
    }

    /// <summary>
    /// 把管理介面的完整清單縮成只含差異的 v2 文件。
    /// </summary>
    /// <remarks>
    /// 收的是<b>全部</b>項目而不是有效清單：被遮住的項目仍然是使用者的資料，
    /// 只是這一輪沒進建議清單。只傳有效清單的話，這裡分不出「使用者刪掉了它」
    /// 與「它的捷徑這一輪被別人佔走」，於是後者也會被寫成永久的停用紀錄。
    /// </remarks>
    public static SqlSnippetDocument CreateOverrides(
        IReadOnlyList<SqlSnippetConfigurationEntry> entries,
        SqlSnippetLibrary defaults)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        if (defaults is null)
        {
            throw new ArgumentNullException(nameof(defaults));
        }

        var records = new List<SqlSnippetOverride>();
        var consumed = new HashSet<SqlSnippetConfigurationEntry>();
        var byId = new Dictionary<string, SqlSnippetConfigurationEntry>(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(defaults.Snippets.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Snippet.Id) && !byId.ContainsKey(entry.Snippet.Id))
            {
                byId[entry.Snippet.Id] = entry;
            }
        }

        foreach (var definition in defaults.Snippets)
        {
            if (!byId.TryGetValue(definition.Id, out var entry))
            {
                // 管理介面裡整筆不見了＝使用者刪掉它，那才寫停用紀錄。
                records.Add(new SqlSnippetOverride(definition.Id, disabled: true));
                continue;
            }

            consumed.Add(entry);

            if (entry.IsDisabled)
            {
                records.Add(new SqlSnippetOverride(definition.Id, disabled: true));
                continue;
            }

            var current = WithId(entry.Snippet, definition.Id);

            if (!AreEquivalent(current, definition))
            {
                records.Add(new SqlSnippetOverride(definition.Id, disabled: false, current));
            }
        }

        foreach (var entry in entries)
        {
            // 自訂項目沒有可以還原的內建定義，停用它就是刪掉它。
            if (consumed.Contains(entry) || entry.IsDisabled)
            {
                continue;
            }

            var id = SqlSnippetIdentity.IsValid(entry.Snippet.Id) && usedIds.Add(entry.Snippet.Id)
                ? entry.Snippet.Id
                : NextCustomId(usedIds);
            records.Add(new SqlSnippetOverride(id, disabled: false, WithId(entry.Snippet, id)));
        }

        return new SqlSnippetDocument(SqlSnippetLibrary.CurrentVersion, records);
    }

    /// <summary>把 v1 的完整清單轉成 v2 差異；相同輸入永遠產生相同 ID 與內容。</summary>
    public static SqlSnippetDocument MigrateVersion1(
        SqlSnippetDocument legacy,
        SqlSnippetLibrary defaults)
    {
        if (legacy.Version != 1)
        {
            throw new ArgumentException("只有 v1 文件可以走 v1 遷移。", nameof(legacy));
        }

        var legacyItems = legacy.Snippets
            .Where(item => !item.Disabled && item.Snippet is not null)
            .Select(item => item.Snippet!)
            .ToList();
        var records = new List<SqlSnippetOverride>();
        var consumed = new HashSet<SqlSnippet>();
        var usedIds = new HashSet<string>(defaults.Snippets.Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
        var claimedBuiltInIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var frozen in SqlSnippetDefaults.LegacyVersion1.Snippets)
        {
            var old = legacyItems.FirstOrDefault(item =>
                string.Equals(item.Shortcut, frozen.Shortcut, StringComparison.OrdinalIgnoreCase));
            var current = defaults.Snippets.FirstOrDefault(item =>
                string.Equals(item.Shortcut, frozen.Shortcut, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                continue;
            }

            claimedBuiltInIds.Add(current.Id);

            if (old is null)
            {
                records.Add(new SqlSnippetOverride(current.Id, disabled: true));
                continue;
            }

            consumed.Add(old);

            if (!AreLegacyEquivalent(old, frozen))
            {
                records.Add(new SqlSnippetOverride(
                    current.Id,
                    disabled: false,
                    FromLegacy(old, current.Id, current)));
            }
        }

        foreach (var snippet in legacyItems)
        {
            if (consumed.Contains(snippet))
            {
                continue;
            }

            if (defaults.TryGet(snippet.Shortcut, out var newlyBuiltIn))
            {
                // v1 時代的自訂捷徑可能在 v2 變成內建項目。讓使用者內容成為該筆
                // 內建定義的 override，否則管理介面會同時出現兩個同捷徑項目而無法存檔。
                if (claimedBuiltInIds.Add(newlyBuiltIn.Id))
                {
                    records.Add(new SqlSnippetOverride(
                        newlyBuiltIn.Id,
                        disabled: false,
                        FromLegacy(snippet, newlyBuiltIn.Id, newlyBuiltIn)));
                }

                continue;
            }

            var id = NextMigratedId(snippet.Shortcut, usedIds);
            usedIds.Add(id);
            records.Add(new SqlSnippetOverride(
                id,
                disabled: false,
                FromLegacy(snippet, id, template: null)));
        }

        return new SqlSnippetDocument(SqlSnippetLibrary.CurrentVersion, records);
    }

    public static bool AreEquivalent(SqlSnippet left, SqlSnippet right)
    {
        return string.Equals(left.Shortcut, right.Shortcut, StringComparison.Ordinal) &&
               string.Equals(left.Code, right.Code, StringComparison.Ordinal) &&
               string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
               left.TriggerFollowUp == right.TriggerFollowUp &&
               left.Category == right.Category &&
               left.IsDestructive == right.IsDestructive &&
               left.ExpansionMode == right.ExpansionMode &&
               left.Positions == right.Positions &&
               PlaceholdersEqual(left.Placeholders, right.Placeholders);
    }

    private static bool AreLegacyEquivalent(SqlSnippet left, SqlSnippet right)
    {
        return string.Equals(left.Shortcut, right.Shortcut, StringComparison.Ordinal) &&
               string.Equals(left.Code, right.Code, StringComparison.Ordinal) &&
               string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
               left.TriggerFollowUp == right.TriggerFollowUp &&
               PlaceholdersEqual(left.Placeholders, right.Placeholders);
    }

    private static bool PlaceholdersEqual(
        IReadOnlyList<SqlSnippetPlaceholder> left,
        IReadOnlyList<SqlSnippetPlaceholder> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].Id, right[index].Id, StringComparison.Ordinal) ||
                !string.Equals(left[index].DefaultValue, right[index].DefaultValue, StringComparison.Ordinal) ||
                !string.Equals(left[index].ToolTip, right[index].ToolTip, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>撞捷徑時誰進得了建議清單；使用者的資料優先。</summary>
    private static HashSet<SqlSnippetConfigurationEntry> ChooseShortcutWinners(
        IReadOnlyList<SqlSnippetConfigurationEntry> entries)
    {
        var byShortcut = new Dictionary<string, SqlSnippetConfigurationEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.IsDisabled)
            {
                continue;
            }

            // 同優先度時保留先遇到的：內建照 JSON 順序、自訂照檔案順序，
            // 兩次載入同一份檔案要得到同一個贏家。
            if (!byShortcut.TryGetValue(entry.Snippet.Shortcut, out var existing) ||
                Priority(entry) > Priority(existing))
            {
                byShortcut[entry.Snippet.Shortcut] = entry;
            }
        }

        return new HashSet<SqlSnippetConfigurationEntry>(byShortcut.Values);
    }

    private static int Priority(SqlSnippetConfigurationEntry entry) =>
        entry.IsBuiltIn && !entry.IsCustomized ? 1 : 2;

    private static SqlSnippet FromLegacy(SqlSnippet source, string id, SqlSnippet? template)
    {
        return new SqlSnippet(
            source.Shortcut,
            source.Code,
            source.Title,
            source.Description,
            source.TriggerFollowUp,
            SqlSnippetPlaceholders.Reconcile(source.Code, source.Placeholders),
            id,
            template?.Category ?? SqlSnippetCategory.Other,
            template?.IsDestructive ?? false,
            SqlSnippetExpansionMode.Caret,
            template?.Positions ?? SqlKeywordPosition.Any);
    }

    private static SqlSnippet WithId(SqlSnippet source, string id)
    {
        return new SqlSnippet(
            source.Shortcut,
            source.Code,
            source.Title,
            source.Description,
            source.TriggerFollowUp,
            SqlSnippetPlaceholders.Reconcile(source.Code, source.Placeholders),
            id,
            source.Category,
            source.IsDestructive,
            source.ExpansionMode,
            source.Positions);
    }

    private static string NextMigratedId(string shortcut, ISet<string> used)
    {
        var baseId = SqlSnippetIdentity.CreateMigratedId(shortcut);
        var candidate = baseId;
        var suffix = 2;

        while (used.Contains(candidate))
        {
            candidate = baseId + "." + suffix;
            suffix++;
        }

        return candidate;
    }

    private static string NextCustomId(ISet<string> used)
    {
        var candidate = SqlSnippetIdentity.NewCustomId();

        while (!used.Add(candidate))
        {
            candidate = SqlSnippetIdentity.NewCustomId();
        }

        return candidate;
    }
}
