using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The one logger every part of the plugin is handed: the owner's level, the plugin's own file, and the
/// server's log for what the server will show.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the plugin has a logger of its own.</strong> The server's minimum level is Information for
/// every category, plugins included, and a plugin cannot change it — so every Debug line this plugin wrote
/// went nowhere, and GitHub issue #7 was an owner with a stuck download and nothing to read about it. This
/// wraps the server's logger: it lets through what the owner's level lets through, writes all of that to
/// <see cref="LogFile"/>, and hands Information and above on to the server as before.
/// </para>
/// <para>
/// <strong>The level changes at once.</strong> It is read on every line from a field a save writes, so a
/// change on the Settings page applies to the next line, with no restart.
/// </para>
/// <para>
/// <strong>No secret goes out of here.</strong> A line is cleaned before it is written anywhere: every
/// secret-named parameter blanked by the rule <see cref="Addresses.Redact"/> keeps, and every value the
/// secret store holds blanked wherever it appears — a private tracker's passkey is as often a path segment of
/// its announce address as a parameter, and only the value finds it there.
/// </para>
/// </remarks>
public sealed class PluginLog : ILogger
{
    /// <summary>The levels the owner can choose, quietest first, as the Settings page offers them.</summary>
    public static readonly IReadOnlyList<LogLevel> Offered =
        [LogLevel.Error, LogLevel.Warning, LogLevel.Information, LogLevel.Debug, LogLevel.Trace];

    /// <summary>
    /// Shorter than this is not blanked. Nothing real is that short — a passkey is thirty-two hex characters —
    /// and blanking two letters wherever they appear would leave every line unreadable while protecting nothing.
    /// </summary>
    private const int ShortestSecret = 6;

    private readonly ILogger _server;
    private readonly LogFile _file;

    /// <summary>The level as a number, so it can be read without a lock from every thread that logs.</summary>
    private int _level = (int)LogLevel.Information;

    /// <summary>Every secret value known, replaced whole when one is added, so a line reads it without a lock.</summary>
    private string[] _secrets = [];

    private readonly Lock _learning = new();

    public PluginLog(ILogger server, string folder, TimeProvider? time = null)
    {
        _server = server;
        _file = new(folder, time ?? TimeProvider.System);
    }

    /// <summary>The quietest level written. Information until the settings say otherwise.</summary>
    public LogLevel Level
    {
        get => (LogLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    /// <summary>Where the files are, for the Settings page to say.</summary>
    public string Folder => _file.Folder;

    /// <summary>A value no line may carry from now on.</summary>
    public void NeverSay(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < ShortestSecret)
        {
            return;
        }

        lock (_learning)
        {
            if (!_secrets.Contains(secret, StringComparer.Ordinal))
            {
                // Longest first, so a secret that contains another is blanked whole rather than leaving its
                // remainder showing round the shorter one's blank.
                _secrets = [.. _secrets.Append(secret).OrderByDescending(one => one.Length)];
            }
        }
    }

    /// <summary>Every value the secret store holds, read once so no line can carry one.</summary>
    /// <remarks>
    /// Read at start-up rather than as each is used, because nothing in the plugin reads them as it goes:
    /// a passkey stored on the page arrives in a line by way of a torrent's own announce list, not by way of
    /// the store, and the only way to know it there is to know it already.
    /// </remarks>
    public async Task LearnSecretsAsync(IPluginSecretStore secrets, CancellationToken ct)
    {
        foreach (string key in await secrets.KeysAsync(ct))
        {
            if (await secrets.GetAsync(key, ct) is string value)
            {
                NeverSay(value);
            }
        }
    }

    /// <summary>Writes what is waiting to the file now.</summary>
    public void Flush()
    {
        _file.Flush();
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None && logLevel >= Level;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _server.BeginScope(state);
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Before anything is formatted: a line below the level costs one comparison and nothing else.
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string line = Escaped(Clean(formatter(state, exception)));

        // The exception's own lines are real ones, so they stay lines — each escaped and indented under the
        // line it belongs to, so none of them starts at the left edge where an entry starts.
        string said = exception is null
            ? line
            : $"{line}{Environment.NewLine}{Indented(Clean(exception.ToString()))}";

        _file.Write(logLevel.ToString(), said);

        if (logLevel >= LogLevel.Information)
        {
            // The cleaned text rather than the template, its arguments and the exception: an argument is
            // exactly where a secret would ride, an exception's message is another, and the server's log is
            // a file on disk that gets pasted into support threads. So the server is handed no exception
            // object at all — its text, cleaned, is in the line.
            _server.Log(logLevel, eventId, said, null, static (text, _) => text);
        }
    }

    private string Clean(string text)
    {
        string cleaned = Addresses.RedactText(text);

        foreach (string secret in Volatile.Read(ref _secrets))
        {
            cleaned = cleaned.Replace(secret, Addresses.Blanked, StringComparison.OrdinalIgnoreCase);
        }

        return cleaned;
    }

    /// <summary><paramref name="text"/> on one line, every control character written out rather than obeyed.</summary>
    /// <remarks>
    /// A peer names its own client and a tracker writes its own failure reason, and both end up in lines.
    /// Written raw, a line break followed by a timestamp is a whole entry the plugin never wrote — in the
    /// owner's file and in the server's log — and an escape sequence can rewrite a terminal following it.
    /// </remarks>
    private static string Escaped(string text)
    {
        if (!text.Any(NeedsEscaping))
        {
            return text;
        }

        System.Text.StringBuilder escaped = new(text.Length + 16);

        foreach (char one in text)
        {
            escaped.Append(one switch
            {
                '\r' => @"\r",
                '\n' => @"\n",
                '\t' => @"\t",
                _ when NeedsEscaping(one) => $@"\u{(int)one:x4}",
                _ => one.ToString(),
            });
        }

        return escaped.ToString();
    }

    /// <summary>Control characters, and the two Unicode line and paragraph separators some readers break on.</summary>
    private static bool NeedsEscaping(char one)
    {
        return char.IsControl(one) || one == (char)0x2028 || one == (char)0x2029;
    }

    /// <summary>Each line of <paramref name="text"/> escaped and indented by four spaces.</summary>
    private static string Indented(string text)
    {
        return string.Join(
            Environment.NewLine,
            text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(one => $"    {Escaped(one)}"));
    }
}
