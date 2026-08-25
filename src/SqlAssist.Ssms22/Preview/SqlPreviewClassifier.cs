using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using SqlAssist.Core;
using SqlAssist.Core.Parsing;

namespace SqlAssist.Ssms22.Preview;

[Export(typeof(IClassifierProvider))]
[ContentType(SqlPreviewDefinitions.ContentTypeName)]
internal sealed class SqlPreviewClassifierProvider : IClassifierProvider
{
    [Import]
    internal IClassificationTypeRegistryService Registry { get; set; } = null!;

    public IClassifier? GetClassifier(ITextBuffer textBuffer)
    {
        try
        {
            return textBuffer.Properties.GetOrCreateSingletonProperty(
                typeof(SqlPreviewClassifier),
                () => new SqlPreviewClassifier(Registry));
        }
        catch (Exception exception)
        {
            // 著色失敗只該讓預覽變成黑白，不該讓預覽開不起來。
            SqlAssistDiagnostics.WriteAlways($"建立預覽著色器失敗：{exception.Message}");
            return null;
        }
    }
}

/// <summary>
/// 結構預覽的 T-SQL 著色器。
/// </summary>
/// <remarks>
/// 用本擴充自己的詞法分析器，而不是向 SSMS 借它的 T-SQL 著色器：
/// 那個著色器綁在舊版語言服務上，只有經過轉接層建立的緩衝區才拿得到，
/// 而預覽的緩衝區是直接由 MEF 工廠建立的。自己畫也比較可預期——
/// 用的是同一份詞法分析器，預覽裡看到的分色與建議清單的判斷永遠一致。
///
/// 每一次要求都重新分析整份文字會很浪費：編輯器是一行一行問的，
/// 一份三百行的指令碼就會被分析三百次。因此以快照為鍵快取詞法串流，
/// 換了內容才重算。
/// </remarks>
internal sealed class SqlPreviewClassifier : IClassifier
{
    private sealed class TokenCache
    {
        public TokenCache(ITextSnapshot snapshot, IReadOnlyList<SqlToken> tokens)
        {
            Snapshot = snapshot;
            Tokens = tokens;
        }

        public ITextSnapshot Snapshot { get; }

        public IReadOnlyList<SqlToken> Tokens { get; }
    }

    private readonly IClassificationType _keyword;
    private readonly IClassificationType _identifier;
    private readonly IClassificationType _number;
    private readonly IClassificationType _string;
    private readonly IClassificationType _comment;
    private readonly IClassificationType _operator;

    private TokenCache? _cache;

    public SqlPreviewClassifier(IClassificationTypeRegistryService registry)
    {
        _keyword = registry.GetClassificationType(PredefinedClassificationTypeNames.Keyword);
        _identifier = registry.GetClassificationType(PredefinedClassificationTypeNames.Identifier);
        _number = registry.GetClassificationType(PredefinedClassificationTypeNames.Number);
        _string = registry.GetClassificationType(PredefinedClassificationTypeNames.String);
        _comment = registry.GetClassificationType(PredefinedClassificationTypeNames.Comment);
        _operator = registry.GetClassificationType(PredefinedClassificationTypeNames.Operator);
    }

    /// <summary>整份內容一次換掉，沒有局部失效的情況，因此永遠不會引發。</summary>
    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged
    {
        add { }
        remove { }
    }

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        var result = new List<ClassificationSpan>();

        if (span.IsEmpty)
        {
            return result;
        }

        try
        {
            foreach (var token in GetTokens(span.Snapshot))
            {
                if (token.End <= span.Start.Position)
                {
                    continue;
                }

                if (token.Start >= span.End.Position)
                {
                    break; // 詞法單元依位置遞增，越過要求的範圍就可以停了。
                }

                if (Classify(token) is not { } classification)
                {
                    continue;
                }

                result.Add(new ClassificationSpan(
                    new SnapshotSpan(span.Snapshot, token.Start, token.Length),
                    classification));
            }
        }
        catch (Exception exception)
        {
            SqlAssistDiagnostics.WriteAlways($"預覽著色失敗：{exception.Message}");
        }

        return result;
    }

    private IClassificationType? Classify(SqlToken token)
    {
        return token.Kind switch
        {
            SqlTokenKind.Comment => _comment,
            SqlTokenKind.String => _string,
            SqlTokenKind.Number => _number,
            SqlTokenKind.Operator => _operator,
            SqlTokenKind.Variable => _identifier,
            SqlTokenKind.Identifier => ClassifyIdentifier(token),
            _ => null
        };
    }

    /// <remarks>
    /// 加了方括號的名稱一律不是關鍵字：<c>[KEY]</c> 是欄位名，不是 <c>KEY</c>。
    /// </remarks>
    private IClassificationType? ClassifyIdentifier(SqlToken token)
    {
        if (token.IsQuoted)
        {
            return _identifier;
        }

        return SqlKeywordCatalog.IsKeywordOrDataType(token.Value) ? _keyword : _identifier;
    }

    private IReadOnlyList<SqlToken> GetTokens(ITextSnapshot snapshot)
    {
        var cache = Volatile.Read(ref _cache);

        if (cache is not null && ReferenceEquals(cache.Snapshot, snapshot))
        {
            return cache.Tokens;
        }

        var tokens = SqlTokenizer.TokenizeWithComments(snapshot.GetText());
        Volatile.Write(ref _cache, new TokenCache(snapshot, tokens));
        return tokens;
    }
}
