using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace C2.Features.Loaders;

public class CSharpCompiler
{
    private readonly HttpClient _http;
    private MetadataReference[]? _net20Refs;
    private MetadataReference[]? _net40Refs;

    public CSharpCompiler(HttpClient http) => _http = http;

    public async Task<CompileResult> CompileAsync(string source, string assemblyName, bool net4)
    {
        var refs = net4 ? await LoadNet40Refs() : await LoadNet20Refs();
        if (refs is null)
        {
            var ver = net4 ? "4.0" : "2.0";
            return new CompileResult(null,
                [$".NET Framework {ver} reference assemblies not found in wwwroot/refs/. " +
                 (net4 ? "Ensure .NET Framework 4.x is installed." : "Enable the Windows '.NET Framework 3.5' feature.")]);
        }

        var tree = CSharpSyntaxTree.ParseText(source);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(true);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            refs,
            options);

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            return new CompileResult(null, errors);
        }

        return new CompileResult(ms.ToArray(), []);
    }

    private async Task<MetadataReference[]?> LoadNet20Refs()
    {
        if (_net20Refs is not null) return _net20Refs;
        try
        {
            var mscorlib = await FetchRef("refs/net20/mscorlib.dll");
            var system = await FetchRef("refs/net20/System.dll");
            _net20Refs = [mscorlib, system];
            return _net20Refs;
        }
        catch { return null; }
    }

    private async Task<MetadataReference[]?> LoadNet40Refs()
    {
        if (_net40Refs is not null) return _net40Refs;
        try
        {
            var mscorlib = await FetchRef("refs/net40/mscorlib.dll");
            var system = await FetchRef("refs/net40/System.dll");
            _net40Refs = [mscorlib, system];
            return _net40Refs;
        }
        catch { return null; }
    }

    private async Task<MetadataReference> FetchRef(string path)
    {
        var bytes = await _http.GetByteArrayAsync(path);
        return MetadataReference.CreateFromImage(bytes);
    }
}

public record CompileResult(byte[]? Assembly, string[] Errors)
{
    public bool Success => Assembly is not null;
}
