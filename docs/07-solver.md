# The challenge solver

The plugin drives the hidden browser the media server provides (`IPluginContext.Browser`, media-server
#601, since `S16-06` on 20 October 2026). It used to download and hide a Chrome of its own; the
marketplace scan refused everything that took (media-server #476), and the server now does it.

## What it must do

1. Fetch a page from a host that answers every plain request with a managed challenge.
2. Wait for the challenge to clear, and give up honestly if it does not.
3. Hand back the **body**, not a rendering of it.
4. Earn the clearance a signed form POST is sent with — the POST itself goes over HTTP (below).
5. Never put a window on anybody's screen.

## No window, on any platform

The host's business (media-server `src/NoMercy.Plugins/Browser/HiddenChrome.cs`, read 20 October 2026):
on Windows a desktop of its own, on Linux an `Xvfb` display with `setsid`, and nothing anywhere else.
The host finds a Chrome at `NOMERCY_CHROME_PATH` or installed in the usual place (`Program
Files\Google\Chrome`, `/usr/bin/google-chrome`, `chromium`, `chromium-browser`); **it downloads none**,
so the owner's server needs a Chrome installed for the solver to have a browser at all. Whether there is
one is `IPluginBrowser.IsAvailable`, and why not is `UnavailableReason`, in the host's words: "Chrome is
not installed on this host.", "Xvfb is not installed on this host.", "A hidden browser window is only
available on Windows and Linux."

The facade itself is refused outright — before there is a browser to ask — only on a host that wired
none. Whether the plugin's manifest names `browser.headless` among its hooks and whether the owner has
approved that capability is checked by the host when a page is **opened** (`PluginCapabilityBroker.Check`
in `OpenAsync`), not when it says whether it has a browser: `IsAvailable` is true and the first navigation
throws `PluginRefusedException`. `HostTabs` keeps that refusal for the rest of the run — every later tab
is refused without asking, and the sentence carries the refusal — and forgets it at the run's end, so an
approval given in the meantime takes effect on the next run without a restart.

The host's page also aborts every request to a host outside the manifest's `network.hosts` and the
grants, subresources included. A managed challenge loads its frame and script from
`challenges.cloudflare.com`, so that host is in the manifest; without it no challenge could clear.

Where the browser is off (GitHub issue 6, 14 October 2026):

- The reason is logged **once** per plugin lifetime, at Warning, by `HostTabs` when a tab is asked for
  all the same; every later ask is logged at Debug. Before this, every search that met a challenge logged
  it again. In the ordinary case nothing asks: the clearance pass and the fetch read the sentence first and
  skip the solve, so the sentence reaches the activity journal and the Sources page rather than the log.
- The sentence the owner reads is the host's reason followed by what it costs: "Chrome is not installed
  on this host, so sources behind a challenge are off." (`HostTabs.Off`).
- A source behind a challenge (`SourceDefinition.NeedsABrowser`) is skipped without a solve being tried,
  unless the host already holds a clearance, which needs no browser to be read. Its failure is
  `NoBrowser` and carries the sentence. Every source that is not behind a challenge answers as usual.
- The run's clearance pass starts no browser; it ends with the sentence in the activity journal.
- The Sources page names the sources that are off and the sentence, above the table, and the sentence
  stands in the refusal column of each of those rows. Asking the host whether it has a browser starts
  nothing, so a page render may ask.

The server's Docker images ship neither `Xvfb` nor the libraries Chrome needs, and headless is not an
answer (below). NoMercy-Entertainment/nomercy-media-server#597 asks for them in the images.

## Headless is not used

Measured: headless Chrome does not pass a managed challenge. Every gated source returns the
interstitial forever.

## The browser

- The host's, one per plugin: started on the first page a plugin opens, stopped when the last page is
  disposed, killed with the plugin. Its profile is a fresh temporary folder per start, so the cookies a
  page earned live exactly as long as a page of the run does.
- A page may only go to, and fetch from, a host the manifest's `network.hosts` or a grant names
  (`BrowserNavigationBlocked` otherwise). Every gated host is in the manifest.
- One tab per host, kept for the run (`HostTabs`): clearance is issued per host, and two tabs on one
  host solve the same gate twice. A tab opens its page at its first navigation, never at the ask —
  opening a page is starting a Chrome, and the solver asks for a tab before it knows where it goes. At
  the run's end (`Chain.RunEndedAsync`) every page is disposed, which is what lets the host stop Chrome
  between runs. The plugin's own browser was kept per host for the plugin's life and sixteen Chrome
  processes were found on the owner's machine with the server stopped; the host now owns that.
- Navigation waits for `DOMContentLoaded` with the host's thirty-second timeout, and the poll below
  decides the rest: "load" waits on adverts that never arrive, and "networkidle" waits out the whole
  timeout because a challenge page keeps talking to its own endpoint.

## The body, not a picture of it

A browser asked for a JSON endpoint renders it in its own viewer, and reading the DOM returns that
viewer's markup. In 0.3.4 every JSON source silently returned an empty array this way, and an XML
feed reported `malformed feed XML: The 'meta' start tag on line 1` — the viewer, not the feed.

The body is re-fetched **inside the page** with `fetch()` and the text returned.

## Clearing a challenge

- Poll until cleared, up to `SolveTimeout` (default 45 seconds).
- A navigation during the poll is the page doing what it is supposed to do. The host's page answers a
  read made during one as a page not there yet (empty content, not loaded) rather than throwing; a
  driver that throws `Execution Context was destroyed` is caught and polled on. 0.3.4 logged it four
  times in one run as a source failure.
- One reload if it has not cleared, then give up with a sentence naming the host.
- A challenge still there after a solve is solved again and the question asked again — twice at most,
  `docs/specs/run.md`. Still there after the second solve, the site cannot be read this run, and the
  indexer round leaves it out of the rest of the run.

## Clearance

`cf_clearance` and the user agent it was issued to, kept per host and sent with plain HTTP requests
afterwards.

- Spent on refusal rather than trusted until expiry: clearance is invalidated for reasons no client
  can see coming.
- Some sites bind clearance to the TLS handshake, and replaying the cookie from `HttpClient` gets a
  403 anyway — measured. Where the solver can hand over the page itself, that is preferred.

## The signed POST

TorrentBay answers a signed request to its own endpoint, built from two values off the row and two
off the search page. **It is posted over plain HTTP, by `ChallengeAwareFetch`, in the session its
listing was read in** — through the host's gate, with the clearance cookie and the user agent it was
issued to. `ISessionPost.PostAsync(url, formBody, ct)` answers the body, or null for no grant, a
challenge, a refusal or a host that did not answer.

**It used to run in a browser tab, and that stopped working on 30 August 2026 without anybody seeing.**
The tabs were changed that day to open fresh for each task and close after it; the post was never
changed with them, so it ran `fetch` from a tab on no page of the site — another origin, none of its
cookies — and every TorrentBay row came back `Failed to fetch`. Found on 15 September 2026 by the
capture tool and confirmed live three ways: the fresh tab failed, a tab reloading the listing got no
rows, and the same request over HTTP in the listing's session named the torrent. The listing is read
over HTTP since the solver hands over a clearance, so its tokens belong to that session and not to any
browser's.

## The port

```csharp
public interface IChallengeSolver
{
    Task<Clearance?> SolveAsync(Uri url, CancellationToken ct);
}

public interface IPageSource
{
    Task<string?> GetPageAsync(Uri url, CancellationToken ct);
}

public interface ISessionPost // implemented by ChallengeAwareFetch, not the solver
{
    Task<string?> PostAsync(Uri url, string formBody, CancellationToken ct);
}
```

Three interfaces, because a chain that hides a capability makes the fetch ask "can you hand me the
page" of something that can and be told no.

## Gated hosts

Named in the catalogue, not discovered — discovering costs a guaranteed 403 before every fetch of
that host. Gating is a property of an **address**: PreDB answers its feed over plain HTTP and puts
its search behind a challenge, so a source that is not marked still reaches the solver when one of
its addresses needs it.

## What is visible

Whether the host has a browser and why not, challenge met on which host, cleared or not and after how
long, clearance kept, clearance spent — all to the activity journal, so the dashboard can say "waiting
on eztvx.to, 45s". A page opened for a host goes to the log at Debug.
