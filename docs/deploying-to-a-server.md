# Deploying a build to a running server

`scripts/deploy-to-server.sh` (or `.ps1`) copies a built plugin onto a NoMercy
server over ssh.

```sh
scripts/deploy-to-server.sh --build     # build Release, then copy
scripts/deploy-to-server.sh             # copy what is already built
SERVER=other-host scripts/deploy-to-server.sh
```

It copies the assembly, the Core assembly, `deps.json` and `plugin.json` into
`%LOCALAPPDATA%\NoMercy\plugins\NoMercy.Plugin.TorrentDownloader` on the far
side, and compares every file's hash afterwards.

## The server has to be stopped

A loaded plugin's assembly is held open by the host. Copying over it fails with
*Device or resource busy*, the old build stays in place, and the next thing you
do looks exactly like a deploy that worked and changed nothing — you test the
old code and draw conclusions from it. This costs an hour the first time and
ten minutes every time after.

So the loop is: **stop the server → deploy → start the server**. The script
compares hashes and refuses to report success on a mismatch, which is what turns
that failure from a silent one into a loud one.

## Why base64 rather than scp

`scp` against this host fails where a plain `ssh` session works, so each file is
base64-encoded, piped through `ssh` into a temp file, and decoded on the far
side. It is slower and it does not matter at a few hundred kilobytes.

## Two things that are easy to get wrong

**Do not keep backups inside the plugins folder.** The host loads every
directory under `plugins/` as a plugin, so a copy of the previous build there
gets loaded as a second plugin with the same id. Keep them somewhere else, e.g.
`%LOCALAPPDATA%\NoMercy\plugin-backups`.

**Send `plugin.json` with the assembly.** They carry the version independently.
Updating one without the other leaves every server reporting a version it is not
running, and the update check then either nags forever or stays silent when it
should not.
