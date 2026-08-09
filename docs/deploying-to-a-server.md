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

## scp works, with -O

Plain `scp` fails against this host and a plain `ssh` session works, which is
what the base64 path was written for. The cause is narrower than it looked:
OpenSSH 9 made `scp` speak the SFTP protocol by default, and this host does not
answer it. `scp -O` uses the old protocol and copies everything in one go,
including the 380 KB `Core.dll`.

The script still uses base64 because it also verifies what arrived, and at a few
hundred kilobytes the speed difference is not worth a second code path. For a
one-off copy by hand, `scp -O` is fine.

## Two things that are easy to get wrong

**Do not keep backups inside the plugins folder.** The host loads every
directory under `plugins/` as a plugin, so a copy of the previous build there
gets loaded as a second plugin with the same id. Keep them somewhere else, e.g.
`%LOCALAPPDATA%\NoMercy\plugin-backups`.

**Send `plugin.json` with the assembly.** They carry the version independently.
Updating one without the other leaves every server reporting a version it is not
running, and the update check then either nags forever or stays silent when it
should not.
