using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlAssist.KeywordGenerator;

/// <summary>包住某個位置的最內層語句：IF 的主體、BEGIN … END 裡的一句都算自己的一句。</summary>
internal sealed class StatementFinder : TSqlFragmentVisitor
{
    private readonly int _offset;

    public StatementFinder(int offset)
    {
        _offset = offset;
    }

    public TSqlStatement? Innermost { get; private set; }

    public override void Visit(TSqlStatement node)
    {
        if (node.StartOffset <= _offset &&
            node.StartOffset + node.FragmentLength > _offset &&
            (Innermost == null || node.FragmentLength < Innermost.FragmentLength))
        {
            Innermost = node;
        }
    }
}

/// <summary>從字或字之前開始、正好在結尾收住的語句以外的片段：運算式、排序項、資料表提示。</summary>
internal sealed class ItemEndingFinder : TSqlFragmentVisitor
{
    private readonly int _wordStart;
    private readonly int _end;

    public ItemEndingFinder(int wordStart, int end)
    {
        _wordStart = wordStart;
        _end = end;
    }

    public bool Found { get; private set; }

    public override void Visit(TSqlFragment node)
    {
        if (!(node is TSqlStatement) && !(node is TSqlBatch) && !(node is TSqlScript) &&
            node.StartOffset >= 0 &&
            node.StartOffset <= _wordStart &&
            node.StartOffset + node.FragmentLength == _end)
        {
            Found = true;
        }
    }
}
