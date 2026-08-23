# Reference

Data, not design. Neither is code to copy; both save a rediscovery.

- `plugin-abi-0.1.478.txt` - the full exported surface of `NoMercy.Plugins.Abstractions`, dumped by
  reflection out of the package this repository really builds against. Named for the server release
  it came from, which is the number that moves; the ABI number a manifest declares is a different
  thing and is `10.0` — see `docs/01-plugin.md` § Identity.

  The file it replaced was named `plugin-abi-10.1.txt` and was taken from `0.1.404`, the version
  `dev` has carried since July. It described a contract nobody builds against, and it is why the
  table action cell went unnoticed for a fortnight.
- `sources-0.3.4.json` - the previous catalogue. The addresses in it were measured working on
  13 August 2026; `docs/05-sources.md` is the specification built from them.
