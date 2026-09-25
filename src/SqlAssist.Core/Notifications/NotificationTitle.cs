using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using SqlAssist.Core.Localization;

namespace SqlAssist.Core.Notifications;

/// <summary>一列通知的標題：記得是目錄裡的哪一句，每次取值都用目前的語言。</summary>
/// <remarks>
/// 呼叫端交進來的是字串（目錄的屬性），快照只存字串的話，換語言後已經顯示的通知停在舊語言，
/// 成功後的過去式也無從得知：中文加「已」就好，英文要換一個字。建立時用字串反查回目錄那一句，
/// 只花一次字典查找、不配置；查不到的（測試的字面值）照原字串顯示。
///
/// 反查而不是把呼叫端改成傳這個型別：標題經過 Metadata、SQL Search、SQL Memory 十幾處
/// <c>string title</c> 的轉手參數，全部改型別等於動遍其他功能區。
/// </remarks>
internal sealed class NotificationTitle
{
    /// <summary>目錄裡標題 <c>X</c> 的過去式是 <c>XDone</c>；有這一對的屬性才是標題。</summary>
    private const string DoneSuffix = "Done";

    private static readonly Lazy<Dictionary<string, NotificationTitle>> s_catalog = new(Build);

    private readonly Func<string> _text;
    private readonly Func<string>? _done;

    private NotificationTitle(string key, Func<string> text, Func<string>? done)
    {
        Key = key; _text = text; _done = done;
    }

    /// <summary>合併與統計用的鍵：目錄的標題是來源語言那一句，換語言也不變。</summary>
    public string Key { get; }

    public string Text => _text();

    /// <summary>成功後那一列的措辭；不在目錄裡的標題套通用句型。</summary>
    public string Done => _done is null ? NotificationCatalog.CompletedFallback(Text) : _done();

    public static NotificationTitle Resolve(string title)
    {
        title ??= "";
        return s_catalog.Value.TryGetValue(title, out var known) ? known : new NotificationTitle(title, () => title, null);
    }

    /// <summary>提醒這類由目錄的方法建立、不經過反查的標題。</summary>
    public static NotificationTitle Live(Func<string> text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        using (SqlText.Use(SqlLanguage.Source)) return new NotificationTitle(text(), text, null);
    }

    [Localizable(false)]
    private static Dictionary<string, NotificationTitle> Build()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var type = typeof(NotificationCatalog);
        var map = new Dictionary<string, NotificationTitle>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(flags))
        {
            if (property.PropertyType != typeof(string)
                || type.GetProperty(property.Name + DoneSuffix, flags) is not { } done)
                continue;

            var text = Getter(property);
            var title = Live(text);
            title = new NotificationTitle(title.Key, text, Getter(done));
            foreach (var language in SqlLanguage.All)
            {
                using (SqlText.Use(language))
                {
                    var value = text();
                    // 兩個標題在某個語言譯成同一句，反查就分不出是哪一件事。
                    if (map.TryGetValue(value, out var other) && !ReferenceEquals(other, title))
                        throw new InvalidOperationException($"通知標題 {property.Name} 與另一個標題在 {language} 同為「{value}」。");
                    map[value] = title;
                }
            }
        }

        return map;
    }

    private static Func<string> Getter(PropertyInfo property) =>
        (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), property.GetGetMethod());
}
