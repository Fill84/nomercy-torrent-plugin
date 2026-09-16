# The show list

## The overview page

- The plugin's overview page lists every show of every `tv` library and every anime of every `anime`
  library, in one list per library.
- A show or anime the library holds no video file of is not listed, unless it is switched on. The
  server keeps a row for every show it ever identified, and those are not the owner's shows.
- The missing column says, for every show listed, how many of its aired episodes have no video file,
  whether the show is switched on or off. It counts season 0 only where that show takes specials.
- Every show and every anime on the list has its own on/off switch.
- A show or anime nobody has switched on is off.
- Only a show or anime is switched on or off. No switch turns a whole library on or off.
- The settings for a show or anime, and each library's preferences, are set on the overview page.
- The plugin's settings page holds no quality, codec or tag setting.
- The overview page opens with the run's status line: whether a run is going, when the last one
  ended, when the next one starts, and the Run and Stop buttons.
- Each library's block shows its preferences on one line with an Edit button, and a table of its
  shows and anime with the columns show, year, missing, quality, codec, tags and state.
- A row's state is On, Off, or On and not saved.
- Every row carries a button that switches the show on or off and a button that opens its settings.
- A library's table shows 50 rows per page, with the shows that are switched on first and each group
  in alphabetical order, and Previous and Next buttons.

## The settings form

- A show's settings are one form with one Save button: on or off, quality, codec, specials, wishes,
  musts and forbidden.
- Quality, codec and specials each offer the choice of following the library, next to their own
  values.
- Wishes, musts and forbidden each have a field holding the list as comma-separated text and a field
  for one tag, which Save appends to the end of that list.
- Below the form the page shows the settings that apply to the show with its library's preferences
  counted in.
- A library's preferences are the same form without the choice of following the library.
- A library quality taken from the encoding profile is shown as a fixed value.

## Switching a show on

- A show or anime is searched for and downloaded only when it is switched on and its settings are
  saved.
- A show switched on whose settings have never been saved has nothing searched and nothing
  downloaded.
- A show switched off has nothing searched and nothing downloaded, and its saved settings are kept.
- A download of a show that has nothing searched — switched off, never saved, or without a quality —
  is cancelled and what it downloaded is deleted, unless its episode is already staged for the
  encoder. This holds as well for downloads running when the plugin is updated to the show list.
- Whether a show has a video file on disk plays no part in whether it is searched for.

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
- A library's codec is any until the owner chooses another.
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
