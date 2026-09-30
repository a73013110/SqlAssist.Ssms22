namespace SqlAssist.CompletionAudit;

/// <summary>把名稱換成代號；報告預設只放代號，原名只留在不給 AI 讀的原始紀錄裡。</summary>
public interface IAuditMask
{
    /// <summary><paramref name="name"/> 的代號；同一個名稱每一次都換成同一個。</summary>
    /// <param name="tokenClass">名稱的種類；不認得時為 null。代號以種類開頭，讀報告時分得出是資料表還是欄位。</param>
    string Mask(string name, AuditTokenClass? tokenClass);
}

/// <summary>現成的 <see cref="IAuditMask"/>。</summary>
public static class AuditMask
{
    /// <summary>不換，原名照列（<c>-RawNames</c>）。</summary>
    public static IAuditMask None { get; } = new Unmasked();

    private sealed class Unmasked : IAuditMask
    {
        public string Mask(string name, AuditTokenClass? tokenClass) => name;
    }
}
