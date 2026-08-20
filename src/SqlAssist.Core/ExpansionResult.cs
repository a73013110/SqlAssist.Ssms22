namespace SqlAssist.Core;

public sealed class ExpansionResult
{
    public ExpansionResult(int replacementStart, int replacementLength, string replacementText, ExpansionKind kind)
    {
        ReplacementStart = replacementStart;
        ReplacementLength = replacementLength;
        ReplacementText = replacementText;
        Kind = kind;
    }

    public int ReplacementStart { get; }

    public int ReplacementLength { get; }

    public string ReplacementText { get; }

    public ExpansionKind Kind { get; }
}

