using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>Service Broker：對話、服務與訊息類型的標頭、端點的傳輸選項。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class ServiceBrokerPhrases
{
    // 語句開頭與 WAITFOR ( 之後都是這一句。
    internal const string GetConversationGroup = "GET CONVERSATION GROUP {name} FROM";

    // 宣告在用到它的片語之前：靜態欄位照書寫順序初始化。
    private static readonly string[] EndpointPayloads = ["SERVICE_BROKER", "DATABASE_MIRRORING"];

    private static readonly string[] EndpointAlgorithms =
        [.. from level in new[] { "SUPPORTED", "REQUIRED" } from algorithm in new[] { "AES", "RC4" } select $" ENCRYPTION = {level} ALGORITHM {algorithm}"];

    internal static readonly PhraseDeclaration[] All =
    [
        // BEGIN DIALOG [CONVERSATION] @h FROM SERVICE s TO SERVICE 'x' ON CONTRACT c WITH …：FROM、TO 之後的 SERVICE 剖析器要讀完
        // 名稱才驗，逐字探不出來，寫到目標服務（含逗號之後的服務代理執行個體）的整段是證據；之後的 ON CONTRACT 由展開探，
        // 寫完一句扣掉的 WITH 手寫補回。
        // FROM SERVICE 那一格證據不立（以名稱結尾、沒有字），另外宣告，目錄物件名冊才認得出那一格要服務的名稱。
        // BEGIN 之後是區塊開頭的位置：從 DIALOG 寫起，否則證據把 BEGIN 立成封閉片語，藏掉 ATOMIC、CATCH 與片段。
        .. from head in new[] { "DIALOG {name}", "DIALOG CONVERSATION {name}" }
           from declaration in new PhraseDeclaration[]
           {
               new(head + " FROM SERVICE") { After = ["BlockStart"] },
               new(head + " FROM SERVICE {name} TO SERVICE {value}") { After = ["BlockStart"], Expand = 4, Values = ["WITH"] },
               new(head + " FROM SERVICE {name} TO SERVICE {value} , {value}") { After = ["BlockStart"], Expand = 4, Values = ["WITH"] },
           }
           select declaration,

        // 對話代碼那一格只收變數：以名稱結尾的格子證據不立，另外宣告；否則 CONVERSATION 被讀成 DIALOG {name} 的名稱，只列 FROM。
        new("DIALOG CONVERSATION") { After = ["BlockStart"] },

        // 對話的 SEND 與 RECEIVE 不是保留字：從語句開頭宣告，兩個字才成為語句開頭（StartsStatementAsPhrase），
        // 前一句寫完換行之後列得出、範圍分析也切得開。RECEIVE 之後是選取清單，不展開；單寫 RECEIVE 是批次開頭省略 EXEC
        // 的程序呼叫（名稱讀法），寫到 FROM 才是它的證據。
        new("SEND ON CONVERSATION"),
        new("RECEIVE") { Closed = false },
        new("RECEIVE ... FROM") { Gap = "*" },

        // 對話代碼之後：SEND 一次可以送給一組括號裡的幾個代碼。MOVE 與 GET 也不是保留字，同樣從語句開頭宣告；
        // GET CONVERSATION GROUP 與 WAITFOR ( 裡的那一句共用同一條尾巴。
        new("SEND ON CONVERSATION {value}") { Expand = 3 },
        new("SEND ON CONVERSATION ()") { Expand = 3 },
        new("MOVE CONVERSATION {value}"),
        new("MOVE CONVERSATION {value} TO"),
        new(GetConversationGroup),

        // 優先順序名稱之後 FOR CONVERSATION SET (…)：種類展開只列一層（FOR），括號裡是選項清單。
        .. from verb in new[] { "CREATE", "ALTER" }
           from tail in new[] { " FOR", " FOR CONVERSATION SET (*" }
           select new PhraseDeclaration($"{verb} BROKER PRIORITY {{name}}{tail}") { Expand = 2 },

        // 佇列名稱之後的 WITH：寫完名稱已是完整的一句，WITH 同時是 CTE 的開頭被扣掉，由清單片語補回。
        // ACTIVATION、POISON_MESSAGE_HANDLING 是清單裡自帶括號清單的項，探測用 OptionItem 的佇列樣板。
        new("CREATE QUEUE {name} WITH ,*"),
        new("ALTER QUEUE {name} WITH ,*"),
        .. from item in new[] { "ACTIVATION (*", "POISON_MESSAGE_HANDLING (*" }
           select new PhraseDeclaration(item) { After = ["OptionItem"], Template = 16, Expand = 2 },

        // 合約的訊息類型清單：每一項都是「訊息類型 SENT BY 哪一端」。
        new("CREATE CONTRACT {name} (* {name}") { Expand = 3 },
        new("CREATE CONTRACT {name} AUTHORIZATION {name} (* {name}") { Expand = 3 },

        // 服務、訊息類型與遠端服務繫結的名稱之後：種類展開只列一層（ON、VALIDATION、TO），再往下的由這幾條展開。
        new("CREATE SERVICE {name} ON") { Expand = 3 },
        new("CREATE SERVICE {name} AUTHORIZATION {name}") { Expand = 4 },
        new("CREATE MESSAGE TYPE {name} VALIDATION =") { Expand = 2 },
        new("CREATE MESSAGE TYPE {name} AUTHORIZATION {name} VALIDATION =") { Expand = 2 },
        new("CREATE REMOTE SERVICE BINDING {name} TO") { Expand = 3 },
        new("CREATE REMOTE SERVICE BINDING {name} AUTHORIZATION {name}") { Expand = 4 },

        // 端點只做 TCP 的兩種承載：FOR 之後那組括號是選項清單。端點名稱與 FOR 之間夾著 STATE、AS TCP (…)，以 ... 跨過；
        // 等號之後的值逐條寫（... 不收 Expand）。ALGORITHM 之後剖析器要讀完演算法才驗，寫到演算法的整段是證據
        // （DISABLED 之後沒有演算法）。HTTP／SOAP 端點已移除，不做。
        // 埠號要在剖析器驗的範圍內（1024 到 32767），否則每一句都在埠號報錯、什麼字都過得了。
        .. from verb in new[] { "CREATE", "ALTER" }
           from payload in EndpointPayloads
           from tail in new[] { "", " AUTHENTICATION =", " ENCRYPTION =" }.Concat(EndpointAlgorithms)
           select new PhraseDeclaration($"{verb} ENDPOINT ... FOR {payload} (*{tail}") { Gap = "e AS TCP (LISTENER_PORT = 4022)" },
    ];
}
