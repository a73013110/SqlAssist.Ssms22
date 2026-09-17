using System;
using System.Collections.Generic;
using System.Threading;

namespace SqlAssist.Core.SqlMemory;

public enum SqlTextDiffLineKind { Unchanged, Removed, Added }

/// <summary>為什麼沒有做逐行比對；<see cref="None"/> 以外的結果是「中段整段取代」。</summary>
public enum SqlTextDiffFallback
{
    None,

    /// <summary>任一側的字元或（去掉共同頭尾後的）行數超過上限，沒有嘗試比對。</summary>
    TooLarge,

    /// <summary>比對到編輯距離上限仍未收斂；差異多到逐行標示也讀不出重點。</summary>
    TooManyChanges,
}

/// <param name="OldNumber">舊文字的行號（從 1 起）；新增的行為 null。</param>
/// <param name="NewNumber">新文字的行號（從 1 起）；刪除的行為 null。</param>
/// <param name="Text">不含換行字元的行內容。</param>
public sealed record SqlTextDiffLine(SqlTextDiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>比對的上限；超過就降級，不讓時間與記憶體隨輸入無界成長。</summary>
public sealed class SqlTextDiffLimits
{
    public SqlTextDiffLimits(int maxCharacters = 1_000_000, int maxLines = 20_000, int maxEditDistance = 1_000)
    {
        if (maxCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (maxLines < 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
        if (maxEditDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxEditDistance));
        MaxCharacters = maxCharacters;
        MaxLines = maxLines;
        MaxEditDistance = maxEditDistance;
    }

    public static SqlTextDiffLimits Default { get; } = new();

    /// <summary>每一側的字元上限。</summary>
    public int MaxCharacters { get; }

    /// <summary>去掉共同頭尾之後，每一側要比對的行數上限。</summary>
    public int MaxLines { get; }

    /// <summary>
    /// 逐行比對的編輯距離上限；回溯紀錄約佔 D² 個整數，1000 約 4 MB。
    /// </summary>
    public int MaxEditDistance { get; }
}

public sealed class SqlTextDiffResult
{
    internal SqlTextDiffResult(IReadOnlyList<SqlTextDiffLine> lines, SqlTextDiffFallback fallback, bool textsEqual)
    {
        Lines = lines;
        Fallback = fallback;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == SqlTextDiffLineKind.Unchanged) continue;
            if (FirstChange < 0) FirstChange = i;
            if (lines[i].Kind == SqlTextDiffLineKind.Added) Added++;
            else Removed++;
        }
        TextsEqual = textsEqual;
    }

    public IReadOnlyList<SqlTextDiffLine> Lines { get; }
    public SqlTextDiffFallback Fallback { get; }
    public int Added { get; }
    public int Removed { get; }

    /// <summary>第一個變更行在 <see cref="Lines"/> 的索引；沒有變更為 -1。</summary>
    public int FirstChange { get; } = -1;

    /// <summary>兩份原文逐字相同。</summary>
    public bool TextsEqual { get; }

    /// <summary>逐行內容相同、原文卻不同：只差在換行字元種類或結尾換行。</summary>
    public bool OnlyLineEndingsDiffer => !TextsEqual && Added == 0 && Removed == 0;
}

