using System.Text.Json;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The host's configuration store: whole-object JSON, exactly as the real one
/// keeps it.
/// </summary>
/// <remarks>
/// It keeps the serialised text rather than the object, so a default that
/// survives in memory but not through a serialiser fails here rather than on
/// the owner's machine — and so a test can ask whether a secret ever reached
/// the file, which is a thing only the text can answer.
/// </remarks>
public sealed class FakeConfiguration : IPluginConfiguration
{
    /// <summary>The JSON as it would sit on disk, in plaintext.</summary>
    public string Written { get; private set; } = string.Empty;

    /// <summary>
    /// How many times the settings have been read from here.
    /// </summary>
    /// <remarks>
    /// The transfers cadence runs every minute and every page draws from the
    /// settings, so this is a read of data that changes when an owner presses
    /// save, asked for at least once a minute. Nothing about the cost of it
    /// shows in an outcome.
    /// </remarks>
    public int Reads { get; private set; }

    public T? GetConfiguration<T>()
        where T : class, new()
    {
        Reads++;

        return Written.Length == 0 ? null : JsonSerializer.Deserialize<T>(Written);
    }

    public Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        return Task.FromResult(GetConfiguration<T>());
    }

    public void SaveConfiguration<T>(T configuration)
        where T : class
    {
        Written = JsonSerializer.Serialize(configuration);
    }

    public Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class
    {
        SaveConfiguration(configuration);
        return Task.CompletedTask;
    }

    public bool HasConfiguration()
    {
        return Written.Length > 0;
    }

    public void DeleteConfiguration()
    {
        Written = string.Empty;
    }
}
