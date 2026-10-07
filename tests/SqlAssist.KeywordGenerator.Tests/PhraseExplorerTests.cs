using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlAssist.KeywordGenerator.Data;
using Xunit;

namespace SqlAssist.KeywordGenerator.Tests;

/// <summary>第四階段：探測文字怎麼組、名稱與值代入哪一種寫法、片語裡的每一個字怎麼補回前面那段。</summary>
public sealed class PhraseExplorerTests : IDisposable
{
    private static readonly string[] Pool = ["ALTER", "HIGH", "LOW", "NOCOUNT", "OFF", "ON", "OR", "PROCEDURE", "SELECT", "TABLE"];

    private static readonly Dictionary<string, string[]> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StatementStart"] = ["", "SELECT 1; "],
        ["DataSource"] = ["SELECT * FROM "],
        ["SelectListTail"] = ["SELECT a "],
        ["BlockStart"] = ["BEGIN "],
    };

    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "SqlAssist.KeywordGenerator.Tests." + Guid.NewGuid() + ".cache");

    public void Dispose()
    {
        File.Delete(_cachePath);
    }

    [Theory]
    [InlineData("", "ALTER INDEX {name} ON {name}", null, null, null, "ALTER INDEX t ON t ")]
    [InlineData("", "EXEC ... WITH ,*", null, "p", null, "EXEC p WITH ")]
    [InlineData("", "RAISERROR () WITH ,*", "('x', 16, 1)", null, null, "RAISERROR ('x', 16, 1) WITH ")]
    [InlineData("SELECT ", "PERIOD FOR SYSTEM_TIME ()", null, null, null, "SELECT PERIOD FOR SYSTEM_TIME (a) ")]
    [InlineData("SELECT * FROM ", "OPENROWSET (*", null, null, null, "SELECT * FROM OPENROWSET (")]
    [InlineData("ALTER TABLE t SWITCH TO t WITH (", "WAIT_AT_LOW_PRIORITY (* ABORT_AFTER_WAIT =", null, null, "MAX_DURATION = 1 MINUTES, ",
        "ALTER TABLE t SWITCH TO t WITH (WAIT_AT_LOW_PRIORITY (MAX_DURATION = 1 MINUTES, ABORT_AFTER_WAIT = ")]
    [InlineData("CREATE SEQUENCE t ", "", null, null, null, "CREATE SEQUENCE t ")]
    [InlineData("", "BACKUP DATABASE ... TO ,* {value} MIRROR TO", null, "t FILE = 'x'", null, "BACKUP DATABASE t FILE = 'x' TO t MIRROR TO ")]
    public void 探測文字代入名稱括號與Gap(string lead, string pattern, string? group, string? gap, string? items, string expected)
    {
        Assert.Equal(expected, Create().ProbeText(lead, pattern, group, gap, items: items));
    }

    [Fact]
    public void 值的寫法取剖析器收的第一種()
    {
        var explorer = Create();

        Assert.Equal("1", explorer.SelectValue("FETCH ABSOLUTE "));
        Assert.Equal("'x'", explorer.SelectValue("CREATE LOGIN l WITH PASSWORD = "));
        Assert.Null(explorer.SelectValue("ALTER INDEX "));
        Assert.Equal("FETCH ABSOLUTE 1 FROM ", explorer.ProbeText(string.Empty, "FETCH ABSOLUTE {value} FROM"));
    }

    [Fact]
    public void 只收兩段名稱的格子代入兩段()
    {
        var explorer = Create();

        Assert.Equal("t.t", explorer.SelectName("CREATE EVENT SESSION s ON SERVER ADD EVENT ", ","));
        Assert.Equal("t", explorer.SelectName("ALTER INDEX ", "ON"));
    }

    [Fact]
    public void 等號之後代入那一格列得出的第一個字()
    {
        var explorer = Create();
        const string probe = "CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = ";

        Assert.Equal("t", explorer.SelectName(probe, null));

        explorer.Explore([new("CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM =") { Values = ["AES_128", "AES_256"], Closed = true }]);

        Assert.Equal("AES_128", explorer.SelectName(probe, null));
        Assert.Equal(probe + "AES_128 ENCRYPTION ", explorer.ProbeText(string.Empty, "CREATE DATABASE ENCRYPTION KEY WITH ALGORITHM = {name} ENCRYPTION"));
    }

    [Fact]
    public void 手寫的值補進探到的字之後()
    {
        var explorer = Create();

        explorer.Explore([new("SET DEADLOCK_PRIORITY") { Values = ["LOW", "NORMAL", "HIGH"], Closed = true }]);
        var phrase = explorer.Phrases[ProbedPhrase.Key("StatementStart", "SET DEADLOCK_PRIORITY")];

        Assert.Equal("SET DEADLOCK_PRIORITY ", phrase.Probe);
        Assert.Equal(["LOW", "NORMAL", "HIGH"], phrase.Words);
        Assert.True(phrase.Closed);
    }

    [Fact]
    public void 封閉的格子只有接得上的字()
    {
        var explorer = Create();

        explorer.Explore([new("SET NOCOUNT")]);
        var phrase = explorer.Phrases[ProbedPhrase.Key("StatementStart", "SET NOCOUNT")];

        Assert.Equal(["OFF", "ON"], phrase.Words);
        Assert.True(phrase.Closed);
        Assert.False(phrase.TakesOperand);
        Assert.False(phrase.TakesName);
    }

    /// <summary>
    /// 收名稱又一個字都沒有的格子不封閉：照「寫不寫得完」判的話憑證擁有者那一格（之後還要 FROM）被判成封閉；
    /// 執行期拿 TakesName 認既有名稱那一格。選項的字收在名稱格裡的照樣封閉。
    /// </summary>
    [Fact]
    public void 收名稱的格子記下來而且不封閉()
    {
        var explorer = Create();

        explorer.Explore([new("CREATE CERTIFICATE {name} AUTHORIZATION"), new("DROP LOGIN")]);
        explorer.OpenEmptyNameSlots();

        var owner = explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE CERTIFICATE {name} AUTHORIZATION")];
        Assert.True(owner.TakesName);
        Assert.False(owner.Closed);
        Assert.True(explorer.Phrases[ProbedPhrase.Key("StatementStart", "DROP LOGIN")].TakesName);
    }

    [Fact]
    public void 收名稱卻有字的格子照樣封閉()
    {
        var explorer = Create(pool: ["ISOLATION", "LEVEL"]);

        explorer.Explore([new("SET TRANSACTION ISOLATION")]);
        explorer.OpenEmptyNameSlots();

        var phrase = explorer.Phrases[ProbedPhrase.Key("StatementStart", "SET TRANSACTION ISOLATION")];
        Assert.Equal(["LEVEL"], phrase.Words);
        Assert.True(phrase.Closed);
    }

    /// <summary>建立的種類寫完名稱之後接得上 AUTHORIZATION 的，擁有者那一格也立起來：CREATE SCHEMA s AUTHORIZATION 之後是主體。</summary>
    [Fact]
    public void 建立種類的名稱之後立擁有者那一格()
    {
        var explorer = Create(pool: ["AUTHORIZATION", "SCHEMA", "TABLE"]);

        explorer.Explore([new("CREATE") { Expand = 1, Kinds = ObjectKinds.New }]);

        var owner = explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE SCHEMA {name} AUTHORIZATION")];
        Assert.Equal("CREATE SCHEMA t AUTHORIZATION ", owner.Probe);
        Assert.True(owner.TakesName);
        Assert.False(explorer.Phrases.Contains(ProbedPhrase.Key("StatementStart", "CREATE TABLE {name} AUTHORIZATION")));
    }

    /// <summary>清單項的等號之後也是一格：開關的值與值之後的尾巴（PASSWORD = 'x' HASHED）在第幾項都一樣。</summary>
    [Fact]
    public void 清單項的等號之後另立中段清單的片語()
    {
        var explorer = Create(pool: ["CHECK_POLICY", "HASHED", "MUST_CHANGE", "NAME", "OFF", "OLD_PASSWORD", "ON", "PASSWORD", "UNLOCK"]);

        explorer.Explore([new("ALTER LOGIN {name} WITH ,*")]);

        var policy = explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER LOGIN {name} WITH ,* CHECK_POLICY =")];
        Assert.Equal(["OFF", "ON"], policy.Words.OrderBy(word => word, StringComparer.Ordinal));
        Assert.True(policy.Closed);
        var password = explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER LOGIN {name} WITH ,* PASSWORD = {value}")];
        Assert.Equal(["HASHED", "MUST_CHANGE", "OLD_PASSWORD", "UNLOCK"], password.Words.OrderBy(word => word, StringComparer.Ordinal));
    }

    /// <summary>等號之後也收運算式的那一格（ENCRYPTION = 列得出 CASE），代入寫完一個值的字，不代入運算式的開頭。</summary>
    [Fact]
    public void 等號之後收運算式時代入寫完一個值的字()
    {
        var explorer = Create(pool: ["CASE", "COALESCE", "ENCRYPTION", "LIFETIME", "OFF", "ON"]);
        const string head = "DIALOG {name} FROM SERVICE {name} TO SERVICE {value} WITH ,*";

        explorer.Explore([new(head) { After = ["BlockStart"] }]);

        var encryption = explorer.Phrases[ProbedPhrase.Key("BlockStart", head + " ENCRYPTION =")];
        Assert.Contains("CASE", encryption.Words);
        Assert.False(explorer.Phrases.Contains(ProbedPhrase.Key("BlockStart", head + " ENCRYPTION = {name}")));
    }

    /// <summary>逗號清單一項的第一個字還沒寫完也是一格：CHANGE_TRACKING 之後是 MANUAL、AUTO、OFF，寫在第幾項都一樣。</summary>
    [Fact]
    public void 逗號清單項的第一個字之後另立中段清單的片語()
    {
        var explorer = Create(pool: ["AUTO", "CHANGE_TRACKING", "MANUAL", "OFF", "STOPLIST"], keywords: ["OFF"]);

        explorer.Explore([new("CREATE FULLTEXT INDEX ON {name} KEY INDEX {name} WITH ,*")]);

        var tracking = explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE FULLTEXT INDEX ON {name} KEY INDEX {name} WITH ,* CHANGE_TRACKING")];
        Assert.Equal(["AUTO", "MANUAL", "OFF"], tracking.Words.OrderBy(word => word, StringComparer.Ordinal));
        Assert.False(explorer.Phrases.Contains(ProbedPhrase.Key("StatementStart", "CREATE FULLTEXT INDEX ON {name} KEY INDEX {name} WITH ,* OFF")));
    }

    /// <summary>
    /// 以尾巴比對的清單（,* ,）逗號之後一樣是一項：TRUSTWORTHY 之後是 ON、OFF。
    /// 項的第一個字是關鍵字的不立：之後寫什麼由位置分析說（GROUP BY a, CASE 之後是運算式）。
    /// </summary>
    [Fact]
    public void 以尾巴比對的清單也立逗號之後那一項()
    {
        var explorer = Create(pool: ["DB_CHAINING", "OFF", "ON", "TRUSTWORTHY"], keywords: ["OFF", "ON"]);

        explorer.Explore([new("ALTER DATABASE {name} SET ,* ,")]);

        var trustworthy = explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER DATABASE {name} SET ,* TRUSTWORTHY")];
        Assert.Equal(["OFF", "ON"], trustworthy.Words.OrderBy(word => word, StringComparer.Ordinal));

        var keyword = Create(pool: ["DB_CHAINING", "OFF", "ON", "TRUSTWORTHY"], keywords: ["OFF", "ON", "TRUSTWORTHY"]);
        keyword.Explore([new("ALTER DATABASE {name} SET ,* ,")]);

        Assert.False(keyword.Phrases.Contains(ProbedPhrase.Key("StatementStart", "ALTER DATABASE {name} SET ,* TRUSTWORTHY")));
    }

    /// <summary>
    /// 逗號之後還收得了同一項的清單（事件通知的事件），每一種第一項探到的都一樣，只探一種；
    /// 用過的項不收第二次的清單（ALTER LOGIN 的 NAME）照舊每一種各探一次、取聯集。
    /// </summary>
    [Fact]
    public void 收得了重複項的清單只探一種第一項()
    {
        var events = Create(pool: ["OBJECT_ALTERED", "OBJECT_CREATED", "OBJECT_DELETED", "TO"]);

        events.Explore([new("CREATE EVENT NOTIFICATION {name} ON SERVER FOR ,* ,")]);

        var next = events.Phrases[ProbedPhrase.Key("StatementStart", "CREATE EVENT NOTIFICATION {name} ON SERVER FOR ,* ,")];
        Assert.Equal(["OBJECT_ALTERED", "OBJECT_CREATED", "OBJECT_DELETED"], next.Words.OrderBy(word => word, StringComparer.Ordinal));

        var login = Create(pool: ["CHECK_POLICY", "NAME", "OFF", "ON", "PASSWORD"]);
        login.Explore([new("ALTER LOGIN {name} WITH ,*")]);

        Assert.Contains("NAME", login.Phrases[ProbedPhrase.Key("StatementStart", "ALTER LOGIN {name} WITH ,*")].Words);
    }

    /// <summary>
    /// 宣告封閉的手寫值是那一格唯一的寫法：之後的片語探測代入它，不代入普通名稱。
    /// RESTORE SERVICE t KEY … 整句寫完才在 t 報「必須是 MASTER」，密碼的值與之後的 FORCE 就探不出來。
    /// </summary>
    [Fact]
    public void 宣告封閉的手寫值代入之後的名稱格()
    {
        var explorer = Create(pool: ["FORCE"]);

        explorer.Explore(
        [
            new("RESTORE SERVICE") { Values = ["MASTER"], Closed = true },
            new("RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}"),
        ]);

        var password = explorer.Phrases[ProbedPhrase.Key("StatementStart", "RESTORE SERVICE {name} KEY FROM FILE = {value} DECRYPTION BY PASSWORD = {value}")];
        Assert.StartsWith("RESTORE SERVICE MASTER KEY ", password.Probe, StringComparison.Ordinal);
        Assert.Equal(["FORCE"], password.Words);
    }

    /// <summary>標頭夾著 ... 的清單，項的等號之後照樣立：RESTORE … WITH STOPATMARK = 'm' 之後是 AFTER。</summary>
    [Fact]
    public void 標頭夾著其餘標頭的清單也立項的等號之後()
    {
        var explorer = Create(pool: ["AFTER", "RECOVERY", "STOPATMARK"]);

        explorer.Explore([new("RESTORE DATABASE ... WITH ,*") { Gap = "d FROM DISK = 'x'" }]);

        Assert.Contains("AFTER", explorer.Phrases[ProbedPhrase.Key("StatementStart", "RESTORE DATABASE ... WITH ,* STOPATMARK = {value}")].Words);
    }

    /// <summary>等號之後收得下一串以逗號分隔的值時，寫完的那幾個是中段的 ,*：CPU = 0, 1 之後同樣是 TO。</summary>
    [Fact]
    public void 等號之後的值清單立成中段清單()
    {
        var explorer = Create(pool: ["AUTO", "TO"]);

        explorer.Explore([new("ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU =") { Expand = 1 }]);

        Assert.Contains("TO", explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = ,* {value}")].Words);
        Assert.False(explorer.Phrases.Contains(ProbedPhrase.Key("StatementStart", "ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = {value}")));
    }

    /// <summary>括號清單一項的等號之後同樣是一格：MEMORY_PARTITION_MODE = 之後是 PER_CPU，寫在第幾項都一樣。</summary>
    [Fact]
    public void 括號清單項的等號之後另立一格()
    {
        var explorer = Create(pool: ["MEMORY_PARTITION_MODE", "NONE", "OFF", "ON", "PER_CPU", "PER_NODE", "STARTUP_STATE"]);

        explorer.Explore([new("ALTER EVENT SESSION {name} ON SERVER WITH (*")]);

        var mode = explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER EVENT SESSION {name} ON SERVER WITH (* MEMORY_PARTITION_MODE =")];
        Assert.Equal(["NONE", "PER_CPU", "PER_NODE"], mode.Words.OrderBy(word => word, StringComparer.Ordinal));
    }

    /// <summary>
    /// 括號清單的項在括號裡接逗號：關上括號的續尾接的是括號外那一份清單，探的話
    /// AUTO_CREATE_STATISTICS ON ( 的逗號之後列出整份 SET 選項。
    /// </summary>
    [Fact]
    public void 括號清單的項不接括號外的逗號()
    {
        var explorer = Create(pool: ["AUTO_CREATE_STATISTICS", "INCREMENTAL", "OFF", "ON", "READ_ONLY"]);

        explorer.Explore([new("ALTER DATABASE {name} SET ,* AUTO_CREATE_STATISTICS ON (*")]);

        Assert.Equal(["INCREMENTAL"], explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER DATABASE {name} SET ,* AUTO_CREATE_STATISTICS ON (*")].Words);
    }

    /// <summary>一項寫到這裡就開了另一份清單的字不是這份清單的項：備份對象的 TO 開的是裝置清單，逗號之後不列 DISK。</summary>
    [Fact]
    public void 清單項不含另一份清單的開頭()
    {
        var explorer = Create(pool: ["DISK", "FILE", "FILEGROUP", "TO", "URL"]);

        explorer.Explore([new("BACKUP DATABASE {name} ,*"), new("BACKUP DATABASE ... TO ,*") { Gap = "t FILE = 'x'" }]);

        var targets = explorer.Phrases[ProbedPhrase.Key("StatementStart", "BACKUP DATABASE {name} ,*")].Words;
        Assert.Contains("FILEGROUP", targets);
        Assert.DoesNotContain("DISK", targets);
        Assert.DoesNotContain("TO", targets);
    }

    /// <summary>官方有、剖析器還不收的選項手寫補進清單：只驗標頭剖析得過，字本身不驗。</summary>
    [Fact]
    public void 剖析器落後的選項補進清單的兩格()
    {
        var explorer = Create(pool: ["DEFAULT_SCHEMA", "NAME"]);

        explorer.Explore([new("ALTER USER {name} WITH ,*") { Lagging = ["ALLOW_ENCRYPTED_VALUE_MODIFICATIONS"] }]);

        Assert.Contains("ALLOW_ENCRYPTED_VALUE_MODIFICATIONS", explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER USER {name} WITH")].Words);
        Assert.Contains("ALLOW_ENCRYPTED_VALUE_MODIFICATIONS", explorer.Phrases[ProbedPhrase.Key("StatementStart", "ALTER USER {name} WITH ,*")].Words);
    }

    [Fact]
    public void 整段剖析得過的片語把每一個字補進前面那段()
    {
        var explorer = Create();
        PhraseDeclaration[] declarations = [new("CREATE"), new("CREATE OR ALTER")];

        explorer.Explore([declarations[0]]);
        explorer.AddEvidence(declarations);

        Assert.Contains("OR", explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE")].Words);
        Assert.Equal(["ALTER"], explorer.Phrases[ProbedPhrase.Key("StatementStart", "CREATE OR")].Words);
    }

    /// <summary>
    /// 附加片語只收別的來源列不出的字：剖析器當名稱讀的（VECTOR_SEARCH 與 fn( 同形）由函式目錄列，
    /// 判不出位置時別的位置已經收了的（AT）一定已經列出。
    /// </summary>
    [Fact]
    public void 附加片語不收別的來源列得出的字()
    {
        var explorer = Create();

        explorer.AddEvidence([
            new("VECTOR_SEARCH (*") { After = ["DataSource"] },
            new("AT TIME") { Lead = "SELECT a " },
            new("AT TIME") { After = ["SelectListTail"] },
            new("GENERATED ALWAYS AS ROW START HIDDEN") { Lead = "CREATE TABLE t (a datetime2 " },
        ]);

        Assert.Equal(["None:GENERATED", "SelectListTail:AT"], explorer.Additive
            .SelectMany(additive => additive.Words.Select(word => additive.After + ":" + word))
            .OrderBy(entry => entry, StringComparer.Ordinal));
    }

    /// <summary>
    /// 內建函式由函式目錄列：引數自有文法的（AI_GENERATE_EMBEDDINGS(x USE MODEL m)）換成普通名稱剖析不過，
    /// 只靠名稱讀法判的話會另立附加片語，與函式目錄重複。
    /// </summary>
    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "None:AI_GENERATE_EMBEDDINGS")]
    public void 內建函式不收進附加片語(bool listed, string expected)
    {
        var explorer = Create(listed ? ["AI_GENERATE_EMBEDDINGS"] : []);

        explorer.AddEvidence([new("AI_GENERATE_EMBEDDINGS (* {value} USE MODEL") { Lead = "SELECT " }]);

        Assert.Equal(expected, string.Join(",", explorer.Additive.SelectMany(additive => additive.Words.Select(word => additive.After + ":" + word))));
    }

    [Fact]
    public void 整段剖析不過的片語不當證據()
    {
        var explorer = Create();

        Assert.Throws<InvalidOperationException>(() => explorer.AddEvidence([new("CREATE OR NOTHING")]));
    }

    [Fact]
    public void 手寫值剖析不過就中止()
    {
        var explorer = Create();

        var exception = Assert.Throws<InvalidOperationException>(() => explorer.Explore([new("SET NOCOUNT") { Values = ["SELECT FROM"] }]));
        Assert.Contains("SELECT FROM", exception.Message);
    }

    private PhraseExplorer Create(string[]? functions = null, string[]? pool = null, string[]? keywords = null)
    {
        var prober = new KeywordProber(KeywordProberTests.Rejecting, _cachePath, loadCache: false);
        pool ??= Pool;
        return new PhraseExplorer(prober, pool, ["ALTER", "OR", "PROCEDURE", "SELECT", "TABLE", "ON", "OFF"], keywords ?? pool,
            new Dictionary<string, List<string>>(), Templates, Continuations.Phrases)
        {
            BuiltInFunctions = functions ?? [],
        };
    }
}