/// <summary>
/// 行級差異：去掉共同頭尾後，以 Myers O(ND) 求最短編輯腳本；超過上限退回中段整段取代。
/// </summary>
/// <remarks>
/// 純邏輯、不依賴 UI，版本歷史、History 比較或片段比對都用這一份。逐行比較是序數比較，
/// 不忽略空白或大小寫——收藏的內容位址本來就逐字精確，比對看不出的差異不能說成「相同」。
/// 換行字元 CR、LF、CRLF 一律視為行界，行內容不含它們；只差在換行的兩份文字由
/// <see cref="SqlTextDiffResult.OnlyLineEndingsDiffer"/> 說明。
/// 可能在大型輸入上跑數十毫秒，呼叫端應放在背景並傳入取消。
/// </remarks>
public static class SqlTextDiff
{
    public static SqlTextDiffResult Compute(string oldText, string newText, CancellationToken cancellationToken = default,
        SqlTextDiffLimits? limits = null)
    {
        if (oldText == null) throw new ArgumentNullException(nameof(oldText));
        if (newText == null) throw new ArgumentNullException(nameof(newText));
        limits ??= SqlTextDiffLimits.Default;
        var textsEqual = string.Equals(oldText, newText, StringComparison.Ordinal);
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        cancellationToken.ThrowIfCancellationRequested();

        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count &&
            string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal)) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
            string.Equals(oldLines[oldLines.Count - 1 - suffix], newLines[newLines.Count - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var oldMiddle = oldLines.Count - prefix - suffix;
        var newMiddle = newLines.Count - prefix - suffix;
        var output = new List<SqlTextDiffLine>(Math.Max(oldLines.Count, newLines.Count) + Math.Min(oldMiddle, newMiddle));
        for (var i = 0; i < prefix; i++) output.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Unchanged, i + 1, i + 1, oldLines[i]));

        var fallback = oldText.Length > limits.MaxCharacters || newText.Length > limits.MaxCharacters ||
            oldMiddle > limits.MaxLines || newMiddle > limits.MaxLines
            ? SqlTextDiffFallback.TooLarge : SqlTextDiffFallback.None;
        if (fallback == SqlTextDiffFallback.None && (oldMiddle > 0 || newMiddle > 0) &&
            !TryMyers(oldLines, newLines, prefix, oldMiddle, newMiddle, limits.MaxEditDistance, output, cancellationToken))
            fallback = SqlTextDiffFallback.TooManyChanges;
        if (fallback != SqlTextDiffFallback.None)
        {
            // 整段取代：先列出舊的中段，再列出新的中段；共同頭尾仍是精確結果。
            for (var i = 0; i < oldMiddle; i++)
                output.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Removed, prefix + i + 1, null, oldLines[prefix + i]));
            for (var i = 0; i < newMiddle; i++)
                output.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Added, null, prefix + i + 1, newLines[prefix + i]));
        }

        for (var i = 0; i < suffix; i++)
        {
            var oldIndex = oldLines.Count - suffix + i;
            output.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Unchanged, oldIndex + 1, newLines.Count - suffix + i + 1, oldLines[oldIndex]));
        }
        return new SqlTextDiffResult(output.AsReadOnly(), fallback, textsEqual);
    }

    /// <summary>CR、LF、CRLF 都是行界；結尾的換行不另外產生一個空行，空字串是零行。</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>對中段做 Myers 前向搜尋並回溯；超過編輯距離上限回 false，不寫入任何列。</summary>
    private static bool TryMyers(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int prefix,
        int n, int m, int maxEditDistance, List<SqlTextDiffLine> output, CancellationToken cancellationToken)
    {
        // 行先換成整數代號，最內層迴圈只比整數，不重複比較長字串。
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        int Code(string line)
        {
            if (!codes.TryGetValue(line, out var code)) codes.Add(line, code = codes.Count);
            return code;
        }
        var a = new int[n];
        var b = new int[m];
        for (var i = 0; i < n; i++) a[i] = Code(oldLines[prefix + i]);
        for (var i = 0; i < m; i++) b[i] = Code(newLines[prefix + i]);

        var bound = Math.Min(n + m, maxEditDistance);
        var offset = bound + 1;
        var v = new int[2 * bound + 3];
        // trace[d] 是第 d 輪開始前、k∈[-d, d] 的最遠 x；回溯只讀這個範圍。
        var trace = new List<int[]>();
        var distance = -1;
        for (var d = 0; d <= bound && distance < 0; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, snapshot.Length);
            trace.Add(snapshot);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { distance = d; break; }
            }
        }
        if (distance < 0) return false;

        var reversed = new List<SqlTextDiffLine>(n + m);
        var cx = n;
        var cy = m;
        void Same() { cx--; cy--; reversed.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Unchanged, prefix + cx + 1, prefix + cy + 1, oldLines[prefix + cx])); }
        for (var d = distance; d > 0; d--)
        {
            var previous = trace[d];
            int V(int diagonal) => previous[diagonal + d];
            var k = cx - cy;
            var previousK = k == -d || (k != d && V(k - 1) < V(k + 1)) ? k + 1 : k - 1;
            var previousX = V(previousK);
            var previousY = previousX - previousK;
            while (cx > previousX && cy > previousY) Same();
            if (cx == previousX)
            {
                cy--;
                reversed.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Added, null, prefix + cy + 1, newLines[prefix + cy]));
            }
            else
            {
                cx--;
                reversed.Add(new SqlTextDiffLine(SqlTextDiffLineKind.Removed, prefix + cx + 1, null, oldLines[prefix + cx]));
            }
        }
        while (cx > 0 && cy > 0) Same();
        for (var i = reversed.Count - 1; i >= 0; i--) output.Add(reversed[i]);
        return true;
    }
}
