# The challenge solver

The plugin has its own. It downloads a Chrome, drives it, and keeps it for the life of the server.

## What it must do

1. Fetch a page from a host that answers every plain request with a managed challenge.
2. Wait for the challenge to clear, and give up honestly if it does not.
3. Hand back the **body**, not a rendering of it.
4. Earn the clearance a signed form POST is sent with — the POST itself goes over HTTP (below).
5. Never put a window on anybody's screen.

## No window, on any platform

| Platform | How |
| --- | --- |
| Windows | a desktop of its own via `CreateDesktop`, Chrome launched with `STARTUPINFO.lpDesktop` pointing at it |
| Linux | an `Xvfb` display, `DISPLAY` set for the child process only |
| macOS | there is nowhere to put a window that is not somebody's Space. **Gated sources are skipped**, and the plugin says so |

`HiddenStages.CanHideABrowser` is the one place that decides. The stage is created **before** Chrome
starts: start it first and the window appears on the owner's desktop for the half second it takes to
move it.

## Headless is not used

Measured: headless Chrome does not pass a managed challenge. Every gated source returns the
interstitial forever.

## The browser

- Downloaded once into the plugin's data folder and reused across restarts. 0.3.4 re-downloaded it
  on every server start because it looked in the wrong place.
- The headless shell shipped alongside is deleted after download.
- One browser for the process. One tab per host, kept open and reused — clearance is issued per
  host, and two tabs on one host solve the same gate twice.

## The body, not a picture of it

A browser asked for a JSON endpoint renders it in its own viewer, and reading the DOM returns that
viewer's markup. In 0.3.4 every JSON source silently returned an empty array this way, and an XML
feed reported `malformed feed XML: The 'meta' start tag on line 1` — the viewer, not the feed.

The body is re-fetched **inside the page** with `fetch()` and the text returned.

## Clearing a challenge

- Poll until cleared, up to `SolveTimeout` (default 45 seconds).
- A navigation during the poll throws `Execution Context was destroyed`. That is the page doing what
  it is supposed to do — catch it and carry on polling. 0.3.4 logged it four times in one run as a
  source failure.
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

Browser downloaded, hidden stage created, browser started and on which port, challenge met on which
host, cleared or not and after how long, clearance kept, clearance spent — all to the activity
journal, so the dashboard can say "waiting on eztvx.to, 45s".
