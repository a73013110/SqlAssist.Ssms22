using System;
using System.Linq;

namespace SqlAssist.Core.Localization;

/// <summary>依語言各留一份的延遲值：含譯文的目錄與資源用它快取。</summary>
/// <remarks>
/// 單一個 <see cref="Lazy{T}"/> 會把第一次取值當下的語言永久凍住，換語言之後清單與提示還是舊的；
/// 在 <see cref="SqlText.Changed"/> 時清掉則要每個快取各接一次事件。以語言為鍵各留一份，
/// 切換後下一次取值自然換成新語言，切回來也不必重建。語言只有兩三種，多留幾份的成本可以忽略。
/// 工廠不應丟例外：<see cref="Lazy{T}"/> 會把例外永久快取起來反覆重丟。
/// </remarks>
public sealed class SqlLanguageCache<T>
{
    private readonly Lazy<T>[] _values;

    public SqlLanguageCache(Func<SqlLanguage, T> factory)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        // 工廠在那個語言的範圍裡執行：裡面取的產生文字（DataTypeText.Int）與參數給的語言一致，
        // 即使是從 For(其他語言) 進來的。
        _values = SqlLanguage.All
            .Select(language => new Lazy<T>(() =>
            {
                using (SqlText.Use(language))
                {
                    return factory(language);
                }
            }))
            .ToArray();
    }

    /// <summary><see cref="SqlText.Current"/> 那一份。</summary>
    public T Current => For(SqlText.Current);

    public T For(SqlLanguage language)
    {
        if (language is null)
        {
            throw new ArgumentNullException(nameof(language));
        }

        return _values[language.Index].Value;
    }
}
