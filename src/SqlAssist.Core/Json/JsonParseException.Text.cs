namespace SqlAssist.Core.Json;

/// <summary>剖析錯誤的譯文；只在 Core 編譯，文字產生器那一份沒有這個檔案。</summary>
public sealed partial class JsonParseException
{
    static partial void Localize(JsonParseError error, int position, string? detail, ref string message)
    {
        var reason = error switch
        {
            JsonParseError.TrailingContent => JsonText.TrailingContent,
            JsonParseError.UnexpectedEnd => JsonText.UnexpectedEnd,
            JsonParseError.ObjectNotClosed => JsonText.ObjectNotClosed,
            JsonParseError.MemberNameNotString => JsonText.MemberNameNotString,
            JsonParseError.ColonExpected => JsonText.ColonExpected,
            JsonParseError.MemberSeparatorExpected => JsonText.MemberSeparatorExpected,
            JsonParseError.ArrayNotClosed => JsonText.ArrayNotClosed,
            JsonParseError.ElementSeparatorExpected => JsonText.ElementSeparatorExpected,
            JsonParseError.StringNotClosed => JsonText.StringNotClosed,
            JsonParseError.EscapeIncomplete => JsonText.EscapeIncomplete,
            JsonParseError.UnicodeEscapeInvalid => JsonText.UnicodeEscapeInvalid,
            JsonParseError.UnknownEscape => JsonText.UnknownEscape(detail ?? string.Empty),
            JsonParseError.UnknownValue => detail is null ? JsonText.UnknownValue : JsonText.UnknownLiteral(detail),
            JsonParseError.CommentNotClosed => JsonText.CommentNotClosed,
            _ => null,
        };

        if (reason is not null)
        {
            message = JsonText.ErrorAt(reason, position);
        }
    }
}
