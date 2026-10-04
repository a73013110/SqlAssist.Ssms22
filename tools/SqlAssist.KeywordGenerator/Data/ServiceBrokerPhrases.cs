using System.Linq;

namespace SqlAssist.KeywordGenerator.Data;

/// <summary>Service Broker：對話、服務與訊息類型的標頭、端點的傳輸選項。探測順序見 <see cref="ClausePhrases.All"/>。</summary>
internal static class ServiceBrokerPhrases
{
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
