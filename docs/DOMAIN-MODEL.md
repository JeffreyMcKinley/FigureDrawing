# Domain Model — FigureDrawing

The objects this app is made of, and the rules each one must obey. One card per object: what
identifies it, how long it lives, what it guarantees, what it is allowed to do, and what it must
never do.

Companion to [ARCHITECTURE.md](ARCHITECTURE.md) — that document says *where* code lives and which
project may reference which; this one says *what the objects mean*. Vocabulary is defined once in
[ARCHITECTURE.md §15](ARCHITECTURE.md#15-ubiquitous-language) and used here without redefinition.
Bounded contexts are [§16](ARCHITECTURE.md#16-bounded-contexts).

## How to read this

Every object carries a **status**, because this model covers both what exists and what the MVP
still needs:

| Status | Means |
|---|---|
| **Implemented** | A Core type exists and enforces the rules below |
| **Implicit** | The rules are enforced, but by a primitive or by logic spread across types — no named type owns them |
| **Proposed** | Not built. The rules are the design contract for the ticket that builds it |

Invariants are numbered (`INV-<object>-<n>`) so a ticket, test name, or review comment can cite
one. An invariant is a rule that must hold at every point an outside caller can observe the
object — not merely on entry and exit of one method.

Object kinds use the standard meanings: **Entity** has identity and changes over time; **Value
object** is immutable and compared by its contents; **Aggregate root** is the only legal entry
point to a cluster of objects; **Domain service** is stateless behaviour that belongs to no single
object; **Port** is an interface the domain defines and the platform implements.

---

## 1. Object catalogue

| # | Object | Kind | Context | Status |
|---|---|---|---|---|
| 1 | `ReferenceImage` | Value object | Reference Library | Implicit (`string`) |
| 2 | `ImageGroup` | Aggregate root | Reference Library | Implicit (one picked tree) |
| 3 | `ImagePool` | Value object | Reference Library | Implicit (`IReadOnlyList<string>`) |
| 4 | `IDocumentTree` / `DocumentEntry` | Port / value object | Reference Library | Implemented |
| 5 | `FolderImageEnumerator` | Domain service | Reference Library | Implemented |
| 6 | `SessionSetupState` | Value object | Session Setup | Implemented |
| 7 | `SessionConfig` | Value object | Session Setup → Execution | Implemented |
| 8 | `DrawingSession` | Aggregate root | Session Execution | Implemented |
| 9 | `Pose` | Value object | Session Execution | Implicit |
| 10 | `PoseCountdown` | Entity | Session Execution | Implemented |
| 11 | `SessionPlayer<TImage>` | Application service | Session Execution | Implemented |
| 12 | `SessionSummary` | Value object | Session Execution | Implemented |
| 13 | `AppSettings` | Aggregate root | Preferences | Implemented |
| 14 | `SettingsStore` | Repository | Preferences | Implemented |
| 15 | `SessionRecord` | Entity | History | Proposed |

---

## 2. Reference Library objects

### 2.1 `ReferenceImage` — *Implicit (`string`)*

One picture the artist can draw from.

| Aspect | Rule |
|---|---|
| **Identity** | The SAF content URI string. Two references are the same image if and only if the strings are equal — no normalization, no case folding, no path comparison |
| **Kind** | Value object. Immutable, compared by value |
| **Lifetime** | Outlives the app. The app holds a reference, never the bytes |
| **State** | The id, and nothing else. Dimensions, format, and orientation are decode-time facts, not domain state |

**Rules**

- `INV-IMG-1` — **The id is opaque.** No domain code parses it, splits it, sorts by it, infers a
  file name or extension from it, or logs more of it than an id. Only the Android adapter, which
  produced it, may interpret it. This is what lets tests use `"a"`, `"b"`, `"c"`.
- `INV-IMG-2` — **Existence is not guaranteed.** An id may become unreadable at any moment
  (permission revoked, file deleted, provider offline, decode failure). Every consumer must treat
  "cannot read this image" as ordinary, not exceptional.
- `INV-IMG-3` — **Format is decided by MIME, never by extension.** Anything whose MIME type starts
  with `image/` is a reference image. No hardcoded extension list. A directory is never an image.
- `INV-IMG-4` — **Never cached as decoded pixels beyond the current screen.** A decoded image is a
  rendering artifact bounded by `MaxImageDimension` (1080 px), not a domain object.

**May not** — carry a caption, rating, tag, or "seen" flag. If per-image state is ever needed, it
becomes an entity with its own store, and that is a deliberate model change.

### 2.2 `ImageGroup` — *Implicit (one picked tree)*

A named set of reference images the user draws from — today, one picked folder and everything
beneath it. The MVP has exactly one live group; the model is written so a second one costs a
list, not a redesign.

| Aspect | Rule |
|---|---|
| **Identity** | The tree URI of the picked folder. Stable across launches; that is what makes restore possible |
| **Kind** | Aggregate root over its images |
| **Lifetime** | Persisted by reference (`AppSettings.LastCollection`), never by contents |
| **State** | Root tree URI, display name, and the enumerated image ids with their encounter order |

**Rules**

- `INV-GRP-1` — **Membership is derived, never stored.** The images in a group are whatever the
  document tree reports *now*. The app never persists a list of image ids — a persisted list would
  silently rot as the user edits the folder. Re-enumerate on load.
- `INV-GRP-2` — **A group is flat.** Subfolders are traversed depth-first and their images merge
  into the one group in encounter order. Nesting is a storage detail, not a domain hierarchy. If
  per-subfolder grouping is ever wanted, that is *several* groups, decided at pick time.
- `INV-GRP-3` — **Enumeration terminates.** A provider that reports a document as its own
  descendant must not loop; visited document ids are tracked.
- `INV-GRP-4` — **A group may be empty, and empty is not an error.** Zero images shows the empty
  state, blocks Start, and does not crash.
- `INV-GRP-5` — **Access can expire.** A group is only usable while its persisted read permission
  is still held. A revoked grant is an expected outcome: fall back to the empty state, log, and let
  the user pick again. Never crash and never prompt in a loop.
- `INV-GRP-6` — **Order is enumeration order.** The group's own order is deterministic and
  provider-driven. Randomization belongs to the session, never to the group.

**Operations** — `Enumerate()` (re-walk the tree), `IsAvailable` (permission still held).

**May not** — copy, move, rename, or delete anything the user owns. The app is strictly read-only
against user storage.

**Multi-group, when it lands** *(Proposed)* — a session draws from a `Selection` of one or more
groups. Rules that must hold then: the pool is the concatenation of each group's images in group
order; duplicate ids across groups collapse to one entry (`INV-POOL-2`); removing a group from the
selection never disturbs a session already running, because the pool was copied at construction
(`INV-POOL-4`).

### 2.3 `ImagePool` — *Implicit (`IReadOnlyList<string>`)*

The ordered image ids one session may draw from. The hand-off object between Reference Library and
Session Execution.

**Rules**

- `INV-POOL-1` — **Ordered and stable.** Index order is fixed for the session's lifetime.
- `INV-POOL-2` — **No duplicates.** The same id appears at most once. Repetition within a session
  comes from passes (`INV-SES-4`), never from a pool that lists an image twice.
- `INV-POOL-3` — **May be smaller than the session's target count.** That is normal and is exactly
  what passes exist to handle.
- `INV-POOL-4` — **Copied, not aliased.** A session copies the pool at construction; later edits to
  the source list cannot change a running session.
- `INV-POOL-5` — **A pool with zero images cannot start a session.** Enforced upstream by the Start
  gate (`INV-SET-3`) and defensively downstream (`INV-SES-7`).

### 2.4 `IDocumentTree` / `DocumentEntry` — *Implemented*

The port through which the domain sees storage, and the one row type it understands.

**Rules**

- `INV-TREE-1` — **The port is the only door.** `DocumentsContract`, `ContentResolver`, `Cursor`,
  and `Uri` stop at the adapter. Nothing SAF-shaped crosses into the domain.
- `INV-TREE-2` — **`GetChildren` returns direct children only.** Recursion is the enumerator's job,
  not the adapter's, so the adapter stays trivial enough to leave untested.
- `INV-TREE-3` — **A `DocumentEntry` is `(DocumentId, MimeType?)` and nothing more.** A null or
  unknown MIME type is legal and simply is not an image.
- `INV-TREE-4` — **The adapter never throws through the port.** A failed query yields nothing.

### 2.5 `FolderImageEnumerator` — *Implemented*

Stateless domain service: walks a tree and answers "which of these are reference images". Owns
`INV-IMG-3`, `INV-GRP-2`, and `INV-GRP-3`. Holds no state between calls, so two callers can never
interfere.

---

## 3. Session Setup objects

### 3.1 `SessionSetupState` — *Implemented*

The evaluated setup screen: parsed seconds, parsed count, and whether a group is loaded.

**Rules**

- `INV-SET-1` — **Parsing is domain logic, not UI logic.** Blank, non-numeric, and non-positive
  input all evaluate to "absent". Surrounding whitespace is tolerated so a stray space cannot
  disable Start.
- `INV-SET-2` — **Both inputs must be strictly positive.** Zero is not a session.
- `INV-SET-3` — **Start requires all three:** a group with at least one image, a valid seconds
  value, and a valid count. The screen binds the button's enabled state to this and decides nothing
  itself.
- `INV-SET-4` — **Evaluation is pure and total.** Same inputs, same result; no input string throws.
  It is called on every keystroke, so it must stay cheap.
- `INV-SET-5` — **A config exists only when the setup is startable.** No partially valid config can
  be obtained.

### 3.2 `SessionConfig` — *Implemented*

`(SecondsPerImage, ImageCount)` — the validated contract handed to a session.

**Rules**

- `INV-CFG-1` — **Immutable.** A config never changes mid-session. Changing a setting starts a new
  session.
- `INV-CFG-2` — **Validated at the boundary, trusted after it.** Session Execution does not
  re-validate; it clamps defensively (`INV-SES-7`) rather than rejecting.
- `INV-CFG-3` — **It is the whole contract.** A new per-session input is a new field here and at
  both ends of the intent-extra boundary — never an out-of-band static or a settings read from
  inside the session.
- `INV-CFG-4` — **Distinct from `AppSettings`.** Config is per-session and immutable; settings are
  per-installation and mutable. Neither is derived from the other automatically: the setup screen
  copies values across explicitly, in both directions, at named moments (seed on launch, persist on
  Start).

---

## 4. Session Execution objects

### 4.1 `DrawingSession` — *Implemented*

**Aggregate root.** One run of N poses under one config. Everything about progress, ordering, and
time accounting is behind it.

| Aspect | Rule |
|---|---|
| **Identity** | None. A session is positional — one live session per player screen — so it is never stored, compared, or resumed. Giving it an id is a deliberate model change (see §7) |
| **Lifetime** | Constructed already positioned on its first image; ends at completion or `End()`. Never restarted — construct a new one |
| **State** | Upcoming queue for the current pass, current image, completed count, accumulated drawing time, complete flag |
| **Commands** | `Next()`, `Skip()`, `End()` |
| **Queries** | `CurrentImage`, `CompletedCount`, `Remaining`, `IsComplete`, `Summary`, `TargetCount`, `SecondsPerImage` |

**Rules**

- `INV-SES-1` — **Commands only.** All mutation goes through the three commands. No public setter,
  no writable field, no way to nudge the count or the clock from outside.
- `INV-SES-2` — **Counting is exact.** `CompletedCount` never exceeds `TargetCount`; `Remaining` is
  never negative.
- `INV-SES-3` — **Only `Next` counts.** `Skip` and `End` never increment the completed count. `End`
  does not count the pose in progress, because it was not finished.
- `INV-SES-4` — **Every image is shown once before any repeat.** Each pass is a fresh traversal of
  the whole pool; a new pass is built only once the previous one is drained. Shuffling, when
  enabled, is Fisher-Yates over the injected `Random`, so a seeded test is reproducible.
- `INV-SES-5` — **Drawing time excludes skipped time.** Time is banked in `Next` (a completed pose)
  and in `End` (the final partial pose). `Skip` banks nothing, so the skipped seconds are gone from
  the total.
- `INV-SES-6` — **After completion, every command is a no-op.** No exception, no state change. A
  late timer tick or a double tap cannot corrupt a finished session.
- `INV-SES-7` — **Degenerate inputs complete immediately.** An empty pool or a non-positive count
  finishes at construction rather than hanging on a null image.
- `INV-SES-8` — **Time is monotonic and injected.** A `Func<TimeSpan>` clock, defaulting to a
  `Stopwatch`. Never `DateTime.Now`; never time derived from counting UI ticks. Wall-clock changes
  and dropped repaints must not alter how much time a pose gets or how much time is banked.
- `INV-SES-9` — **`CurrentImage` is null if and only if the session is complete.** The screen may
  rely on that biconditional.

**State machine**

```
 construct ──▶ [Running] ──Next (count < target)──▶ [Running]
                  │  │
                  │  └──Skip──▶ [Running]            (no count, no time banked)
                  │
                  ├──Next (count reaches target)──▶ [Complete]  (normal completion)
                  ├──End──────────────────────────▶ [Complete]  (early end, partial time banked)
                  └──empty pool / count <= 0──────▶ [Complete]  (at construction)

 [Complete] ──any command──▶ [Complete]              (no-op, INV-SES-6)
```

**May not** — decode an image, know what a `Bitmap` is, read settings, write to storage, start a
thread, or format anything for display.

### 4.2 `Pose` — *Implicit*

One display of one image for the configured duration. It is the unit the user experiences and the
unit the summary counts, but no type carries it: the "current pose" is spread across the session's
current image and the countdown's remaining time.

**Rules the concept must obey wherever it is enforced**

- `INV-POSE-1` — **A pose ends exactly once**, in exactly one of three ways: completed (timer
  expiry or manual done-tap), skipped, or cut short by ending the session.
- `INV-POSE-2` — **A new pose gets the full configured duration.** No carry-over from the previous
  pose, ever.
- `INV-POSE-3` — **The pose clock restarts whenever the current image changes.** Today this is
  enforced by `SessionActivity.Advance` pairing `player.Next()` with `countdown.Restart()` — a
  domain rule held by a screen, and the model's one known gap. Skip (FD-006) will need the same
  pair, which is the trigger to move both into Core.
- `INV-POSE-4` — **Duration is uniform within a session.** Per-pose durations (long-pose warmups,
  ramping timers) would make `SessionConfig` carry a schedule instead of a scalar — a model change,
  not a parameter tweak.

### 4.3 `PoseCountdown` — *Implemented*

Entity owning how much time is left on the current pose, whether it is draining, and how it reads
on screen.

**Rules**

- `INV-CD-1` — **Remaining is computed from the clock, never decremented by ticks.** Remaining is
  `duration − time actually spent running`. A slow, dropped, or bursty repaint cannot make a pose
  longer or shorter.
- `INV-CD-2` — **Paused time does not count.** This is what makes backgrounding correct: a hidden
  app burns no pose time and fires no timer.
- `INV-CD-3` — **`Pause` and `Resume` are idempotent**, and `Resume` is a no-op once expired — a
  resume can never revive a dead pose.
- `INV-CD-4` — **Remaining is never negative**, and expiry is `Remaining <= 0`.
- `INV-CD-5` — **Display rounds up.** A fresh 30 s pose reads `30` immediately and reads `0` only
  once it has actually expired. Format is `m:ss`, or `h:mm:ss` past an hour.
- `INV-CD-6` — **`Restart` always resumes running** and clears all banked time.
- `INV-CD-7` — **It renders text, not views.** The countdown produces a string; the screen owns the
  repaint loop and the `TextView`.

**State machine**

```
 construct ──▶ [Running] ──Pause──▶ [Paused] ──Resume──▶ [Running]
                   │                    │
                   └──time runs out──▶ [Expired] ◀──Resume (no-op)──┘
 [any] ──Restart──▶ [Running]   (banked time cleared, full duration)
```

### 4.4 `SessionPlayer<TImage>` — *Implemented*

Application service between the session and the screen: turns the session's current image id into
something displayable and owns the unreadable-image policy.

**Rules**

- `INV-PLY-1` — **Loading is injected.** A `Func<string, TImage?>` returning null for "cannot
  read". The generic parameter is what keeps `Bitmap` out of the domain.
- `INV-PLY-2` — **An unreadable image is skipped, not counted.** It travels the skip path, so it
  neither advances the completed count nor banks time — a broken file cannot consume a slot the
  user paid for.
- `INV-PLY-3` — **Failure is bounded.** After a fixed number of consecutive failures (default 100)
  the session ends and reports "could not display" rather than looping. Necessary because a pool
  smaller than the target count repeats forever by design.
- `INV-PLY-4` — **"Could not display" is distinguishable from normal completion**, so the screen
  can show an error instead of a summary.
- `INV-PLY-5` — **The loader never throws through the player.** Decode failures are caught at the
  adapter and returned as null.
- `INV-PLY-6` — **Resolution is synchronous and re-entrant-safe.** It runs to a decision — a
  displayable image, completion, or the failure budget — before returning.

### 4.5 `SessionSummary` — *Implemented*

Immutable projection: `(ImagesDisplayed, TotalDrawingTime)`.

**Rules**

- `INV-SUM-1` — **A snapshot, not a live view.** Reading it never mutates anything.
- `INV-SUM-2` — **`ImagesDisplayed` is the completed count**, so skipped images are absent from it
  by definition.
- `INV-SUM-3` — **`TotalDrawingTime` is banked time only** — completed poses, plus the final
  partial pose when the session was ended early. It is not wall-clock session length, and the
  difference is the point.
- `INV-SUM-4` — **It is readable before completion** and simply reflects progress so far.

---

## 5. Preferences objects

### 5.1 `AppSettings` — *Implemented*

The persisted preferences document: pose duration, session image count, shuffle, grayscale, last
group.

**Rules**

- `INV-SET-P1` — **Exactly one document, forever.** Fixed identity (`Id == 1`) in one collection.
  A second document or collection needs a stated reason.
- `INV-SET-P2` — **Every property has a default**, so a first run and a missing field behave
  identically. New preferences are new defaulted properties, never a migration.
- `INV-SET-P3` — **Settings seed, they do not control.** Values are copied into the setup screen on
  launch and into intent extras on Start. Neither a session nor the setup logic reads settings at
  runtime.
- `INV-SET-P4` — **Written at named moments only** — folder picked, Start pressed. Never on every
  keystroke, and never from a background thread.
- `INV-SET-P5` — **`LastCollection` holds a group reference, not its contents** (`INV-GRP-1`), and
  a stale one is expected (`INV-GRP-5`).
- `INV-SET-P6` — **Losing it is survivable.** A deleted or corrupt database costs preferences and
  nothing else; the app must start with defaults.

### 5.2 `SettingsStore` — *Implemented*

The repository for that single document: read-with-create-on-first-run, upsert, dispose.

**Rules**

- `INV-STO-1` — **Sole owner of the database.** No other type opens a `LiteDatabase`.
- `INV-STO-2` — **Owns the document identity.** Callers never set `Id`; the store stamps it.
- `INV-STO-3` — **Disposed with the screen that opened it.**
- `INV-STO-4` — **Storage vocabulary stops here.** Nothing above the store speaks BSON, collections,
  or LiteDB types.

---

## 6. Proposed objects

### 6.1 `SessionRecord` — *Proposed*

A finished session kept for history or streaks. Not in the MVP; specified so it is not invented
ad hoc.

**Rules if built**

- Identity is a generated id assigned at completion; a session in progress has none.
- Immutable once written — a finished session is a fact, never edited.
- Stores the summary, the config used, the group reference, and a completion timestamp. Never the
  image ids: the group may have changed, and reconstructing an old sequence would be a lie.
- Timestamps enter from outside the domain, through the injected clock, exactly like every other
  time value.
- Its arrival is also the trigger for materializing domain events
  ([ARCHITECTURE.md §18](ARCHITECTURE.md#18-domain-events)) — a second consumer is the whole reason
  the events would exist.

---

## 7. Cross-object rules

These bind the objects together and are the ones most easily broken by a plausible-looking change.

**Reference and ownership**

- `INV-X-1` — Reference Library never references Session Execution. The pool flows one way.
- `INV-X-2` — Session Execution never reads `AppSettings`. Every per-session value arrives through
  `SessionConfig` or an explicit constructor argument.
- `INV-X-3` — No object holds another across a screen boundary. Screen-to-screen hand-off is intent
  extras carrying primitives, never a static, singleton, or `Application` field.
- `INV-X-4` — Nothing in the domain holds an Android object, a view, or a decoded bitmap.

**Identity and equality**

- `INV-X-5` — Value objects (`SessionConfig`, `SessionSummary`, `DocumentEntry`,
  `SessionSetupState`) are compared by value and never mutated after construction.
- `INV-X-6` — Entities with identity — `ImageGroup` (tree URI), `AppSettings` (fixed id) — are
  compared by that identity. `DrawingSession` and `PoseCountdown` have no identity and must never
  be put in a set, dictionary key, or equality check.

**Time and randomness**

- `INV-X-7` — Every time-dependent object takes an injected `Func<TimeSpan>` monotonic clock.
  `DateTime.Now` and a directly constructed `Stopwatch` inside the domain are violations.
- `INV-X-8` — Every random-dependent object takes an injected `Random`, so any sequence is
  reproducible from a seed.
- `INV-X-9` — Nothing in the domain sleeps, posts, schedules, or starts a thread. The domain is
  synchronous; the screen owns the loop.

**Failure**

- `INV-X-10` — A single bad image never ends anything but its own pose (`INV-PLY-2`); a wholly bad
  pool ends the session with a distinguishable error (`INV-PLY-3`).
- `INV-X-11` — Every failure the platform can produce — revoked permission, missing provider,
  decode error — has a defined domain outcome. "Throws" is not a defined outcome.

---

## 8. Enforcement

Each invariant above is enforced in exactly one place, and tested at the cheapest tier that can
reach it ([ARCHITECTURE.md §11](ARCHITECTURE.md#11-testing-strategy)):

| Invariant family | Enforced in | Tested by |
|---|---|---|
| `INV-IMG-*`, `INV-GRP-*`, `INV-TREE-*` | `FolderImageEnumerator`, the SAF adapter | `FolderImageEnumeratorTests` with an in-memory tree |
| `INV-SET-*`, `INV-CFG-*` | `SessionSetup`, `SessionSetupState` | `SessionSetupTests` |
| `INV-SES-*`, `INV-SUM-*` | `DrawingSession` | `DrawingSessionTests`, `DrawingSessionE2ETests` |
| `INV-CD-*` | `PoseCountdown` | `PoseCountdownTests`, `CountdownSessionE2ETests` |
| `INV-PLY-*` | `SessionPlayer<TImage>` | `SessionPlayerTests` with a fake loader |
| `INV-POSE-*` | Split — `PoseCountdown` and `SessionActivity` (see `INV-POSE-3`) | Partly untestable today; that is the gap |
| `INV-SET-P*`, `INV-STO-*` | `AppSettings`, `SettingsStore` | `SettingsStoreTests` |
| `INV-X-*` | Structural | Project references, `AndroidBuildTests`, code review |

Adding an invariant means adding a named test. Removing one means saying so in a ticket — an
invariant deleted quietly is how a domain model stops describing the code.
