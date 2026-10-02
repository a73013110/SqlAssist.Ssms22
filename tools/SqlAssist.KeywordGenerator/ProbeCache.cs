using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SqlAssist.KeywordGenerator;

/// <summary>
/// 剖析事實的快取：只記剖析器說了什麼，怎麼解讀每次重算，所以改片語、續尾或判定規則都用得上舊的結果。
/// 鍵（ScriptDom 版本、剖析器、拒收錯誤碼、<see cref="ProbeFacts.SourceHash"/>）一變就整份不用。
/// </summary>
internal sealed class ProbeCache
{
    private const string Format = "SqlAssistProbeCache/1";

    // 中斷（Ctrl+C）攔不到，只能定期存：最多白算這麼久。
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(2);

    private readonly string _key;
    private readonly Stopwatch _sinceSave = Stopwatch.StartNew();
    private long _savedMisses;

    public ProbeCache(string key, string path)
    {
        _key = key;
        Path = path;
    }

    public string Path { get; }

    public Memo<byte[]> Classes { get; } = new Memo<byte[]>();

    public Memo<bool> Complete { get; } = new Memo<bool>();

    public Memo<int> Rejection { get; } = new Memo<int>();

    public Memo<long> Accepted { get; } = new Memo<long>();

    public long Hits => (long)Classes.Hits + Complete.Hits + Rejection.Hits + Accepted.Hits;

    public long Misses => (long)Classes.Misses + Complete.Misses + Rejection.Misses + Accepted.Misses;

    /// <summary>距上次存超過檢查點間隔，而且之後有新算的結果。</summary>
    public bool CheckpointDue => _sinceSave.Elapsed >= CheckpointInterval && Misses != _savedMisses;

    public void Load()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        try
        {
            using var file = File.OpenRead(Path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(zip, Encoding.UTF8);

            if (reader.ReadString() != Format || reader.ReadString() != _key)
            {
                return;
            }

            Classes.Read(reader, r => r.ReadBytes(r.ReadInt32()));
            Complete.Read(reader, r => r.ReadBoolean());
            Rejection.Read(reader, r => r.ReadInt32());
            Accepted.Read(reader, r => r.ReadInt64());
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
        {
            // 讀到一半壞掉的快取整份不用，已讀進來的也不採信。
            Classes.ForgetLoaded();
            Complete.ForgetLoaded();
            Rejection.ForgetLoaded();
            Accepted.ForgetLoaded();
        }
    }

    /// <summary>寫回快取。prune：只存這一次用到的項目，樣板或片語改掉之後舊文字的結果不會一直留著；
    /// 只有跑完全程才修剪，檢查點與中途失敗時連同還沒用到的舊項目一起存。</summary>
    public void Save(bool prune)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)));
        var temporary = Path + ".tmp";

        using (var file = File.Create(temporary))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(zip, Encoding.UTF8))
        {
            writer.Write(Format);
            writer.Write(_key);
            Classes.Write(writer, prune, (w, value) => { w.Write(value.Length); w.Write(value); });
            Complete.Write(writer, prune, (w, value) => w.Write(value));
            Rejection.Write(writer, prune, (w, value) => w.Write(value));
            Accepted.Write(writer, prune, (w, value) => w.Write(value));
        }

        if (File.Exists(Path))
        {
            File.Replace(temporary, Path, null);
        }
        else
        {
            File.Move(temporary, Path);
        }

        _savedMisses = Misses;
        _sinceSave.Restart();
    }

    public void Delete()
    {
        File.Delete(Path);
    }
}
