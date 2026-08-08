# This repository and the radio plugin disagree about whether a plugin form submits its fields

**From:** the agent working in `../nomercy-yt-plugin`, 2026-08-08
**Status:** a note, not a change. Nothing in this repository has been touched.
**Why it is being raised here:** if the radio plugin is right, this plugin's settings page
saves nothing and nobody finds out.

## The two claims

**This repository**, `src/NoMercy.Plugin.TorrentDownloader/Views/SettingsView.cs`, lines 26-28:

> The client interpolates CallPlugin's method straight into the request path
> (`plugins/{pluginId}/{method}`) and **posts the form's own fields as the body**, discarding
> anything else the action intent carried — a PluginForm's submit never forwards the intent's
> payload.

**The radio plugin**, `src/NoMercy.Plugin.InternetRadio/Views/SearchView.cs`, lines 10-20,
dated 2026-08-08:

> A typed field cannot be used at all. `PluginComponentType.Form` maps to `NMCard`, so a
> `PluginViews.Form` renders as a card — the a11y tree shows `role=button` with the input and
> the submit nested inside it — and there is no form element for anything to collect. **Four
> plugin-side shapes were tried and the posted body was `{}` every time**, because the term
> never leaves the browser.

Both cannot be true against the same client. The radio note is the more recent of the two and
reads as an observation ("four shapes were tried"), and that plugin also carries
`StoreSearchAsync(userId, query, rawBody, ct)` — a signature that exists to log the raw body
precisely because someone was staring at an empty one. It then abandoned typed input entirely
and spells search terms into the route one character at a time.

## What is at stake here

If the radio finding holds, then in this repository:

- `SaveSettings`, `SaveIndexer` and `SaveClient` receive a `SaveSettingsRequest` bound from an
  empty body, so every field lands as its default.
- The index riding in the route still arrives, so the *right* entry is targeted — and then
  overwritten with blanks.
- `AddIndexer`, `AddClient`, `RemoveIndexer` and `RemoveClient` are unaffected: they carry no
  body at all, and the comment in `SettingsView.cs` already records that a `PluginButton`
  dispatches its payload intact where a form does not.

That failure mode is quiet. The request succeeds, the view re-renders, and the "last saved"
line updates. `SettingsSaveHandlerTests` would not catch it either — it exercises the handler
with a request object the test constructs, which is downstream of the part in question.

## The cheapest way to settle it

One click, in a real dashboard, against a running server:

1. Open the Torrent Downloader settings page.
2. Type something recognisable into an indexer URL — `http://probe.invalid/marker` does fine.
3. Press Save.
4. Read what the controller actually received. If nothing logs it today, a one-line
   `[FromBody] JsonElement` overload or a `Request.Body` read in front of the existing binding
   is enough to see it once.

The answer is worth writing down wherever the next person looks, because it decides the shape
of every plugin UI after this one. `../nomercy-yt-plugin` cannot build its add-a-URL page
without knowing it, and has scheduled the same probe for its own M6 — if you settle it first,
that work disappears. Its design records the three outcomes and what each one costs:

| Body arrives | Consequence |
| --- | --- |
| With the fields | Ordinary `PluginViews.Form` everywhere. This repository is right. |
| Empty | Free-text input moves to a `PluginViews.WebView` page the plugin serves from its own controller — a real form on the server's own origin, so the dashboard CSP is satisfied by `'self'`. |
| Empty, and the `app-web` fix is small | Fix it upstream instead. The owner owns that client, and a form that submits its fields unblocks every plugin after this one. |

## A second, unrelated note while I was reading

`docs/superpowers/specs/2026-07-31-nm-components-research.md` is now out of date in a way that
would mislead someone acting on it. It was written against `nomercy-media-server@886a8b3` and
concludes that none of NM Components is reachable from a plugin. Against `master@9011e74` —
what `scripts/fetch-abstractions.sh` packs today, version `0.1.470` — the following all exist
in `NoMercy.Plugins.Abstractions`:

- `PluginComponentType` maps to real `NM*` tags (`NMCard`, `NMText`, `NMButton`, `NMProgress`,
  `NMBadge`, `NMEmptyState`, `NMSpinner`, `NMImage`), not to a `Plugin*` namespace.
- `PluginViews` emits `box` with spacing tokens, and builds `NMContentHeader`, `NMHelper`,
  `NMFormLabel`, `NMInput`, `NMSelect`, `NMToggle`, `NMCheckbox` and `NMFileUpload`.
- `PluginTranslations` and `PluginTranslationValidator` exist, so a plugin ships locale files
  the host validates at load.
- `PluginRouteTable`, `PluginRoute`, `PluginLayout` and `PluginSurface` exist, so pages are
  declared rather than string-matched.
- `PluginView.RefreshInterval` exists — the self-refresh that research note lists as missing,
  and which it says "changes the plugin's design most".

The section headed "none of this is reachable from a plugin today" is the part to re-check
first. The localisation gap it identifies is still real, but narrower than described: the host
localises `PluginRoute.Label` and nav-entry labels against `lang/`, and nothing else —
`PluginViewRequest` carries no locale, so the body of a view cannot be localised at all.
