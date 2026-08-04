// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// Everything IPluginContext hands a plugin, composed from the fakes Tasks 2-4 already
// built rather than reinvented: FakeConfiguration, FakeSecretStore, FakeGrants and
// FakeLibraryQuery are the exact doubles those tasks' own tests exercise, exposed here
// under both their concrete type (so a test can inspect them) and the interface members
// IPluginContext requires. The handful of members with no earlier fake - EventBus, Hub,
// Services - get the smallest no-op double that satisfies the interface, because nothing
// in this stage's plugin exercises them.
public sealed class FakePluginContext : IPluginContext
{
    public FakePluginContext()
    {
        DataFolderPath = Directory.CreateTempSubdirectory("nomercy-torrent-downloader-tests-").FullName;
        HttpClient = new HttpClient(HttpHandler);
    }

    public IEventBus EventBus { get; } = new NoOpEventBus();

    public IServiceProvider Services { get; } = new NoOpServiceProvider();

    // Defaults to NullLogger.Instance, matching every real context this plugin sees when a
    // test does not care about log output. Settable so a test that DOES care - proving each
    // scheduled job reaches distinct work - can swap in a RecordingLogger without needing a
    // second IPluginContext double.
    public ILogger Logger { get; set; } = NullLogger.Instance;

    public string DataFolderPath { get; }

    public FakeConfiguration Configuration { get; } = new();

    IPluginConfiguration IPluginContext.Configuration => Configuration;

    public FakeHttpMessageHandler HttpHandler { get; } = new();

    public HttpClient HttpClient { get; }

    public Ulid PluginId { get; set; } = PluginIdentity.Id;

    public FakeSecretStore Secrets { get; } = new();

    IPluginSecretStore IPluginContext.Secrets => Secrets;

    public FakeLibraryQuery Library { get; } = new();

    IPluginLibraryQuery IPluginContext.Library => Library;

    // Settable to null: IPluginContext.LibraryWriter is only present when the plugin
    // declared LibraryWrite and the owner granted a library, neither of which this stage
    // does. Null is the default and matches every real context this plugin will ever see.
    public IPluginLibraryWriter? LibraryWriter { get; set; }

    public FakeGrants Grants { get; } = new();

    IPluginGrants IPluginContext.Grants => Grants;

    public IPluginHubContext Hub { get; } = new NoOpHubContext();

    public List<(string Name, object? Payload)> PublishedEvents { get; } = [];

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        PublishedEvents.Add((name, payload));
        return Task.CompletedTask;
    }

    private sealed class NoOpEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
            where TEvent : IEvent
        {
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IEvent
        {
            return NoOpDisposable.Instance;
        }

        public IDisposable Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : IEvent
        {
            return NoOpDisposable.Instance;
        }
    }

    private sealed class NoOpHubContext : IPluginHubContext
    {
        public Task PushAsync(string type, object? payload)
        {
            return Task.CompletedTask;
        }

        public Task PushToUserAsync(string userId, string type, object? payload)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static readonly NoOpDisposable Instance = new();

        public void Dispose() { }
    }
}
