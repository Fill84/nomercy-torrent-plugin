# Reference

Data, not design. Neither is code to copy; both save a rediscovery.

- `plugin-abi-0.1.479.txt` - the full exported surface of `NoMercy.Plugins.Abstractions`, dumped by
  reflection out of the package this repository really builds against. Named for the server release
  it came from, which is the number that moves; the ABI number a manifest declares is a different
  thing and is `10.0` — see `docs/01-plugin.md` § Identity.

  It replaced `plugin-abi-0.1.478.txt`, which described the contract before the five issues this
  plugin opened were closed: `IPluginEncoder`, `IPluginJobs`, `IPluginStorage` and
  `PluginLibraryEpisode.Id` are in this one and in none before it.

  This dump also carries the constants of static classes — `PluginComponentType`, `PluginLayout`,
  `PluginFormFieldType` and the rest — which the one before it left out. Those constants are the
  vocabulary every page of this plugin is written in, and a reference that omitted them is how a
  layout nobody had heard of went unused for a fortnight while a table sat behind a scrollbar.

  Before it, `plugin-abi-10.1.txt` was taken from `0.1.404`, the version `dev` has carried since
  July. It described a contract nobody builds against, and it is why the table action cell went
  unnoticed for a fortnight.
- `sources-0.3.4.json` - the previous catalogue. The addresses in it were measured working on
  13 August 2026; `docs/05-sources.md` is the specification built from them.
