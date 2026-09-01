using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// A doc block describes the member under it.
/// </summary>
/// <remarks>
/// Eighteen places in <c>src/</c> carried two or three <c>&lt;summary&gt;</c>
/// blocks in one run of <c>///</c> lines, each of them a block pasted above a
/// member it does not describe — and in most of them the member it did describe
/// was a few lines further down with no block at all. The compiler says nothing
/// about it and neither does the formatter, so it went on happening.
/// </remarks>
public class DocBlocksTests
{
    [Fact]
    public void NoDocBlockDescribesTwoThings()
    {
        List<string> stacked = [];

        foreach (string file in Sources())
        {
            List<string> run = [];
            int at = 0;
            int began = 0;

            foreach (string line in File.ReadAllLines(file).Append(string.Empty))
            {
                at++;

                if (line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    if (run.Count == 0)
                    {
                        began = at;
                    }

                    run.Add(line);

                    continue;
                }

                if (run.Count > 0)
                {
                    int summaries = run.Count(one => one.Contains("<summary>", StringComparison.Ordinal));

                    if (summaries > 1)
                    {
                        stacked.Add($"{Path.GetFileName(file)}:{began} carries {summaries} summaries.");
                    }

                    run.Clear();
                }
            }
        }

        Assert.Empty(stacked);
    }

    /// <summary>Every source file this repository ships, and nothing generated.</summary>
    private static IEnumerable<string> Sources()
    {
        return Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(one => !one.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                          && !one.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No NoMercy.Plugin.TorrentDownloader.sln above {AppContext.BaseDirectory}.");
        }

        return directory.FullName;
    }
}
