namespace SqlAssist.Core.Search;

/// <summary>
/// 「這一輪讀不到」是哪一種讀不到。
/// </summary>
/// <remarks>
/// 只有兩個值，而且刻意不細分。呈現那一層要的答案只有一個：下一步是「重試一次或換條件」
/// 還是「去要權限」。把連不上、逾時、資料庫離線各給一個值的話，每多一個值就要在狀態表面
/// 多一條分支，而它們的下一步完全一樣。
///
/// <see cref="Unknown"/> 是預設值，也是任何一點不確定時的答案：斷言權限而斷錯的那一次，
/// 會叫使用者去查一個好好的權限設定，而他怎麼查都查不出問題。斷言不足只是少說一句話。
/// </remarks>
public enum SearchUnavailableKind
{
    /// <summary>說不出是哪一種：連不上、逾時、資料庫離線，或伺服器沒說錯誤碼。</summary>
    Unknown = 0,

    /// <summary>就是權限：讀得到伺服器，但這個登入看不到這一份。</summary>
    Denied = 1,
}
