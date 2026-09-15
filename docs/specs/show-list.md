# The show list

## The overview page

- The plugin's overview page lists every show of every `tv` library and every anime of every `anime`
  library, in one list per library.
- Every show and every anime on the list has its own on/off switch.
- A show or anime nobody has switched on is off.
- Only a show or anime is switched on or off. No switch turns a whole library on or off.
- The settings for a show or anime, and each library's preferences, are set on the overview page.
- The plugin's settings page holds no quality, codec or tag setting.

## Switching a show on

- A show or anime is searched for and downloaded only when it is switched on and its settings are
  saved.
- A show switched on whose settings have never been saved has nothing searched and nothing
  downloaded.
- A show switched off has nothing searched and nothing downloaded, and its saved settings are kept.

## The settings of a show

Every show and anime has these settings:

- **Quality**: one resolution, chosen from 2160p, 1080p, 720p and 480p.
- **Codec**: one codec, chosen from any, h264, h265, xvid and divx.
- **Wishes**: a list of tags a release name preferably carries.
- **Musts**: a list of tags every downloaded release name carries, all of them.
- **Forbidden**: a list of tags no downloaded release name carries.
- **Specials**: on or off. With specials off, no episode of season 0 is searched for.

- Each tag list is filled in as comma-separated text or one tag at a time, appended to the list.
- A tag is any word a release name can carry: a language such as `DUAL` or `VOSTFR`, a source such
  as `WEB`, a group, or anything else.
- A wish that no release name carries leaves the show downloadable: release names without that wish
  are taken instead.
- A release name carrying a forbidden tag is never downloaded, whatever else it carries.

## Library preferences

- Each library (`tv` and `anime`) has preferences of the same shape: quality, codec, wishes, musts,
  forbidden and specials.
- A library's specials are off until the owner switches them on.
- A show's specials are those of its library until the show sets its own.
- A library's quality is the resolution of the encoding profile of the library's folders when the
  server tells the plugin that profile, and otherwise the quality the owner sets for the library.
- A show's quality and codec are those of its library until the show sets its own.
- A show whose quality is set neither by the show nor by its library has nothing searched and nothing
  downloaded.
- The plugin's settings page holds no English-only setting. A language is a tag: `DUAL`, `VOSTFR` or
  any other language tag goes into a show's or a library's wishes, musts or forbidden.
- A show's wishes, musts and forbidden are the library's lists together with the show's own lists.
- A tag that the library and the show put in different lists counts only in the show's list.
