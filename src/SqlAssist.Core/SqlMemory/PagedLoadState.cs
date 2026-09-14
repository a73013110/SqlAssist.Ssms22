namespace SqlAssist.Core.SqlMemory;

/// <summary>清單只接受目前篩選世代的回應；取消無法撤回已派送的隔離呼叫。</summary>
public sealed class PagedLoadState
{
    public long Generation { get; private set; }
    public string? Cursor { get; private set; }
    public bool Loading { get; private set; }

    public long Reset()
    {
        Generation++;
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

    public bool Accept(long generation, string? cursor)
    {
        if (generation != Generation || !Loading) return false;
        Cursor = cursor;
        Loading = false;
        return true;
    }

    public void Fail(long generation)
    {
        if (generation == Generation) Loading = false;
    }
}
