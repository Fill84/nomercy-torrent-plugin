# A plugin cannot see Storage, so it cannot let you choose where downloads go

**For:** `nomercy-media-server`
**From:** the torrent plugin, 2026-08-09
**Checked against:** `nomercy-media-server@9011e74`, contract version `0.1.470`

## What was asked for

A download folder chosen through the server's own Storage system, configured on the plugin's
settings page the way a library's folders are — so the owner picks where downloads land instead of
typing a path.

## Why it cannot be built today

Three separate gaps, and the first two are small.

**1. The plugin contract has no storage of any kind.** `IPluginContext` offers configuration,
secrets, grants, an `HttpClient`, a library reader, a jailed library writer and a data folder.
`PluginLibrary` is `(Id, Title, Type)` — no folders, no roots, no driver. There is no way to ask
which storage drivers exist, which folders hang off them, or what a library's root is on disk.

**2. There is no folder field.** `PluginFormFieldType` has text, password, number, toggle, select,
checkbox and file. The dashboard already owns a folder browser — `FolderBrowserModal.vue`, over
`POST dashboard/filesystem/{roots,home,ls,mkdir}` — and a plugin form has no way to ask for it. So
even a plain path is typed blind, with no validation that it exists or is writable.

**3. A non-local driver is not a path at all.** This is the one worth deciding before building
anything. The torrent engine writes pieces with `FileStream` at byte offsets, because that is what
resuming a partial download means. A storage driver that is not a local disk has no byte offsets to
write to, so "downloads go to this storage folder" cannot mean the engine writes there directly. It
would mean downloading locally and handing the finished file to the facade — which is what the
intake handoff already does, one `MoveIntoIntakeAsync` away from being exactly that.

## The smallest thing that would work

Two additions, in this order of usefulness:

**A folder field type.** `PluginFormFieldType.Folder`, rendered with the folder browser the
dashboard already has, posting back the chosen absolute path. This alone fixes the real complaint:
the owner picks instead of typing, and cannot pick something that does not exist. It needs nothing
from the storage layer.

**A read-only storage view.** Something like:

```csharp
public interface IPluginStorage
{
    Task<IReadOnlyList<PluginStorageFolder>> GetFoldersAsync(CancellationToken ct = default);
}

/// <param name="Driver">Which storage driver holds it — "local" is the one a plugin can write to directly.</param>
public record PluginStorageFolder(string Id, string Path, string Driver, string? LibraryId);
```

That is enough for a settings page to offer the folders the server already knows about, and enough
for a plugin to refuse politely when the owner picks a folder on a driver it cannot stream into.

## What this plugin does in the meantime

`IncompleteFolder` and `IntakeFolder` stay free-text paths, defaulting to the plugin's own data
folder. They work, they are just typed rather than chosen, and nothing checks them until the first
download tries to write.

The intake handoff is deliberately the only place a finished file crosses into the server's world.
If storage arrives later and it turns out downloads should be completed locally and then handed to
a driver, that handoff is the one method that changes.
