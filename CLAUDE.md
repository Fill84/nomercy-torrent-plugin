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

### These are not blockers. They are the work.

| What happened | What to do |
| --- | --- |
| A site changed and a capture no longer matches `docs/05-sources.md` | Take a fresh capture, fix the reader against it, correct that site's paragraph, carry on. Sites move; that is the job. |
| The media server does not match `docs/09-host-contract.md` | The server is right and the document is wrong. Read the server, correct the document, carry on. |
| A test disproves a statement in the specs | The test wins if it is a real test. Correct the spec, note it under **Decisions** in `PROGRESS.md`, carry on. |
| A slice's steps cannot be carried out as written | That is a fault in the plan. Do the work the slice was for, then correct the slice so the next reader does not hit it. |

None of these needs the owner. Fix the document and keep going.

**Stop only for a decision that is the owner's to make** — a policy choice, something that changes
what the plugin is for, or an action with consequences outside this repository. Never stop for a
fact: facts are in the documents or in the media server, and both can be read.

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
- **The old plugin is reference, not source.** Nothing is carried over. The section below says
  exactly what that forbids; read it before opening `../nomercy-torrent-plugin` for the first time.

## The old plugin is reference. Nothing is carried over.

`../nomercy-torrent-plugin` is 0.3.4 — the thing being replaced. It is read to learn three things:
what a site does, what the media server accepts, and what went wrong. It is never a building block.

**Nothing is carried over. Not one file.** Not a class, a method, a regex, a settings page, a
`.csproj`, a manifest, a script, a workflow, a fixture, a JSON blob or a table of constants. Copying
the file is forbidden; so is retyping its contents into a new file, renaming it on the way in,
pasting a method and adjusting it, cloning that repository, branching from it, checking a path out
of it, or reusing its `.git`. This repository starts empty and everything in it is written here.

**"Read it" means read it.** Open the file, understand the behaviour, close it, then write this
plugin's version from the specification in `docs/`. If the specification does not say enough to
write it, then **the specification is what gets fixed** — the gap is never filled from the old code.

**Why, and it is not style.** 0.3.4's faults are structural: they are in `docs/10-known-failures.md`
and every one of them shipped *with* tests covering it. Code carried over carries its fault along,
and the test written against carried-over code passes because the fault came too. That is precisely
how those faults survived a release.

**The shortcut is the tell.** If a step feels quicker because the old plugin already has it, that
feeling *is* the copy. Slow down and write it. There is no legitimate reason starting with "the old
plugin already has".

**The only things from 0.3.4 that live here are in `docs/reference/`, and they are data.** The ABI
dump and the 0.3.4 catalogue exist so nothing is rediscovered. They are read while writing a
specification or a reader. They are never copied into `src/`, and no shipped code reads them.

## Definition of done, per slice

- The tests named in the slice exist and pass, and each was seen to fail first.
- `dotnet build -c Release -warnaserror` is clean.
- `dotnet test` is green, all of it.
- `dotnet format --verify-no-changes` is clean.
- No `TODO`, no commented-out code, no `NotImplementedException`.
- Every non-obvious decision has a comment saying **why**.
- `PROGRESS.md` updated, the work committed and pushed.

## Testing

**Write only tests that can fail for a real reason.** A test exists to prove behaviour, never to
turn the suite green. Delete the fix, run the test, watch it fail — if it still passes, the test is
worthless and writing it was worse than writing none. This is not a formality: every fault in
`docs/10-known-failures.md` § H shipped *with* tests covering it.

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
