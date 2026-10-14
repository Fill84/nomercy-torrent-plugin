namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>Reading the plugin's own log files while the plugin may still be writing them.</summary>
/// <remarks>
/// <c>File.ReadAllText</c> asks that nobody else write the file while it reads, and a plugin that is running
/// writes its log from other threads whenever it likes: the read is refused, and a test fails over the
/// plugin doing exactly what it should. Shared the way the plugin shares it, as an owner's tail would be.
/// </remarks>
public static class LogFiles
{
    /// <summary>Every line in every file of the folder, or nothing when there is no folder yet.</summary>
    public static IReadOnlyList<string> Lines(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        List<string> lines = [];

        foreach (string path in Directory.GetFiles(folder))
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);

            while (reader.ReadLine() is string line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
