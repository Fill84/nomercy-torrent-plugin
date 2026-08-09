# Two things a plugin view declares that the dashboard does not render

**For:** `nomercy-app-web` (and the design system it draws with)
**From:** the torrent plugin, 2026-08-09
**Seen on:** beast-unit, contract `0.1.470`, plugin pages `/settings` and `/downloads`

Both were found by reading the rendered accessibility tree of a real page, not by reasoning about
the contract. The plugin sends what the contract asks for in both cases.

## 1. A select never shows the value it was given

The settings page declares:

```csharp
new PluginFormField
{
    Name = "maximumResolution",
    Label = "Highest quality to download",
    Type = PluginFormFieldType.Select,
    Value = "1080p",
    Options =
    [
        new PluginFormOption { Value = "720p",  Label = "Up to 720p" },
        new PluginFormOption { Value = "1080p", Label = "Up to 1080p" },
        new PluginFormOption { Value = "2160p", Label = "Up to 2160p" },
    ],
}
```

`PluginViews` puts both `value` and `options` on the `NMSelect` node, so the payload is right. What
renders is:

```
combobox:
  option "Highest quality to download" [disabled] [selected]
  option "Up to 720p"
  option "Up to 1080p"
  option "Up to 2160p"
```

The placeholder is selected and `1080p` is not. **An owner cannot see what the setting currently
is** — every select on every plugin page reads as unset, whatever is stored.

It is not data loss here, because this plugin treats a blank submission as "keep what is stored".
That is this plugin's choice, though, and a plugin that treated blank as "clear it" would silently
reset the field every time someone saved the form for an unrelated reason.

**Expected:** the option whose `value` equals the field's `value` is selected, and the disabled
placeholder is only selected when nothing matches.

## 2. A view's headings are not headings

`PluginViews.Text(id, text, "heading")` and `"subheading"` render as plain text nodes. On the
downloads page the result is:

```
heading "Torrent Downloader" [level=1]     <- the host's own, from the mount
text: "DownloadsActive"                    <- the page's heading and its first subheading
```

Two separate text nodes, adjacent, with nothing between them — so they are announced as one word.
The only real heading on the page belongs to the host, which means **a plugin page has no internal
structure for a screen reader**: no way to skip to a section, and nothing to say where "Active"
ends and "Wanted" begins.

The variant is clearly meant to carry that meaning, since it is the only thing distinguishing a
heading from body text in the contract.

**Expected:** the `heading` and `subheading` variants render an element with a heading role, nested
one level below the host's `h1`.

## Why this is worth fixing where it is

Both are one fix each in the client, and both are unfixable in a plugin: the plugin cannot pick the
tag, and it has already sent the value. Every plugin page on the platform inherits whatever these
do — the radio plugin and the YouTube plugin will each have found, or will find, the same two.
