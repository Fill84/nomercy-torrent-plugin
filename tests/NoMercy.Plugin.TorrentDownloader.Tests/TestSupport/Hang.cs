namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>How long a test waits on something that must finish, before calling it a hang.</summary>
/// <remarks>
/// <para>
/// A bound here tells a hang from an answer, and nothing else. Set to what the work takes on a quiet
/// machine — five seconds, ten, thirty — it measured how busy the machine was: on the shared CI runner of
/// 15 and 16 September 2026 a cycle took 31 and 32 seconds beside tests that took 49 and 56, and a browser
/// start that is two thread-pool hops took five minutes while the whole test process stalled. Each failed
/// with nothing wrong.
/// </para>
/// <para>
/// Two minutes is far beyond any of that and still ends a run that really hangs.
/// </para>
/// </remarks>
public static class Hang
{
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);
}
