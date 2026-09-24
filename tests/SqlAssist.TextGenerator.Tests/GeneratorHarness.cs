using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SqlAssist.Core.Localization;

namespace SqlAssist.TextGenerator.Tests;

/// <summary>在記憶體裡跑產生器與分析器；參考 Core，讓產生的類別真的編得過、跑得動。</summary>
internal static class GeneratorHarness
{
    public const string Root = @"C:\repo\src\SqlAssist.Sample\Feature\";

    public static GeneratorRun Generate(params (string Name, string Json)[] files) =>
        Generate(files, "zh-Hant,en");

    public static GeneratorRun Generate((string Name, string Json)[] files, string languages)
    {
        var compilation = Compile(string.Empty);
        var texts = files.Select(file => (AdditionalText)new MemoryText(Root + file.Name, file.Json)).ToImmutableArray();
        var options = new Options(new Dictionary<string, string> { ["build_property.SqlAssistTextLanguages"] = languages });
        var driver = CSharpGeneratorDriver.Create(
            new[] { new SqlTextGenerator().AsSourceGenerator() },
            texts,
            (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
            options);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return new GeneratorRun(output, diagnostics);
    }

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool check = true)
    {
        var values = new Dictionary<string, string>();
        if (!check)
        {
            values["build_property.SqlAssistTextCheckLiterals"] = "false";
        }

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new LiteralTextAnalyzer());
        var withAnalyzers = Compile(source).WithAnalyzers(
            analyzers,
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new Options(values)));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    public static CSharpCompilation Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SqlText).Assembly.Location));
        return CSharpCompilation.Create(
            "Sample",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private sealed class MemoryText : AdditionalText
    {
        private readonly string _text;

        public MemoryText(string path, string text)
        {
            Path = path;
            _text = text;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(_text);
    }

    private sealed class Options : AnalyzerConfigOptionsProvider
    {
        public Options(Dictionary<string, string> values)
        {
            GlobalOptions = new Values(values);
        }

        public override AnalyzerConfigOptions GlobalOptions { get; }

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values.Empty;
    }

    private sealed class Values : AnalyzerConfigOptions
    {
        public static readonly Values Empty = new(new Dictionary<string, string>());

        private readonly Dictionary<string, string> _values;

        public Values(Dictionary<string, string> values)
        {
            _values = values;
        }

        public override bool TryGetValue(string key, out string value) =>
            _values.TryGetValue(key, out value!);
    }
}

internal sealed class GeneratorRun
{
    public GeneratorRun(Compilation output, ImmutableArray<Diagnostic> diagnostics)
    {
        Output = output;
        Diagnostics = diagnostics;
    }

    public Compilation Output { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public IEnumerable<string> Ids => Diagnostics.Select(diagnostic => diagnostic.Id);

    /// <summary>把產生的組件載進來，取一個靜態屬性或呼叫一個靜態方法。</summary>
    public object? Invoke(string type, string member, params object?[] arguments)
    {
        using var stream = new MemoryStream();
        var emitted = Output.Emit(stream);
        if (!emitted.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var target = assembly.GetType(type, throwOnError: true)!;
        return arguments.Length == 0 && target.GetProperty(member) is { } property
            ? property.GetValue(null)
            : target.GetMethod(member)!.Invoke(null, arguments);
    }
}
