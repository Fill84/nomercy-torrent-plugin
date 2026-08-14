# Reference

Data, not design. Neither is code to copy; both save a rediscovery.

- `plugin-abi-10.1.txt` - the full exported surface of `NoMercy.Plugins.Abstractions`, dumped by
  reflection. **The `10.1` in the name is wrong**: the server's `PluginAbi.Current` on `dev` is
  `10.0`, and a manifest asking for `10.1` is refused at load. The file name is left as it is
  because nothing reads it; the manifest declares `10.0`. See `docs/01-plugin.md` § Identity.
- `sources-0.3.4.json` - the previous catalogue. The addresses in it were measured working on
  13 August 2026; `docs/05-sources.md` is the specification built from them.
