namespace SqlAssist.Ssms22.UI;

/// <summary>卡片認得的狀態；與通知來源的結果分開，等待中與降級不一定各有一個圖示。</summary>
internal enum NotificationVisualStatus { Pending, Running, Completed, Failed, Canceled }

/// <summary>通知卡片要畫的一列。</summary>
/// <remarks>
/// 卡片只認得這個型別，不認得 <c>Core/Notifications</c>：措辭、可見度、合併與期限都在
/// 呈現端（<c>Editor/NotificationHost</c>）決定完才交過來，卡片只負責版面、狀態圖示與動畫。
///
/// 這樣第二個通知來源（例如設定或片段的一次性回饋）只要能產生同一個記錄就接得上，
/// 不必先有一套通用的通知框架——只有一個來源時抽出來的抽象會照著那個來源長。
/// </remarks>
/// <param name="Id">列的身分；同一個 Id 會沿用同一列，狀態改變不重建也不重排。</param>
/// <param name="Title">主要那一行的措辭，已經是完成後的過去式與耗時。</param>
/// <param name="Subject">這件事作用在哪個物件；接在標題同一行。</param>
/// <param name="Context">從哪個文件或資料庫發起；全部相同時只在抬頭下顯示一次。</param>
/// <param name="Message">工作自己回報或由結果決定的那一行；空字串代表不預留列。</param>
/// <param name="StatusText">狀態列與輔助技術唸出來的那一句。</param>
/// <param name="Repeat">這一列代表幾次呼叫；大於 1 才顯示 ×N 徽章。</param>
internal sealed record NotificationCardItem(
    long Id,
    string Title,
    string Subject,
    string Context,
    string Message,
    NotificationVisualStatus Status,
    string StatusText,
    int Repeat);
