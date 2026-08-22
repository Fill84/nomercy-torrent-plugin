# The challenge solver

The plugin has its own. It downloads a Chrome, drives it, and keeps it for the life of the server.

## What it must do

1. Fetch a page from a host that answers every plain request with a managed challenge.
2. Wait for the challenge to clear, and give up honestly if it does not.
3. Hand back the **body**, not a rendering of it.
4. Make a signed form POST from inside the session that loaded the page.
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
- A second challenge after a fresh solve is a site this plugin cannot read.

## Clearance

`cf_clearance` and the user agent it was issued to, kept per host and sent with plain HTTP requests
afterwards.

- Spent on refusal rather than trusted until expiry: clearance is invalidated for reasons no client
  can see coming.
- Some sites bind clearance to the TLS handshake, and replaying the cookie from `HttpClient` gets a
  403 anyway — measured. Where the solver can hand over the page itself, that is preferred.

## The signed POST

TorrentBay answers a signed request to its own endpoint, built from two values off the row and two
off the search page. Sent from this process it arrives without the session that earned the right to
ask and is refused, so it runs in the tab that already has the site open.

`IInPagePost.PostAsync(url, formBody, ct)` returns null when there is no solver that can — a post
certain to be refused is not worth making, and the caller can say "this site needs a browser"
instead of "this site refused us".

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

public interface IInPagePost
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
