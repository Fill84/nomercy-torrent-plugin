namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests.TestSupport;

/// <summary>How long a test waits on something that must finish, before calling it a hang.</summary>
/// <remarks>
/// <para>
/// A bound here tells a hang from an answer, and nothing else. Set to what the work takes on a quiet
/// machine — three seconds, five, twenty — it measured how busy the machine was: on the shared CI runner of
/// September 2026 ordinary tests took forty seconds and more, and tests that waited a few seconds for a
/// loopback answer or a free pool thread failed with nothing wrong.
/// </para>
/// <para>
/// Two minutes is far beyond any of that and still ends a run that really hangs. The same figure as the
/// shell test project's own, which this project cannot reference.
/// </para>
/// </remarks>
public static class Hang
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);
}
