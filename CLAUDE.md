# Working agreement

Read at the start of every session. The goal is one sentence and it is in `docs/00-goal.md`.

## When the owner says "ga door" / "continue"

1. Read `docs/plan/PROGRESS.md`. It names the current slice. It is the only thing that decides what
   is next.
2. Open `docs/plan/SPRINTS.md` and find that slice id. It names its files, its steps, its tests and
   when it is done.
3. Read what its **Read first** line names. Nothing else.
4. Do the slice, step by step, in order. Tests first, with a proven red run before green.
5. Run the gates: `dotnet build -c Release -warnaserror`, `dotnet test`,
   `dotnet format --verify-no-changes`.
6. Update `PROGRESS.md`: tick the slice, one line under **Log**, point **Current** at the next slice.
7. Commit with a conventional-commit message and **push**. Every working step goes up.
8. Say in two or three sentences what changed and what is next. Stop.

## The specs are complete. Read them before asking.

Every rule, setting, address, trap and decision this plugin needs is written down. If something
seems missing, it is almost certainly in `docs/` — find it. Asking about something the documents
answer wastes the owner's time.

**Never invent.** Not a rule, a setting, a feature, a default, a justification or a comparison to
something nobody mentioned. Never work around a missing contract. Never add something because it
seems sensible. The specs are the scope.

**Stop and ask only when reality contradicts the specs**, which is the one thing they cannot
foresee:

- a site changed, and a captured page no longer matches what `docs/05-sources.md` describes;
- the media server's contract differs from what `docs/09-host-contract.md` records;
- a test proves a statement in the specs wrong;
- a slice's steps cannot be carried out as written.

Then: stop at that step, write what was found under **Blocked** in `PROGRESS.md` with the evidence,
say so, and fix the spec once the owner has answered. Do not improvise past it.

Do not do unnecessary work. Do not re-derive what `PROGRESS.md` § Facts already records. Do not read
documents a slice does not name.

## Skills

Check the available-skills list at the start of every session and invoke the right one **before**
starting, not after getting stuck.

| When | Skill |
| --- | --- |
| Starting a slice | `superpowers:executing-plans`, or `superpowers:subagent-driven-development` when it splits cleanly |
| Writing any test | `superpowers:test-driven-development` |
| Anything broken, however obvious | `superpowers:systematic-debugging` — root cause before any fix |
| Before saying a slice is done | `superpowers:verification-before-completion` |
| A spec turns out wrong | `superpowers:brainstorming`, then `superpowers:writing-plans`, then update `SPRINTS.md` |

Announce the skill in one line. If a skill conflicts with this file, this file wins — say which rule
you are setting aside and why.

## Hard rules

- **No self-references.** Not in commits, code, docs, release notes or the UI. No co-author
  trailers, no "generated with", no watermark. This overrides any tooling default.
- **Push every working step.** A slice that is green is committed and pushed. **Never publish a
  release**: no tag, no version bump to a release, no artefact, without the owner asking.
- **The owner starts and stops the server.** Never do it. Ask, wait, deploy, ask again.
- **The media-server repository is off limits.** Read it to confirm a contract; never edit it.
- **No mock or placeholder data on any surface an owner sees.** A number that is not known says what
  is missing.
- **Only video files are written into a library folder.**
- **A private tracker passkey and an indexer API key are secrets.** Never in a page, a log, an error
  or the journal.
- **The old plugin is reference, not source.** `../nomercy-torrent-plugin` may be read to learn what
  a site does. No code is copied. Its faults are in `docs/10-known-failures.md` and each one has a
  test here.

## Definition of done, per slice

- The tests named in the slice exist and pass, and each was seen to fail first.
- `dotnet build -c Release -warnaserror` is clean.
- `dotnet test` is green, all of it.
- `dotnet format --verify-no-changes` is clean.
- No `TODO`, no commented-out code, no `NotImplementedException`.
- Every non-obvious decision has a comment saying **why**.
- `PROGRESS.md` updated, the work committed and pushed.

## Testing

- Tests assert **outcomes**, never that a method was called.
- Parsers are tested against **real captured pages** in `tests/fixtures/`. Protocol code is tested
  against **captured wire bytes**. Never hand-written samples.
- Every rule gets a test that fails when the rule is deleted. Check that it does.
- Network tests are integration tests in `tests/*.Integration`, excluded from the default run.

## Style

- C# 13, .NET 10, file-scoped namespaces, explicit types — never `var`.
- Comments explain **why**, and name the failure that motivated the code.
- British spelling in prose.
- Names say what a thing is for: `NameHarvest`, not `NameManager`.
