using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Settings;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Completion;

/// <summary>
/// 既有名稱那一格（登入、使用者、結構描述、憑證…）：位置由「前面是哪一種」推出，名單由中繼資料給。
/// </summary>
/// <remarks>
/// 少了這一份，<c>ALTER LOGIN </c>、<c>DROP USER </c> 落在 <see cref="CompletionTarget.Any"/>：
/// 整個資料庫的資料表、函式與片段一起進場，偏偏沒有那一種名稱。
/// 種類只認產生器探到的 CreatedKinds，不逐句手寫。
/// </remarks>
public sealed class SqlCatalogEntityCompletionTests
{
    private static readonly IReadOnlyList<SqlSuggestion> BuiltIn = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current);

    [Theory]
    // 片語收名稱的那一格：ALTER／DROP 種類、FOR LOGIN、OPEN SYMMETRIC KEY。
    [InlineData("ALTER LOGIN ", "LOGIN")]
    [InlineData("DROP LOGIN ", "LOGIN")]
    [InlineData("ALTER USER ", "USER")]
    [InlineData("DROP USER ", "USER")]
    [InlineData("DROP USER IF EXISTS ", "USER")]
    [InlineData("ALTER SCHEMA ", "SCHEMA")]
    [InlineData("DROP SCHEMA IF EXISTS ", "SCHEMA")]
    [InlineData("ALTER DATABASE ", "DATABASE")]
    [InlineData("DROP DATABASE ", "DATABASE")]
    [InlineData("DROP CERTIFICATE ", "CERTIFICATE")]
    [InlineData("ALTER ROLE ", "ROLE")]
    [InlineData("ALTER SERVER ROLE ", "SERVER ROLE")]
    [InlineData("DROP APPLICATION ROLE ", "APPLICATION ROLE")]
    [InlineData("DROP DATABASE SCOPED CREDENTIAL ", "DATABASE SCOPED CREDENTIAL")]
    [InlineData("OPEN SYMMETRIC KEY ", "SYMMETRIC KEY")]
    [InlineData("CREATE USER LibReader FOR LOGIN ", "LOGIN")]
    [InlineData("ALTER QUEUE ", "QUEUE")]
    [InlineData("DROP SERVICE ", "SERVICE")]
    [InlineData("DROP MESSAGE TYPE ", "MESSAGE TYPE")]
    [InlineData("DROP REMOTE SERVICE BINDING ", "REMOTE SERVICE BINDING")]
    [InlineData("CREATE SERVICE LoanService ON QUEUE ", "QUEUE")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE ", "SERVICE")]
    [InlineData("BEGIN DIALOG @Handle FROM SERVICE LoanSender TO SERVICE 'LoanReceiver' ON CONTRACT ", "CONTRACT")]
    [InlineData("GRANT SEND ON SERVICE::", "SERVICE")]
    [InlineData("SELECT 1\nALTER LOGIN ", "LOGIN")]
    // 清單片語的一項：DEFAULT_ 加種類、種類本身。
    [InlineData("ALTER LOGIN L1 WITH PASSWORD = 'x', DEFAULT_DATABASE = ", "DATABASE")]
    [InlineData("CREATE LOGIN L1 WITH PASSWORD = 'x', DEFAULT_DATABASE = ", "DATABASE")]
    [InlineData("ALTER USER U1 WITH NAME = U2, DEFAULT_SCHEMA = ", "SCHEMA")]
    [InlineData("ALTER USER U1 WITH DEFAULT_SCHEMA = ", "SCHEMA")]
    [InlineData("CREATE USER U3 WITHOUT LOGIN WITH DEFAULT_SCHEMA = ", "SCHEMA")]
    [InlineData("ALTER USER U1 WITH LOGIN = ", "LOGIN")]
    [InlineData("ALTER LOGIN L1 WITH CREDENTIAL = ", "CREDENTIAL")]
    // 類別之後的 ::。
    [InlineData("GRANT SELECT ON SCHEMA::", "SCHEMA")]
    [InlineData("GRANT CONTROL ON LOGIN::", "LOGIN")]
    [InlineData("ALTER AUTHORIZATION ON SERVER ROLE::", "SERVER ROLE")]
    [InlineData("GRANT CONTROL ON ASYMMETRIC KEY::", "ASYMMETRIC KEY")]
    // 主體：擁有者與成員的範圍取那一句的種類。
    [InlineData("ALTER ROLE R1 ADD MEMBER ", "USER", "ROLE")]
    [InlineData("ALTER ROLE R1 DROP MEMBER ", "USER", "ROLE")]
    [InlineData("ALTER SERVER ROLE R1 ADD MEMBER ", "LOGIN", "SERVER ROLE")]
    [InlineData("CREATE SCHEMA Lending AUTHORIZATION ", "USER", "ROLE")]
    [InlineData("CREATE SCHEMA AUTHORIZATION ", "USER", "ROLE")]
    [InlineData("CREATE CERTIFICATE LibCert AUTHORIZATION ", "USER", "ROLE")]
    [InlineData("CREATE SERVER ROLE LibOperators AUTHORIZATION ", "LOGIN", "SERVER ROLE")]
    // 權限的 TO／FROM：範圍依 ON 的類別，沒有 ON 時兩邊都列。
    [InlineData("GRANT SELECT ON dbo.Loan TO ", "USER", "ROLE")]
    [InlineData("GRANT SELECT ON dbo.Loan TO LibReader, ", "USER", "ROLE")]
    [InlineData("REVOKE SELECT ON SCHEMA::dbo FROM ", "USER", "ROLE")]
    [InlineData("GRANT CONNECT ON DATABASE::LibArchive TO ", "USER", "ROLE")]
    [InlineData("GRANT CONTROL ON LOGIN::LibAdmin TO ", "LOGIN", "SERVER ROLE")]
    [InlineData("ALTER AUTHORIZATION ON SCHEMA::Lending TO ", "USER", "ROLE")]
    [InlineData("ALTER AUTHORIZATION ON DATABASE::LibArchive TO ", "LOGIN", "SERVER ROLE")]
    [InlineData("DENY VIEW DEFINITION TO ", "USER", "ROLE", "LOGIN", "SERVER ROLE")]
    [InlineData("GRANT SELECT TO ", "USER", "ROLE", "LOGIN", "SERVER ROLE")]
    public void 既有名稱那一格列那一種的名單(string textBeforeCaret, params string[] kinds)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);

        Assert.Equal(CompletionTarget.CatalogEntity, context.Target);
        Assert.Equal(kinds.OrderBy(kind => kind), context.CatalogEntities.Select(entity => entity.Kind).OrderBy(kind => kind));
    }

    /// <summary>新名字、值寫完、不在清單裡的等號，與名單只有伺服器知道的語言都不是。</summary>
    [Theory]
    [InlineData("CREATE LOGIN ")]
    [InlineData("CREATE USER ")]
    [InlineData("CREATE SCHEMA ")]
    [InlineData("CREATE DATABASE ")]
    [InlineData("ALTER LOGIN L1 ")]
    [InlineData("DROP USER LibReader ")]
    [InlineData("ALTER USER U1 WITH DEFAULT_SCHEMA = dbo ")]
    [InlineData("UPDATE dbo.Lib_Reader SET DEFAULT_SCHEMA = ")]
    [InlineData("SELECT * FROM dbo.Lib_Reader WHERE DEFAULT_DATABASE = ")]
    [InlineData("ALTER LOGIN L1 WITH DEFAULT_LANGUAGE = ")]
    [InlineData("DROP TABLE ")]
    [InlineData("DROP EXTERNAL TABLE ")]
    public void 不是既有名稱那一格(string textBeforeCaret)
    {
        Assert.NotEqual(CompletionTarget.CatalogEntity, SqlCompletionContextAnalyzer.Analyze(textBeforeCaret).Target);
    }

    /// <summary>名冊的每一種都是產生器探到的建立種類：字對不上的話那一種永遠比對不到。</summary>
    [Fact]
    public void 名冊的種類都是產生器的建立種類()
    {
        var created = SqlKeywordCatalogData.CreatedKinds.Select(kind => kind.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(SqlCatalogEntity.All, entity => Assert.Contains(entity.Kind, created));
        Assert.All(SqlCatalogEntity.All, entity => Assert.False(string.IsNullOrWhiteSpace(entity.KindText)));
    }

    [Fact]
    public async Task 名單列那一種的名稱與片語的字_不列資料表()
    {
        var metadata = new EntityMetadata();

        var list = await GetAsync("ALTER DATABASE |", new SqlAssistSettings(), metadata);

        Assert.Contains(list, item => item.DisplayText == "LibArchive" && item.Kind == SuggestionKind.CatalogEntity);
        Assert.Contains(list, item => item.DisplayText == "CURRENT");
        Assert.DoesNotContain(list, item => item.DisplayText == "Lib_Reader");
        Assert.Equal(new[] { "DATABASE" }, metadata.Asked);
    }

    [Fact]
    public async Task 主體列兩種的聯集()
    {
        var metadata = new EntityMetadata();

        var list = await GetAsync("ALTER ROLE LibReaders ADD MEMBER |", new SqlAssistSettings(), metadata);

        Assert.Contains(list, item => item.DisplayText == "LibClerk");
        Assert.Contains(list, item => item.DisplayText == "LibReaders");
        Assert.DoesNotContain(list, item => item.DisplayText == "LibAdmin");
    }

    /// <summary>主體那一格的關鍵字由位置給：PUBLIC、擁有者的 SCHEMA（OWNER 由片語接）；其餘關鍵字與物件都不列。</summary>
    [Theory]
    [InlineData("GRANT SELECT ON dbo.Loan TO |", "PUBLIC")]
    [InlineData("REVOKE SELECT ON dbo.Loan FROM LibClerk, |", "PUBLIC")]
    [InlineData("ALTER AUTHORIZATION ON SCHEMA::Lending TO |", "SCHEMA")]
    public async Task 主體那一格列位置的關鍵字(string text, string keyword)
    {
        var list = await GetAsync(text, new SqlAssistSettings(), new EntityMetadata());

        Assert.Contains(list, item => item.DisplayText == keyword);
        Assert.Contains(list, item => item.DisplayText == "LibClerk");
        Assert.DoesNotContain(list, item => item.DisplayText is "SELECT" or "Lib_Reader");
    }

    /// <summary>選項的值與類別之後判不出位置：整份關鍵字不跟著進來，只剩名稱與片語的字。</summary>
    [Theory]
    [InlineData("ALTER LOGIN L1 WITH PASSWORD = 'x', DEFAULT_DATABASE = |", "LibArchive")]
    [InlineData("GRANT SELECT ON LOGIN::|", "LibAdmin")]
    public async Task 名稱格不列其餘關鍵字(string text, string name)
    {
        var list = await GetAsync(text, new SqlAssistSettings(), new EntityMetadata());

        Assert.Contains(list, item => item.DisplayText == name);
        Assert.DoesNotContain(list, item => item.Kind is SuggestionKind.Keyword or SuggestionKind.Snippet);
    }

    [Fact]
    public async Task 關掉資料庫物件時只剩片語的字()
    {
        var metadata = new EntityMetadata { Throws = true };
        var settings = new SqlAssistSettings { IncludeDatabaseObjects = false };

        var list = await GetAsync("DROP USER |", settings, metadata);

        Assert.Contains(list, item => SqlClausePhrase.FirstWord(item.DisplayText) == "IF");
        Assert.DoesNotContain(list, item => item.Kind == SuggestionKind.CatalogEntity);
    }

    [Fact]
    public async Task 網域帳戶插入時加方括號()
    {
        var context = Analyze("ALTER LOGIN |");
        var list = await SqlCompletionCandidates.GetAsync(context, BuiltIn, new SqlAssistSettings(), new EntityMetadata(), CancellationToken.None);
        var account = Assert.Single(list, item => item.DisplayText == @"LIBRARY\Reader");

        Assert.Equal(@"[LIBRARY\Reader]", SqlInsertionText.Build(account, context, new SqlAssistSettings()));
    }

    [Fact]
    public async Task 方括號裡照樣列名稱()
    {
        var list = await GetAsync("DROP LOGIN [|", new SqlAssistSettings(), new EntityMetadata());

        Assert.Contains(list, item => item.DisplayText == "LibAdmin");
    }

    /// <summary>
    /// 語句寫到片語為止已經完整、又換了行：判得出下一句的位置才略過片語，判不出時片語的字照樣加進來。
    /// </summary>
    [Fact]
    public async Task 換行後判不出位置時片語的字照列()
    {
        var list = await GetAsync("CREATE USER U3\n|", new SqlAssistSettings(), new EntityMetadata());

        Assert.Contains(list, item => item.DisplayText.StartsWith("WITHOUT", StringComparison.Ordinal));
        Assert.Contains(list, item => item.DisplayText == "SELECT");
    }

    /// <summary>選項清單的一項裡面：密碼之後的尾巴、開關的值；CREATE USER 也收 Always Encrypted 的那一個。</summary>
    [Theory]
    [InlineData("ALTER LOGIN L1 WITH PASSWORD = 'x' ", "HASHED", "OLD_PASSWORD", "MUST_CHANGE", "UNLOCK")]
    [InlineData("ALTER LOGIN L1 WITH NAME = L2, PASSWORD = 'x' ", "HASHED", "OLD_PASSWORD", "MUST_CHANGE", "UNLOCK")]
    [InlineData("CREATE LOGIN L1 WITH PASSWORD = 'x' ", "HASHED", "MUST_CHANGE")]
    [InlineData("ALTER USER U1 WITH PASSWORD = 'x' ", "OLD_PASSWORD")]
    [InlineData("ALTER LOGIN L1 WITH CHECK_POLICY = ", "ON", "OFF")]
    [InlineData("ALTER LOGIN L1 WITH PASSWORD = 'x', CHECK_EXPIRATION = ", "ON", "OFF")]
    [InlineData("CREATE USER U3 WITHOUT LOGIN WITH ", "DEFAULT_SCHEMA", "ALLOW_ENCRYPTED_VALUE_MODIFICATIONS")]
    [InlineData("CREATE USER U3 FOR LOGIN L1 WITH DEFAULT_SCHEMA = dbo, ", "ALLOW_ENCRYPTED_VALUE_MODIFICATIONS")]
    [InlineData("ALTER USER U1 WITH ", "NAME", "DEFAULT_SCHEMA", "LOGIN", "PASSWORD", "DEFAULT_LANGUAGE", "ALLOW_ENCRYPTED_VALUE_MODIFICATIONS")]
    [InlineData("ALTER ROLE R1 ", "ADD", "DROP", "WITH")]
    [InlineData("ALTER ROLE R1 ADD ", "MEMBER")]
    [InlineData("ALTER SERVER ROLE R1 DROP ", "MEMBER")]
    public void 選項清單的字齊全(string textBeforeCaret, params string[] words)
    {
        var context = SqlCompletionContextAnalyzer.Analyze(textBeforeCaret);
        var offered = context.ClausePhrase?.Suggestions.Select(item => SqlClausePhrase.FirstWord(item.DisplayText)).ToHashSet() ?? new HashSet<string>();

        Assert.All(words, word => Assert.Contains(word, offered));
    }

    private static SqlCompletionContext Analyze(string text)
    {
        var caret = text.IndexOf('|');
        return SqlCompletionContextAnalyzer.Analyze(text.Remove(caret, 1), caret);
    }

    /// <param name="text">整份指令碼；<c>|</c> 是游標。</param>
    private static async Task<IReadOnlyList<SqlSuggestion>> GetAsync(
        string text,
        SqlAssistSettings settings,
        ISqlCompletionMetadata metadata)
    {
        return await SqlCompletionCandidates.GetAsync(Analyze(text), BuiltIn, settings, metadata, CancellationToken.None);
    }

    private sealed class EntityMetadata : ISqlCompletionMetadata
    {
        private static readonly Dictionary<string, string[]> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LOGIN"] = new[] { "LibAdmin", @"LIBRARY\Reader" },
            ["SERVER ROLE"] = new[] { "LibOperators" },
            ["USER"] = new[] { "LibClerk" },
            ["ROLE"] = new[] { "LibReaders" },
            ["DATABASE"] = new[] { "LibArchive" },
        };

        public bool Throws { get; init; }

        public List<string> Asked { get; } = new();

        public Task<IReadOnlyList<string>> GetCatalogEntityNamesAsync(SqlCatalogEntity entity, CancellationToken cancellationToken)
        {
            Ask();
            Asked.Add(entity.Kind);
            return Task.FromResult<IReadOnlyList<string>>(Names.TryGetValue(entity.Kind, out var names) ? names : Array.Empty<string>());
        }

        public Task<SqlCompletionContext> ResolveQualifierAsync(SqlCompletionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context);

        public Task<IReadOnlyList<SqlSuggestion>> GetObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken)
        {
            Ask();
            return Task.FromResult<IReadOnlyList<SqlSuggestion>>(new[]
            {
                new SqlSuggestion("Lib_Reader", "Lib_Reader", string.Empty, string.Empty, SuggestionKind.Table, schemaName: "dbo")
            });
        }

        public Task<IReadOnlyList<SqlSuggestion>> GetSystemObjectsAsync(SqlObjectPath? qualifierPath, CancellationToken cancellationToken) =>
            Nothing();

        public Task<IReadOnlyList<SqlSuggestion>> GetColumnsAsync(
            SqlTableReference table,
            SqlAssistSettings settings,
            CancellationToken cancellationToken) => Nothing();

        public IReadOnlyList<SqlSuggestion> PeekColumns(SqlTableReference table, string? qualifier, SqlAssistSettings settings) =>
            Array.Empty<SqlSuggestion>();

        public Task WarmColumnsAsync(IReadOnlyList<SqlColumnSource> sources, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<SqlSuggestion>> GetParametersAsync(SqlExecutedModule module, CancellationToken cancellationToken) =>
            Nothing();

        public Task<SqlInstanceListData> GetInstanceListAsync(SqlInstanceList list, CancellationToken cancellationToken) =>
            Task.FromResult(SqlInstanceListData.Empty);

        private Task<IReadOnlyList<SqlSuggestion>> Nothing()
        {
            Ask();
            return Task.FromResult<IReadOnlyList<SqlSuggestion>>(Array.Empty<SqlSuggestion>());
        }

        private void Ask()
        {
            if (Throws)
            {
                throw new InvalidOperationException("關掉資料庫物件時不該問資料庫。");
            }
        }
    }
}
