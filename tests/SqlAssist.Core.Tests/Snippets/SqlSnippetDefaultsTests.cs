using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SqlAssist.Core.Completion;
using SqlAssist.Core.Keywords;
using SqlAssist.Core.Parsing;
using SqlAssist.Core.Snippets;
using Xunit;

namespace SqlAssist.Core.Tests.Snippets;

public sealed class SqlSnippetDefaultsTests
{
    [Fact]
    public void 內建JSON有四十三筆且識別碼與捷徑唯一()
    {
        var defaults = SqlSnippetDefaults.Current;

        Assert.Equal(43, defaults.Count);
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            defaults.Count,
            defaults.Snippets.Select(item => item.Shortcut).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(defaults.Snippets, snippet =>
        {
            Assert.True(SqlSnippetIdentity.IsValid(snippet.Id), snippet.Id);
            Assert.True(SqlSnippetLibrary.Empty.ValidateShortcut(snippet.Shortcut, null, out var error), error);
            Assert.False(SqlKeywordCatalog.TryGetCanonical(snippet.Shortcut, out _), snippet.Shortcut);
            Assert.DoesNotContain("$CURSOR$", snippet.Code, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 內建佔位符由程式碼順序推導且Tab模式至少有一欄()
    {
        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            var extracted = SqlSnippetPlaceholders.Extract(snippet.Code);

            Assert.Equal(extracted, snippet.Placeholders.Select(item => item.Id).ToArray());
            Assert.True(Count(snippet.Code, SqlSnippet.CaretMarker) <= 1, snippet.Shortcut);

            if (snippet.ExpansionMode == SqlSnippetExpansionMode.TabStops)
            {
                Assert.NotEmpty(snippet.Placeholders);
            }
        }
    }

    /// <remarks>
    /// 這一族片段的價值來自連線中繼資料，不是靜態骨架：展開出來的那一行只是
    /// 半句話，游標落點必須剛好是「會列出對應物件」的位置，下一次 Tab 才接得
    /// 下去。改成 Tab Stop、或在尾巴多一個字元（分號、括號、換行）都會把這條
    /// 鏈整個切斷，而症狀只是「清單沒有跳出來」，沒有任何錯誤。
    ///
    /// <see cref="CompletionIntent"/> 一起守：<c>ii</c> 落在
    /// <see cref="CompletionTarget.DataSource"/> 還不夠，要
    /// <see cref="CompletionIntent.InsertStatement"/> 才會由
    /// <c>SqlCommitExpander</c> 展開成欄位清單與 <c>VALUES</c>；退化成
    /// <see cref="CompletionIntent.Reference"/> 的話只是把資料表名稱補上去，
    /// 而那與使用者自己打完全一樣。
    /// </remarks>
    [Theory]
    [InlineData("ssf", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("st100", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("st1", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("ssc", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("sd", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("ii", CompletionTarget.DataSource, CompletionIntent.InsertStatement)]
    [InlineData("ui", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("df", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("ij", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("lj", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("rj", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("fj", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("cj", CompletionTarget.DataSource, CompletionIntent.Reference)]
    [InlineData("ca", CompletionTarget.Function, CompletionIntent.Reference)]
    [InlineData("oa", CompletionTarget.Function, CompletionIntent.Reference)]
    [InlineData("ap", CompletionTarget.Procedure, CompletionIntent.AlterDefinition)]
    [InlineData("af", CompletionTarget.Function, CompletionIntent.AlterDefinition)]
    public void 接續片段展開後落在會列出該類物件的位置(
        string shortcut,
        CompletionTarget target,
        CompletionIntent intent)
    {
        Assert.True(SqlSnippetDefaults.Current.TryGet(shortcut, out var snippet));
        Assert.Equal(SqlSnippetExpansionMode.Caret, snippet.ExpansionMode);
        Assert.True(snippet.TriggerFollowUp, shortcut);

        var expanded = snippet.Expand(out var caret);

        Assert.Equal(expanded.Length, caret);

        var context = SqlCompletionContextAnalyzer.Analyze(expanded);

        Assert.Equal(target, context.Target);
        Assert.Equal(intent, context.Intent);
    }

    /// <remarks>
    /// 反過來守：<c>triggerFollowUp</c> 少勾一個，那一筆就退化成「插入半句話之後
    /// 什麼都不做」——使用者看到的是一行寫到一半的 SQL 與一個不動的游標。
    /// 上面那份表格漏掉新片段時這裡會失敗。
    /// </remarks>
    [Fact]
    public void 每一筆接續片段都在上面的表格裡()
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ssf", "st100", "st1", "ssc", "sd", "ii", "ui", "df",
            "ij", "lj", "rj", "fj", "cj", "ca", "oa", "ap", "af"
        };

        var actual = SqlSnippetDefaults.Current.Snippets
            .Where(item => item.TriggerFollowUp)
            .Select(item => item.Shortcut)
            .ToArray();

        Assert.Equal(covered.OrderBy(item => item, StringComparer.Ordinal), actual.OrderBy(item => item, StringComparer.Ordinal));
    }

    /// <remarks>
    /// 這個 repo 是公開的，識別字本身就是使用者的私有資產（見 CLAUDE.md）。
    /// 內建片段是最容易不小心把真實 schema 名稱帶進來的地方：寫樣板時手邊
    /// 正好開著一份真的指令碼。這裡只認一組通用佔位名稱，要加新的就得先想過。
    /// </remarks>
    [Fact]
    public void 內建片段只用通用的佔位名稱()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "dbo", "TableName", "ColumnName", "SchemaName", "DatabaseName",
            "ProcedureName", "FunctionName", "ViewName", "IndexName",
            "TargetTable", "SourceTable", "TargetColumn", "SourceColumn",
            "KeyColumn", "UpdateColumn", "InsertColumn", "TempTable",
            "IX_TableName_ColumnName",
            "Value", "Name", "cte", "item_cursor", "t"
        };

        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            foreach (var placeholder in snippet.Placeholders)
            {
                // 限定名稱要逐段檢查。dbo.TableName 整串比對時 LooksLikeIdentifier
                // 會因為那個點號判定「不是識別字」而整筆放行——而物件欄位合併之後，
                // 預設值正好都是這個形狀，等於守門對最該守的那一族失效。
                foreach (var part in placeholder.DefaultValue.Split('.'))
                {
                    // 空白、數字、SQL 常值與型別名稱都不是識別字，不必列進白名單。
                    if (part.Length == 0 || !LooksLikeIdentifier(part))
                    {
                        continue;
                    }

                    Assert.True(
                        allowed.Contains(part),
                        $"{snippet.Shortcut} 的 ${placeholder.Id}$ 預設值「{placeholder.DefaultValue}」" +
                        $"裡的「{part}」不在通用佔位名稱清單裡；真實系統的名稱不能進這個 repo。");
                }
            }
        }
    }

    /// <summary>看起來像資料庫識別字（會被誤認成真實名稱）的預設值。</summary>
    private static bool LooksLikeIdentifier(string value)
    {
        if (!char.IsLetter(value[0]) && value[0] != '_')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        // 全大寫的是 T-SQL 型別與關鍵字（INT、NULL、TABLE…），那是語言事實。
        return value.ToUpperInvariant() != value;
    }

    [Fact]
    public void CASE捷徑不與關鍵字撞名()
    {
        Assert.False(SqlSnippetDefaults.Current.TryGet("case", out _));
        Assert.True(SqlSnippetDefaults.Current.TryGet("cs", out _));
    }

    [Fact]
    public void 沒有前綴時Snippet不會佔據第一順位()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "CopyNo",
                    "[CopyNo]",
                    "INT",
                    "CopyNo",
                    SuggestionKind.Column)
            });

        var first = candidates
            .OrderByDescending(SuggestionMatcher.ComposeStandingScore)
            .First();

        Assert.NotEqual(SuggestionKind.Snippet, first.Kind);
    }

    [Fact]
    public void 最近使用過的Snippet仍不佔據空前綴首頁()
    {
        SqlSuggestionUsage.Clear();

        try
        {
            var snippet = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
                .Single(item => item.DisplayText == "ssf");
            var column = new SqlSuggestion("CopyNo", "CopyNo", "INT", "CopyNo", SuggestionKind.Column);
            SqlSuggestionUsage.Record(snippet);

            Assert.True(
                SuggestionMatcher.ComposeStandingScore(column) >
                SuggestionMatcher.ComposeStandingScore(snippet));
        }
        finally
        {
            SqlSuggestionUsage.Clear();
        }
    }

    [Fact]
    public void 輸入libr時資料表排在任何Snippet之前()
    {
        var candidates = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Concat(new[]
            {
                new SqlSuggestion(
                    "Lib_Reader",
                    "[dbo].[Lib_Reader]",
                    "Table · dbo",
                    "Table Lib_Reader",
                    SuggestionKind.Table,
                    schemaName: "dbo")
            });

        var ranked = SuggestionMatcher.Rank(
            candidates,
            SqlCompletionContextAnalyzer.Analyze("libr"));

        Assert.Equal("Lib_Reader", ranked[0].Suggestion.DisplayText);
    }

    [Fact]
    public void 危險片段只在空前綴首頁隱藏()
    {
        var destructive = BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current)
            .Single(item => item.DisplayText == "df");

        Assert.False(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: false));
        Assert.True(SuggestionMatcher.IsVisibleWithoutPrefix(destructive, categorySelected: true));
    }

    [Fact]
    public void DDL片段不會混進SELECT欄位位置()
    {
        var filtered = Available("SELECT ");

        Assert.DoesNotContain("ctb", filtered);
        Assert.Contains("cs", filtered);
    }

    /// <remarks>
    /// <c>positions</c> 給得太緊的症狀是全靜默的：使用者只覺得「這個片段有時候
    /// 有、有時候沒有」。每一筆都要有一個「一定找得到」的位置守著。
    /// </remarks>
    [Theory]
    // 語句級：語句開頭與 BEGIN…END 區塊裡都要在。曾經只給 StatementStart，
    // 於是 BEGIN 之後（分析器只回報 BlockStart）整批語句片段全部消失。
    [InlineData("SELECT 1;\n", "ssf,st100,st1,ssc,sd,ii,ui,df,mg,cdb,ctb,cv,cp,cf,citvf,cix,at,dt,ap,af,be,bt,ct,rt,ife,ifne,wl,tc,cur,trn,cte,sno,ptt")]
    [InlineData("BEGIN\n    ", "ssf,st100,st1,ssc,sd,ii,ui,df,mg,cdb,ctb,cv,cp,cf,citvf,cix,at,dt,ap,af,be,bt,ct,rt,ife,ifne,wl,tc,cur,trn,cte,sno,ptt")]
    // 運算式級：CASE 在選取清單、逗號之後與述詞裡都要在。
    [InlineData("SELECT ", "cs")]
    [InlineData("SELECT a, ", "cs")]
    [InlineData("SELECT * FROM Loan WHERE ", "cs")]
    [InlineData("SELECT * FROM Loan a INNER JOIN Copy b ON ", "cs")]
    // 資料來源之後：JOIN／APPLY 全家與排序、分組子句。
    [InlineData("SELECT * FROM Loan AS a ", "ij,lj,rj,fj,cj,ca,oa,ob,gb")]
    public void 內建片段在它自然的位置找得到(string prefix, string shortcuts)
    {
        var available = Available(prefix);

        foreach (var shortcut in shortcuts.Split(','))
        {
            Assert.Contains(shortcut, available);
        }
    }

    /// <remarks>
    /// 結構描述與名稱分成兩格的代價是每個物件都要按兩次 Tab，而第一格的答案幾乎
    /// 永遠是 dbo——那一次 Tab 是白按的。合成一格之後，Completion 依設定插進來的
    /// <c>dbo.Lib_Reader</c>、<c>Lib_Reader</c>、<c>[dbo].[Lib_Reader]</c> 三種寫法
    /// 也才填得進同一格（見 <c>SqlInsertionText</c>）；拆成兩格時第三種根本放不下。
    /// </remarks>
    [Fact]
    public void 物件欄位不拆成結構描述與名稱兩格()
    {
        var split = new Regex(@"\$[A-Za-z][A-Za-z0-9]*\$\.\$[A-Za-z]");

        foreach (var snippet in SqlSnippetDefaults.Current.Snippets)
        {
            Assert.False(
                split.IsMatch(snippet.Code),
                $"{snippet.Shortcut} 把結構描述與名稱拆成兩格了；合成一格填完整名稱。");
        }
    }

    /// <remarks>
    /// 這一族欄位的價值來自「Tab 進去就有清單」。清單開不開，由展開文字中這一格
    /// <b>起點之前</b>的那一段決定——樣板把 FROM 換成別的字、或在關鍵字與欄位之間
    /// 多一個字元，清單就靜靜地不再出現，沒有任何錯誤。這裡把那條鏈釘死。
    ///
    /// 分析的是展開後的文字而不是 <c>Code</c>：前面幾格已經填成預設值，
    /// 而使用者在編輯器裡看到的正是那一份。
    /// </remarks>
    [Theory]
    [InlineData("mg", "targetTable")]
    [InlineData("mg", "sourceTable")]
    [InlineData("cv", "sourceTable")]
    [InlineData("citvf", "sourceTable")]
    [InlineData("at", "table")]
    [InlineData("dt", "table")]
    [InlineData("ife", "table")]
    [InlineData("ifne", "table")]
    [InlineData("cur", "table")]
    [InlineData("cte", "table")]
    public void 物件欄位落在會列出資料來源的位置(string shortcut, string fieldId)
    {
        var context = AnalyzeBeforeField(shortcut, fieldId);

        Assert.True(context.IsValid);
        Assert.Equal(CompletionTarget.DataSource, context.Target);
    }

    /// <remarks>
    /// 反過來守：這幾格填的是使用者正要取的<b>新名字</b>，清單裡沒有一項會是對的。
    /// 彈出來的唯一效果是他順手按下 Enter，剛打的名字被換成別人的資料表。
    ///
    /// 不必為此加旗標：<c>CREATE TABLE</c>、<c>CREATE VIEW</c> 這些位置推不出目標，
    /// 前綴又被清空，分析器自己就回報不參與。「什麼時候該有清單」的規則因此
    /// 只有一份，在分析器裡。
    /// </remarks>
    [Theory]
    [InlineData("ctb", "table")]
    [InlineData("cv", "view")]
    [InlineData("cp", "procedure")]
    [InlineData("cf", "function")]
    [InlineData("citvf", "function")]
    public void 新建物件的名稱欄位不主動開清單(string shortcut, string fieldId)
    {
        Assert.False(AnalyzeBeforeField(shortcut, fieldId).IsValid);
    }

    /// <remarks>
    /// <b>已知缺口，不是期望行為。</b><c>cix</c> 的資料表欄位落在 <c>ON</c> 之後，
    /// 而 <c>ON</c> 也是 JOIN 條件的位置——那裡要的是欄位，不是資料表。目前分不出
    /// 這兩者，所以這一格沒有清單。
    ///
    /// 釘在這裡是為了讓它別被遺忘：哪天分析器認得 <c>CREATE … INDEX … ON</c>，
    /// 這個測試會失敗，那時把這一行移到上面那組即可。
    /// </remarks>
    [Fact]
    public void 索引的資料表欄位目前分不出JOIN條件因此沒有清單()
    {
        Assert.False(AnalyzeBeforeField("cix", "table").IsValid);
    }

    /// <remarks>
    /// <c>mg</c> 的六個欄位格分屬 target 與 source。它們曾經是三個同名欄位，
    /// 而原生引擎會把同名的同步起來——選了目標的比對鍵，來源那一邊就跟著變成
    /// 同一個名字，可是兩張表不一定同名。拆開之後這裡連「這一格該列哪張表的
    /// 欄位」一起守。
    ///
    /// 走全文分析：限定字要解析成資料表，得看得到游標後方的
    /// <c>MERGE INTO … AS target</c>。
    /// </remarks>
    [Theory]
    [InlineData("targetKey", "TargetTable")]
    [InlineData("sourceKey", "SourceTable")]
    [InlineData("targetUpdate", "TargetTable")]
    [InlineData("sourceUpdate", "SourceTable")]
    [InlineData("sourceInsert", "SourceTable")]
    public void MERGE片段的欄位格解析得出它屬於哪一張表(string fieldId, string expected)
    {
        var context = AnalyzeInField("mg", fieldId);

        Assert.Equal(CompletionTarget.Column, context.Target);

        var source = Assert.Single(context.ColumnSources!);

        Assert.Equal(SqlColumnSourceKind.Table, source.Kind);
        Assert.Equal(expected, source.Table!.ObjectName);
    }

    /// <remarks>
    /// <c>INSERT ($targetInsert$)</c> 沒有限定字，那個位置推不出目標，
    /// 而剛進格時前綴又是空的——與 <c>SELECT |</c> 一樣要打了字才有清單。
    /// 這不是缺口：那一格文法上是 target 的欄位，使用者打第一個字母時
    /// 敘述範圍會把兩張表的欄位都交出來（見
    /// <c>SqlColumnCompletionTests.MERGE的INSERT欄位清單看得到兩張表</c>）。
    /// </remarks>
    [Fact]
    public void MERGE的INSERT欄位格要打了字才有清單()
    {
        Assert.False(AnalyzeInField("mg", "targetInsert").IsValid);
    }

    /// <summary>展開文字中某一格<b>起點</b>那個位置的全文分析結果。</summary>
    private static SqlCompletionContext AnalyzeInField(string shortcut, string fieldId)
    {
        var expansion = Snippet(shortcut).Expansion;
        var field = expansion.Fields.Single(
            item => string.Equals(item.Placeholder.Id, fieldId, StringComparison.Ordinal));

        return SqlCompletionContextAnalyzer.Analyze(expansion.Text, field.Offset);
    }

    /// <summary>展開文字中某一格<b>起點之前</b>那一段的分析結果。</summary>
    private static SqlCompletionContext AnalyzeBeforeField(string shortcut, string fieldId)
    {
        var expansion = Snippet(shortcut).Expansion;
        var field = expansion.Fields.Single(
            item => string.Equals(item.Placeholder.Id, fieldId, StringComparison.Ordinal));

        return SqlCompletionContextAnalyzer.Analyze(expansion.Text.Substring(0, field.Offset));
    }

    private static SqlSnippet Snippet(string shortcut)
    {
        return SqlSnippetDefaults.Current.Snippets.Single(
            item => string.Equals(item.Shortcut, shortcut, StringComparison.Ordinal));
    }

    private static IReadOnlyCollection<string> Available(string prefix)
    {
        return SuggestionMatcher
            .Filter(
                BuiltInSuggestionCatalog.Create(SqlSnippetDefaults.Current),
                SqlCompletionContextAnalyzer.Analyze(prefix))
            .Where(item => item.Kind == SuggestionKind.Snippet)
            .Select(item => item.DisplayText)
            .ToArray();
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;

        while ((offset = text.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
