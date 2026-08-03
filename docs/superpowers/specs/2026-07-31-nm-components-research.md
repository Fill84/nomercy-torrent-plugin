# NM Components — research notes and gap analysis

**Source:** `https://docs-dev.nomercy.tv/nm-components/` — 59 pages, read 2026-07-31.
**Checked against:** `nomercy-media-server@886a8b3` (dev), `nomercy-app-web@89e21aa`.

Written before implementing anything, because the headline finding is that **none of this is
reachable from a plugin today** and building against it now would produce a broken page.

---

## 1. What NM Components is

> "An NM component is something your server describes and the client draws. You send JSON, and the
> same JSON becomes a real interface on the web app, on the phone and on the television."

> "This matters most for plugins. A plugin cannot ship its own Vue app to a television, and nobody
> wants to write the same settings screen three times."

So it is explicitly aimed at the problem this plugin has. It is the right destination.

### The envelope

```json
{
  "id": "welcome",
  "component": "NMCard",
  "props": {
    "id": "welcome",
    "box": { "padding": { "all": "4" }, "gap": { "all": "3" } },
    "items": [
      { "id": "t", "component": "NMBadge",   "props": { "id": "t", "color": "teal" } },
      { "id": "s", "component": "NMSpinner", "props": { "id": "s", "size": "lg" } }
    ]
  }
}
```

Note `items` lives **inside `props`**, not beside it. That differs from the plugin contract, where
`PluginComponent.Items` is a sibling of `Props`.

### The four rules

1. **Bricks, not screens.** A card does not know it is in a grid. The container decides placement;
   the component decides its own contents.
2. **`box` is universal.** Every component accepts it. Spacing is applied *from outside* a component
   rather than by editing it. Keys seen: `direction`, `gap`, `padding`, `wrap`, `hidden_on`.
3. **Everything is a token, never a value.** No pixel lengths, no hex colours, no z-index numbers —
   named layers instead. `"padding": {"all": "4"}` is step four, resolved per platform. Rationale is
   explicit: a pixel cannot survive the trip to a platform that does not measure in pixels, and a hex
   colour is wrong in one theme and does not follow a theme switch.
4. **Accessibility ships with the component.** Roles, labels, keyboard behaviour, and a D-pad
   equivalent for television. Not optional, not the author's job.

Plus: **no user-facing string that has not been localised** — "anything you send is shown as it
arrives."

### Actions

```json
"action": { "kind": "mutate", "target": "/api/v1/settings/theme", "body": { "theme": "dark" }, "confirm": "Switch to the dark theme?" }
```

An intent naming a target, never code. **A client that does not recognise an action ignores it** —
that is what lets a newer server talk to an older client.

### Self-refresh

```json
"update": { "when": "online", "link": "/api/v1/dashboard/status" }
```

A component can refresh itself, declaratively. This is what a transfers list wants.

### Per-surface omission

```json
"box": { "hidden_on": ["tv"] }
```

Deliberately discouraged: "A dashboard that needs a lot of it is usually one that was designed for a
mouse and then sent to a remote."

---

## 2. The component inventory (55)

accordion, alert, avatar, badge, badge-group, breadcrumb, button, button-group, card, carousel, chat,
checkbox, checkbox-group, color-picker, combobox, command-palette, content-footer, content-header,
date-picker, divider, drawer, dropdown, empty-state, file-upload, form-label, helper, image, input,
link, list, metrics, modal, navigation, pagination, popover, progress, radio, radio-group, rating,
search-input, segmented, select, skeleton, slider, spinner, step-indicator, stepper, table, tabs,
tag, textarea, toast, toggle, toggles, tooltip, tree-view

Plus `box` (the placement vocabulary) and `payloads` (how to build a tree).

---

## 3. The gap: none of this is reachable from a plugin today

