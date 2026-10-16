using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The server's library writer, as the contract offers it: it records what it was asked and can refuse.
/// </summary>
/// <remarks>
/// <para>
/// A boundary, not the plugin's own logic. The server's <c>PluginLibraryWriter</c> moves a file into a
/// <c>.recycled</c> folder beside it and throws <see cref="PluginLibraryAccessDeniedException"/> for a path
/// outside the libraries the owner granted; out of process the same refusal arrives as a
/// <see cref="PluginRefusedException"/>. This records each call, with how many encodes had been asked for at
/// that moment, so a test can say the old episode went before the new one was encoded.
/// </para>
/// <para>
/// It moves a recycled file exactly as the server does (media-server <c>dev</c>, read 16 October 2026:
/// <c>PluginLibraryWriter.RecycleAsync</c> and <c>RecyclePathFor</c>, then <c>LocalStorageDriver.MoveFile</c>):
/// into a <c>.recycled</c> folder beside it, under the same name, and never over a file already there —
/// <c>File.Move(…, overwrite: false)</c>, so a second copy of the same name is an <see cref="IOException"/>.
/// A double that moved nothing hid that, and a folder that still held every old file after its recycling.
/// </para>
/// </remarks>
/// <param name="encoder">The encoder whose asks are counted at each recycle, so the order can be asserted.</param>
public sealed class RecordingLibraryWriter(FakeEncoder? encoder = null) : IPluginLibraryWriter
{
    private readonly List<(string Path, int EncodesAskedBefore)> _recycled = [];

    /// <summary>Every path handed to <see cref="RecycleAsync"/>, in order, with the encodes asked for before it.</summary>
    public IReadOnlyList<(string Path, int EncodesAskedBefore)> Recycled => _recycled;

    /// <summary>Every path the plugin asked to delete or move. It must never ask either.</summary>
    public List<string> DeletedOrMoved { get; } = [];

    /// <summary>Why the server refuses every recycle, or null to take them.</summary>
    public string? Refusal { get; set; }

    public Task<IReadOnlyList<PluginLibrary>> GetWritableLibrariesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginLibrary>>([]);
    }

    public Task RecycleAsync(string path, CancellationToken ct = default)
    {
        if (Refusal is string reason)
        {
            throw new PluginLibraryAccessDeniedException(path, reason);
        }

        string normalised = path.Replace('\\', '/');
        int cut = normalised.LastIndexOf('/');
        string recycled = $"{normalised[..cut]}/.recycled/{normalised[(cut + 1)..]}";

        Directory.CreateDirectory(Path.GetDirectoryName(recycled)!);
        File.Move(path, recycled, overwrite: false);

        _recycled.Add((path, encoder?.Asked.Count ?? 0));

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        DeletedOrMoved.Add(path);

        return Task.CompletedTask;
    }

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        DeletedOrMoved.Add(sourcePath);

        return Task.CompletedTask;
    }

    /// <summary>What <see cref="CanWriteAsync"/> answers: whether the path is in a library the owner granted.</summary>
    public bool Writable { get; set; } = true;

    public Task<bool> CanWriteAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(Writable);
    }
}
