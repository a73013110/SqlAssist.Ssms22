using System;
using System.Collections.Generic;

namespace SqlAssist.Core.QueryMemory;

/// <summary>清單只接受目前篩選世代的回應；取消無法撤回已派送的隔離呼叫。</summary>
public sealed class QueryMemoryBrowserState<T>
{
    private readonly List<T> _items = new();
    public IReadOnlyList<T> Items => _items.AsReadOnly();
    public long Generation { get; private set; }
    public string? Cursor { get; private set; }
    public bool Loading { get; private set; }

    public long Reset()
    {
        Generation++;
        _items.Clear();
        Cursor = null;
        Loading = false;
        return Generation;
    }

    public bool Begin(long generation)
    {
        if (generation != Generation || Loading) return false;
        Loading = true;
        return true;
    }

    public bool Accept(long generation, IEnumerable<T> items, string? cursor)
    {
        if (generation != Generation || !Loading) return false;
        _items.AddRange(items);
        Cursor = cursor;
        Loading = false;
        return true;
    }

    public void Fail(long generation)
    {
        if (generation == Generation) Loading = false;
    }
}