| | NM Components (documented) | Plugin contract (shipped, `886a8b3`) |
| --- | --- | --- |
| Tag namespace | `NMCard`, `NMBadge`, … (55) | `PluginCard`, `PluginBadge`, … (16) |
| Children | `props.items` | `Items`, sibling of `Props` |
| Placement | universal `box` with tokens | none — no spacing vocabulary at all |
| Action shape | `{kind, target, body, confirm}` | `PluginActionIntent {type, payload{method,payload,transport}, confirm}` |
| Self-refresh | `update {when, link}` | none — client refreshes only after an action |
| Per-surface | `hidden_on` | none |
| Form inputs | input, select, checkbox, radio, slider, date-picker, file-upload, combobox… | one `PluginForm` with 7 field types |

Verified in `NoMercy.Plugins.Abstractions`: no `NM*` type, no `box`, no token vocabulary.

**And the client proves it.** `app-web` has two independent renderers:

- `nmComponentMap.ts` → NM components, but only the app's own home/library set (`NMHomeCard`,
  `NMMusicCard`, `NMSeasonCard`, `NMCarousel`, …). **Not** the documented design-system set — there is
  no `NMBadge`, `NMInput`, `NMButton` in it.
- `pluginComponentMap.ts` → the 16 `Plugin*` tags. This is what the plugin host uses
  (`views/Plugins/Host/index.vue` imports `pluginComponentMap` and `PluginNode`).

`PluginNode.vue` on an unknown tag:

```
getPluginComponent(name) -> undefined
  -> renders $t('plugins.unsupported_component', { component })
```

So a plugin emitting `NMCard` today gets a page of "unsupported component" notices — by design, and
the design is good ("a plugin built against a newer server should look out of date, not broken"),
but it means **this cannot be adopted unilaterally from the plugin side.**

---

## 4. What IS actionable now, without any upstream change

Applying the NM *principles* within the vocabulary the plugin actually has:

- **Localisation.** The rule is that no unlocalised user-facing string should be sent. Every string
  this plugin sends is an English literal. Whether the plugin surface supports a localisation key at
  all needs answering — the contract has no place for one, so this may be a genuine contract gap.
- **Semantic over visual.** Already followed: `PluginBadgeVariant.Warning`, never a colour.
- **Container-decides-placement.** Partly followed by accident; the current view is a flat list of
  children in one `PluginContainer`.
- **Accessibility.** Comes from the host's components; nothing this plugin does can add or remove it.

## 5. What the plugin's UI actually needs, mapped to NM

Recorded so the port is mechanical when the contract lands, and so the upstream ask is concrete:

| Need | Today | NM equivalent |
| --- | --- | --- |
| Settings sections | `PluginText` subheadings | `NMContentHeader`, `NMDivider` |
| Indexer / client entry | one `PluginForm` per entry | `NMCard` + `NMInput` / `NMSelect` / `NMToggle` / `NMFormLabel` / `NMHelper` |
| Secret field | `PluginFormField` type `password` | `NMInput` password variant |
| Add / Remove | `PluginButton` + confirm | `NMButton`, `NMModal` for confirm |
| Save feedback | a "last saved" line, because the client discards the response | `NMToast` |
| Transfers list (Stage 4) | `PluginTable` + `PluginProgress` | `NMTable` + `NMProgress` + `update {when, link}` |
| Wanted-episode browsing | nothing | `NMList`, `NMPagination`, `NMSearchInput` |
| First-run configuration | one long form | `NMStepper` + `NMStepIndicator` |
| Validation errors | a `status`/`message` the client discards | `NMAlert`, `NMHelper` |

The `update {when, link}` capability is the one that changes the plugin's design most: a transfers
view could refresh itself instead of the plugin pushing over the hub, which would remove the reason
to declare `ws` at all.

---

## 6. Open questions for the owner

1. Is NM Components intended to replace the `Plugin*` vocabulary for plugin views, or to sit
   alongside it? The docs say it matters most for plugins, but nothing in the plugin pipeline uses it.
2. Is there a timeline? If it is close, the sensible move is to leave the current UI alone and port
   once; if it is far off, it is worth improving the current view within the 16 tags.
3. Localisation: is a plugin expected to send translation keys rather than strings? There is nowhere
   in the current contract to put one.
