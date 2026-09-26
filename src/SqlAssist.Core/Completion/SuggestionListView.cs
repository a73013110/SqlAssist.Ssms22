using System;
using System.Collections;
using System.Collections.Generic;
using SqlAssist.Core.Matching;

namespace SqlAssist.Core.Completion;

/// <summary>
/// <see cref="SuggestionList.Update"/> 的結果：清單上看得到的項目與分類鈕的狀態。
/// </summary>
public sealed class SuggestionListView<TItem>
{
    internal SuggestionListView(
        IReadOnlyList<TItem> source,
        List<SuggestionList.Candidate> shown,
        SuggestionCategorySet matched,
        SuggestionCategorySet applied)
    {
        Items = new EntryList(source, shown);
        Matched = matched;
        Applied = applied;
    }

    /// <summary>依顯示順序排好的可見項目；有前綴時最多 <see cref="SuggestionList.MaximumItems"/> 筆。</summary>
    /// <remarks>逐項讀取時才組出來，不另配置一份：呼叫端本來就要把它換成平台自己的項目。</remarks>
    public IReadOnlyList<SuggestionListEntry<TItem>> Items { get; }

    /// <summary>這一輪有命中的分類；沒有命中的分類鈕要變灰。</summary>
    public SuggestionCategorySet Matched { get; }

    /// <summary>這一輪實際套用的分類；空集合代表全部。</summary>
    public SuggestionCategorySet Applied { get; }

    /// <summary>
    /// 一項都沒有；呼叫端應關閉清單。
    /// </summary>
    /// <remarks>
    /// 平台對空清單的處理是顯示「無建議」或退回上一份舊清單，兩種都是錯的畫面。
    /// </remarks>
    public bool IsEmpty => Items.Count == 0;

    private sealed class EntryList : IReadOnlyList<SuggestionListEntry<TItem>>
    {
        private readonly IReadOnlyList<TItem> _source;
        private readonly List<SuggestionList.Candidate> _shown;

        public EntryList(IReadOnlyList<TItem> source, List<SuggestionList.Candidate> shown)
        {
            _source = source;
            _shown = shown;
        }

        public int Count => _shown.Count;

        public SuggestionListEntry<TItem> this[int index]
        {
            get
            {
                var candidate = _shown[index];
                return new SuggestionListEntry<TItem>(_source[candidate.Order], candidate.Score, candidate.Spans);
            }
        }

        public IEnumerator<SuggestionListEntry<TItem>> GetEnumerator()
        {
            for (var index = 0; index < _shown.Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>清單上的一項。</summary>
public readonly struct SuggestionListEntry<TItem>
{
    internal SuggestionListEntry(TItem item, int score, IReadOnlyList<MatchSpan> spans)
    {
        Item = item;
        Score = score;
        Spans = spans ?? Array.Empty<MatchSpan>();
    }

    public TItem Item { get; }

    /// <summary>排名分數，越大越前面；跨輸入之間不具可比性。沒有前綴時為 0，順序沿用開場排序。</summary>
    public int Score { get; }

    /// <summary>顯示文字中被命中的字元區段，供高亮；沒有前綴時為空。</summary>
    public IReadOnlyList<MatchSpan> Spans { get; }
}
