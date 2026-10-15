using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Scan;

/// <summary>
/// A small library compiled from source in memory, so a rule of the scan is
/// tested against what the compiler really writes rather than against a guess
/// at it.
/// </summary>
internal static class CompiledAssembly
{
    public static MemoryStream From(string source, bool allowUnsafe = false)
    {
        // The running runtime's own assemblies: the sample is compiled against
        // exactly the framework the plugin is.
        IEnumerable<MetadataReference> framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Sample",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13))],
            framework,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: allowUnsafe));

        MemoryStream image = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(image);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                "The sample does not compile: " + string.Join("; ", result.Diagnostics));
        }

        image.Position = 0;

        return image;
    }
}
