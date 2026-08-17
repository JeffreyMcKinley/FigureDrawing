# Architecture — FigureDrawing

How this codebase is organized and the rules changes must hold to. Written for agents and
contributors reviewing or extending the app. Product scope lives in the root [README](../README.md);
per-story work lives in [docs/tickets](tickets/README.md).

> Derived from the code as of FD-005. Where a rule below is marked **(confirm)** it was inferred
> from existing code rather than stated anywhere — correct it if the intent differs.

## 1. The one rule

**All logic that can be written without Android goes in `FigureDrawing.Core`. The Android layer
only wires it to views.**

Everything else in this document follows from that. Core is a plain `net9.0` library with no
Android reference, so every rule in it is reachable by a fast unit test on the desktop runner.
The Android projects need a device or emulator, so anything that lands there is expensive to
verify and stays deliberately thin.

## 2. Projects

| Project | TFM | Role |
|---|---|---|
| `FigureDrawing.csproj` (repo root) | `net9.0-android` | Android app: Activities, layouts, resources, decoding |
| `FigureDrawing.Core` | `net9.0` | Session engine, setup validation, folder enumeration, settings persistence, bitmap math |
| `FigureDrawing.Tests` | `net9.0` | xUnit unit, E2E-model, and contract tests. No device needed |
| `FigureDrawing.UITests` | `net9.0` | Appium UI tests. Needs a running emulator |

Dependency direction, and the only direction allowed:

```
FigureDrawing (Android)  ──▶  FigureDrawing.Core  ──▶  LiteDB
      │                              ▲
      │                              │
FigureDrawing.UITests          FigureDrawing.Tests
   (via Appium, black box)        (direct reference)
```

**Rules**

- `FigureDrawing.Core` must never reference `Mono.Android`, `Android.*`, or `Java.*`. If a Core
  type needs a platform concept, it takes an abstraction or a delegate instead (see §4).
- The app project references Core. Core never references the app.
- Nothing references the test projects.
- LiteDB is a transitive dependency of the app via Core — do not add a direct `PackageReference`
  for it to the app project.

**Root-glob gotcha.** The app csproj sits at the repo root, so its default `**/*.cs` glob would
swallow the sibling projects' sources. The three `<Compile Remove>` entries in
`FigureDrawing.csproj` are load-bearing; `AndroidBuildTests` guards them. Adding a new sibling
project means adding a fourth exclude.

## 3. Layers

### Core — pure model

| Type | Owns |
|---|---|
| `SessionSetup` / `SessionSetupState` / `SessionConfig` | Parsing and validating the two setup inputs; the Start gate |
| `DrawingSession` | The session: image sequence, shuffle, counts, skip semantics, elapsed-time accounting |
| `SessionPlayer<TImage>` | Resolving the current image id to something displayable; skipping unreadable images |
| `PoseCountdown` | Per-pose remaining time, pause/resume, display formatting |
| `PoseSession<TImage>` | The player aggregate: pose/break/complete state machine over session + player + countdown |
| `ViewerTools` | Grayscale/flip/grid/blur flags and the zoom range for the pose on screen |
| `FolderImageEnumerator` / `IDocumentTree` / `DocumentEntry` | Recursive image discovery under a picked folder |
| `BitmapMath` | Power-of-two sub-sample calculation |
| `Data/AppSettings` / `Data/SettingsStore` | The persisted settings document and its LiteDB store |

Core types are deterministic and side-effect free apart from `SettingsStore`. They expose state as
properties for the screen to read (`CurrentImage`, `Remaining`, `IsComplete`, `Display`) and accept
commands as methods (`Next`, `Skip`, `End`, `Pause`, `Resume`, `Restart`).

### Android — screens only

| Type | Owns |
|---|---|
| `MainActivity` | The three tabbed panes: setup inputs, the reference library + folder picker (SAF), settings |
| `SessionActivity` | The player screen: pose, rail, break/pause overlays, summary, the repaint loop, lifecycle |
| `ImageDecoding` | Two-pass `BitmapFactory` decode shared by both screens |

The look is the **Nocturne** design system, imported from the Claude Design project *Figure Drawing
Practice App*. Its tokens live in `Resources/values/colors.xml` + `dimens.xml`, and its component
classes (`.tool-chip`, `.btn-*`, `.card`, `.input`, the tab bar) are the widget styles in
`Resources/values/styles.xml`. Retune the system there rather than styling a control inline; a
one-off `android:background` on a button is the same kind of violation as a rule in an Activity.

**Typeface.** The system's font, Inter, is bundled at `Resources/font/` (weights 400/500/600, the
same three the design system imports) under the SIL Open Font Licence — see
`docs/third-party-licenses/`. It is what puts the app's minimum at **API 26**: framework font
resources arrived in Oreo. It reaches views through the theme's `android:textAppearance` and
`android:textAppearanceButton`, *not* through a bare `android:fontFamily` item on the theme —
`TextView` never reads that off the theme, so setting it there looks right and does nothing.
`EditText` is the one exception and names the family in the `Input` style, because the platform's
`Widget.Material.EditText` pins its own text appearance. `TypefaceContractTests` guards all of it.

An Activity is allowed to: find views, read intent extras, subscribe to view events, call Core,
render Core's state, and manage its own lifecycle. It is not allowed to own a rule.

**(confirm)** There is no MVVM/MVP framework here and no ViewModel layer — screens talk to Core
objects directly. That is the intended shape at this size; introducing a presenter layer is a
deliberate decision, not a drive-by refactor.

## 4. Crossing the boundary

Core never imports Android. Where it needs something the platform provides, it takes it as a
constructor parameter. Three patterns already in use — reuse them rather than inventing a fourth:

**Interface adapter.** `IDocumentTree` describes "list the children of a folder document".
`MainActivity.ContentResolverDocumentTree` backs it with `DocumentsContract` + `ContentResolver`;
tests back it with an in-memory tree.

**Injected delegate.** `SessionPlayer<TImage>` takes `Func<string, TImage?> load`. The screen
passes `LoadBitmap`; tests pass a fake. The generic parameter is what keeps `Bitmap` out of Core.

**Injected clock.** `DrawingSession` and `PoseCountdown` both take an optional
`Func<TimeSpan>? clock`, defaulting to a `Stopwatch`. Tests drive time deterministically. Any new
time-dependent Core type must follow this — never call `DateTime.Now` inside Core.

`Random` follows the same rule: `DrawingSession` takes an optional `Random` so shuffles are
reproducible under test.

## 5. State and navigation

- **Session state** lives in the single `PoseSession<Bitmap>` owned by `SessionActivity`, created in
  `OnCreate` from intent extras (it composes `DrawingSession` / `SessionPlayer` / `PoseCountdown`).
- **Screen-to-screen handoff** is intent extras only. `SessionActivity` declares its keys as public
  constants (`ExtraPool`, `ExtraSeconds`, `ExtraCount`, `ExtraBreak`, `ExtraShuffle`,
  `ExtraGrayscale`, `ExtraKeepAwake`, `ExtraChime`); `MainActivity.StartSession` fills them. Add a new input by adding a constant, not a string literal
  at the call site, and not a static or singleton.
- **Persisted state** is the single `AppSettings` LiteDB document. It seeds the setup inputs on
  launch and records the last folder.
- **(confirm)** Session state is *not* currently saved in `OnSaveInstanceState`, so process death
  restarts the pose. That is a known gap, not a pattern to copy. `SessionActivity` mitigates the
  common case by declaring `ConfigurationChanges` for size/orientation and re-laying out in place,
  so folding or unfolding mid-session keeps the pose.

## 6. Data access

- LiteDB is reached only through `SettingsStore`. No other type opens a `LiteDatabase`.
- The database file lives in app-private storage: `Path.Combine(FilesDir.AbsolutePath, "figuredrawing.db")`.
- `SettingsStore` is `IDisposable` and is disposed in `OnDestroy`. It is a single-document store —
  `DocumentId == 1` in the `settings` collection.
- New preferences are new properties on `AppSettings` with a default value. Do not add a second
  document or a second collection without a reason.

## 7. Threading

- Everything in the app today runs on the main thread. Core is synchronous by design.
- Android UI objects may only be touched on the main thread. `SessionActivity`'s repaint loop uses
  `Handler(Looper.MainLooper)`, so it already is.
- The repaint `Handler` posts and removes **one stored `Java.Lang.IRunnable`**. Posting an `Action`
  wraps it in a fresh Java `Runnable` each call, so `RemoveCallbacks` would never match and ticks
  would survive teardown. Keep the stored-runnable pattern.
- Countdown time comes from a monotonic clock, never from counting ticks. A slow or dropped repaint
  must not change how much time a pose gets.
- **(confirm)** If background work is introduced — decoding off the UI thread is the obvious first
  candidate — it belongs behind an `async` method in the Android layer, with results marshalled back
  via the existing main-looper `Handler`, and it must be cancelled in `OnPause`/`OnDestroy`.

## 8. Lifecycle and resources

- `OnPause` freezes the pose clock and stops the repaint loop; `OnResume` restores both. A
  backgrounded app must not burn pose time or fire a timer while hidden.
- `OnDestroy` stops the loop, clears `KeepScreenOn`, and disposes anything it owns.
- Every queued callback must be removable, and every listener attached to a long-lived object must
  be detached.
- Images are always decoded through `ImageDecoding.DecodeSampledBitmap`, bounded to
  `MaxImageDimension` (1080 px). Never call `SetImageURI` or decode at full resolution — a folder of
  real photos will exhaust memory.
- A single unreadable image must never sink the screen: decode failures return `null` and are
  logged, and `SessionPlayer` skips past them with a bounded failure budget.

## 9. Errors and logging

- All logs go through `Android.Util.Log` with the tag constant `LogTag = "FigureDrawing"`.
- Anything crossing the system boundary — SAF results, URI permission grants, image decoding — is
  wrapped in `try`/`catch`, logged, and turned into a visible message rather than a crash.
  `MainActivity.OnActivityResult` is the reference example.
- Catching broad `Exception` is acceptable at those boundaries, and only there. Elsewhere, catch the
  specific type or let it throw.
- Never log image contents or full user file paths beyond the content URI already logged.

## 10. UI resources

- Layouts are `Resources/layout/activity_*.xml`, strings are `Resources/values/strings.xml`.
- Views are resolved by `FindViewById<T>(Resource.Id.x)!` in `OnCreate` and stored in `null!` fields.
- **User-facing text always comes from `strings.xml`** via `GetString(Resource.String.y)`. No string
  literals in an Activity.
- Because these lookups are by name at runtime, a rename compiles fine and crashes on device. The
  contract tests in §11 exist to catch exactly that — a new view id or string must be added to them.

## 11. Testing strategy

Three tiers, cheapest first. Prefer the cheapest tier that can catch the bug.

**Unit tests** (`FigureDrawing.Tests`, one file per Core type) — the default. Everything in Core is
covered here, with injected clock/`Random`/loader making them deterministic.

**Contract tests** (`UiResourceContractTests`, `SessionScreenContractTests`, `AndroidBuildTests`) —
a pattern worth understanding before touching the Android layer. They parse the *source and XML as
files* rather than running them, so they need no device but still catch the runtime-only failures
that Xamarin's compile-time checks miss: a view id referenced from code but absent from the layout,
a missing string, a build property regression. `TestPaths` locates the repo root by walking up to
`FigureDrawing.sln`, since the working directory differs between `nx` and `dotnet test`.

**E2E-model tests** (`*E2ETests.cs`) — drive the Core objects through a whole session in one test,
without Android. They cover interaction between engine, player, and countdown.

**UI tests** (`FigureDrawing.UITests`) — Appium against a real emulator. Slow and last resort; use
only for behavior genuinely unreachable from Core. Run them with `scripts/run-appium-tests.ps1`,
which is the only supported entry point: it installs the toolchain, boots the emulator, builds and
installs a self-contained APK, resets app + picker state, and manages the server.

Three rules the harness depends on, each learned from a failure that looked like an app bug:

- **One session per device.** The UiAutomator2 driver installs a single instrumentation on the
  device and force-stops any running instance when a session starts, so two concurrent sessions kill
  each other and every test fails with "The instrumentation process cannot be initialized". Every
  UI-test class therefore carries `[Collection(AppiumCollection.Name)]` — one shared session, classes
  run sequentially — with `DisableTestParallelization` as the backstop.
- **Each test owns its starting state.** The suite shares one app install, so a test that picks a
  folder leaves that choice persisted for every test after it. Anything asserting first-run
  behaviour calls `UiTestEnvironment.ResetAppState()` itself rather than trusting run order.
- **Navigate by Activity, not by package.** Setup/Images/Settings are panes of `MainActivity` while
  the player is a separate Activity, so "press Back until the package is ours" is already satisfied
  on the player screen and never reaches the tabs. `AppiumGuard.ReturnToMainScreen` keys off the
  current *Activity* instead.
- **The picker must be walked into a subfolder.** Android refuses to grant the root of shared
  storage through `ACTION_OPEN_DOCUMENT_TREE` — it shows "Can't use this folder" and offers no
  confirm button — and that root is exactly where DocumentsUI opens with no history.
  `AppiumGuard.SelectDefaultFolder` taps the seeded folder by name before confirming.

Rules:

- New Core type ⇒ new unit test file.
- New view id or user-facing string ⇒ add it to the contract tests.
- Logic that is hard to unit test is a design smell — that is the signal to move it into Core, not
  to write a UI test for it.

## 12. Build and run

- Run tasks through Nx, not the underlying tooling: `./nx.bat run FigureDrawing.Tests:test`.
- `Directory.Build.props` supplies the Android SDK and JDK 17 paths (Microsoft.Android 35 rejects
  JDK 25). Every rule there is guarded so it never overrides an explicit choice.
- One-command run: `dotnet build FigureDrawing.csproj -t:RunEmulator`, which delegates to
  `scripts/run-app.*`. Both the MSBuild target and a manual run go through the same script — keep it
  that way.
- The full Android compile test is opt-in via `RUN_ANDROID_BUILD_TEST=1`.

## 13. Adding a feature — the checklist

1. Write the rule as a pure type in `FigureDrawing.Core`, taking any platform need as an
   abstraction or delegate.
2. Unit test it in `FigureDrawing.Tests`.
3. Wire it into an Activity: find views, read Core state, render, forward events back.
4. Add any new view id or string to `strings.xml` / the layout **and** to the contract tests.
5. Pause and resume it correctly in the lifecycle if it involves time or callbacks.
6. Run `./nx.bat run FigureDrawing.Tests:test`.

## 14. Anti-patterns

Findings against any of these are architecture violations, not style opinions.

- A rule, calculation, or state machine implemented inside an Activity.
- `Android.*` / `Java.*` referenced from `FigureDrawing.Core`.
- `DateTime.Now`, `Stopwatch`, or `new Random()` used directly inside Core instead of the injected
  clock/`Random`.
- A `LiteDatabase` opened outside `SettingsStore`.
- A hardcoded user-facing string in an Activity.
- Screen-to-screen data passed via a static, singleton, or `Application` field instead of intent extras.
- A `Handler` callback posted as an `Action` and expected to be removable.
- Bitmap decoding that bypasses `ImageDecoding.DecodeSampledBitmap`.
- A new view id or string added without a corresponding contract-test entry.
- A UI test written for behavior that could have been reached from Core.

---

# Part II — Domain model (DDD view)

Sections 1–14 describe the *physical* architecture: which project a type lives in and what it may
reference. Sections 15–21 describe the *domain* architecture: what the app is about, which
concepts belong together, and which rule each type is allowed to own. The per-object rules and
numbered invariants live in a companion document, [DOMAIN-MODEL.md](DOMAIN-MODEL.md). The two views must agree —
a bounded context that cannot live inside `FigureDrawing.Core` is a modelling mistake, not a
reason to put a rule in an Activity.

Derived from the code as of FD-005, following the `v3-ddd-architecture` skill. Two of that
skill's prescriptions are **deliberately not adopted**: the microkernel/plugin runtime and a
dependency-injection container. This is a single-user offline app of ~1,200 lines with no
extension points and no third-party modules; a kernel registry would add indirection without
removing a rule. Constructor injection by hand (§4) already gives the same inversion.

## 15. Ubiquitous language

The words below are the only correct names for these concepts. Code, tickets, log messages, and
strings use them consistently; a synonym in a new type name is a review comment.

| Term | Means | Not |
|---|---|---|
| **Reference image** | One picture the artist draws from, identified by a SAF content URI string | "photo", "file" |
| **Pool** | The ordered set of reference images discovered under the picked folder | "gallery", "album" |
| **Pass** | One traversal of the whole pool; a new pass reshuffles. Pools smaller than the target count repeat by passes | — |
| **Pose** | One display of one reference image for the configured duration | "slide", "frame" |
| **Session** | A run of N poses under one config, from Start to completion or early end | "workout", "practice" |
| **Setup** | The pre-session screen state: folder + seconds-per-image + count, and whether Start is allowed | "config screen" |
| **Config** | The validated `(SecondsPerImage, ImageCount)` pair handed to a session | "settings" (that's persistence) |
| **Complete** | A pose that counted toward the target — timer expiry or a manual done-tap | "finished" (ambiguous with session end) |
| **Skip** | Leaving a pose without counting it and without banking its time | "next" |
| **Break** | The configured rest between two poses. Never before the first or after the last | "pause" (that's the drawer stopping the clock) |
| **Viewing aid** | Grayscale, flip, grid, blur, zoom — how the pose is presented, never what it counts | "filter", "effect" |
| **Drawing time** | Accumulated time over completed poses only; skipped time is never banked | "elapsed", "session length" |
| **Unreadable** | An image id the loader could not decode; skipped and logged, never fatal | "corrupt", "missing" |
| **Settings** | The persisted user preferences document that *seeds* setup across launches | "config" |

Two distinctions carry real invariants and are worth stating twice:

- **Config vs Settings.** `SessionConfig` is validated, immutable, and scoped to one session.
  `AppSettings` is persisted, mutable, and scoped to the installation. Settings seed setup; setup
  produces config; config never writes back to settings implicitly.
- **Complete vs Skip vs End.** Three different terminal moves on a pose, with three different
  effects on `CompletedCount` and `TotalDrawingTime`. See the invariant table in §17.

## 16. Bounded contexts

Four contexts, each a cohesive vocabulary with its own rules. All four live in
`FigureDrawing.Core`; the Android layer holds only their adapters and screens.

| Context | Owns | Core types today | Namespace / folder |
|---|---|---|---|
| **Reference Library** | Discovering drawable images under a picked folder; what counts as an image | `FolderImageEnumerator`, `IDocumentTree`, `DocumentEntry` | `FigureDrawing.Core` (root) |
| **Session Setup** | Parsing and validating the two inputs; the Start gate; producing a config | `SessionSetup`, `SessionSetupState`, `SessionConfig` | `FigureDrawing.Core` (root) |
| **Session Execution** | Running a session: sequence, passes, counts, skip semantics, time accounting, per-pose countdown, breaks, resolving an id to a displayable image, the viewing aids | `DrawingSession`, `SessionPlayer<TImage>`, `PoseCountdown`, `PoseSession<TImage>`, `ViewerTools`, `SessionSummary` | `FigureDrawing.Core/Session` |
| **Preferences** | The persisted settings document and its lifecycle | `AppSettings`, `SettingsStore` | `FigureDrawing.Core/Data` |

Supporting, deliberately outside the four: **Rendering** (`ImageDecoding`, `BitmapMath`, the
`ImageView` wiring). It has no domain rules — only the memory-bound decode policy of §8. It is a
shared technical service, not a context, and `BitmapMath` is the one piece of it pure enough to
live in Core.

### Context map

```
        Preferences  ──seeds──▶  Session Setup  ──SessionConfig──▶  Session Execution
             ▲                        ▲                                    ▲
             │                        │                                    │
        (last folder)          folderSelected                            pool
             │                        │                                    │
             └────────────  Reference Library  ─────────────────────────────┘
                                     ▲
                                     │ IDocumentTree (anti-corruption layer)
                                     │
                          Storage Access Framework (Android)
```

Relationship types, using the standard names, because each one implies a different rule for
change:

- **Session Setup → Session Execution — customer/supplier, published language.** `SessionConfig`
  is the contract. Execution never re-validates it and never reaches back into setup state. A new
  session input is a new field on `SessionConfig`, added at both ends in one change.
- **Reference Library → Session Execution — supplier.** The pool crosses as
  `IReadOnlyList<string>`. Execution treats ids as opaque: it never parses, sorts by, or
  interprets a content URI. That opacity is what lets tests pass `"a"`, `"b"`, `"c"`.
- **Storage Access Framework → Reference Library — anti-corruption layer.** `IDocumentTree` +
  `DocumentEntry` are the ACL. `DocumentsContract`, `ContentResolver`, and `Cursor` stop at
  `MainActivity.ContentResolverDocumentTree`. Nothing SAF-shaped may cross into Core — that is
  §4's interface-adapter pattern stated as a context rule.
- **Preferences → Setup — open host, one direction.** Settings seed the inputs and record the last
  folder. Neither Session Setup nor Session Execution reads `AppSettings` at runtime; the Android
  layer copies the values it needs into intent extras at launch (§5).
- **Screen → screen — serialization boundary.** Intent extras are the wire format between
  `MainActivity` and `SessionActivity`. They carry primitives only, keyed by the public constants
  on `SessionActivity`. That boundary is why a `SessionConfig` is reconstructed on the far side
  rather than passed as an object.

## 17. Tactical model

### Session Execution

**Aggregate root: `DrawingSession`.** It is the consistency boundary for everything about the run
— the upcoming queue, `CompletedCount`, and `_accumulatedDrawingTime` are only ever mutated
through `Next` / `Skip` / `End`, and no caller can observe them mid-transition. Its identity is
positional (one live session per player screen), so it carries no id: a session is never stored,
never compared, and never resumed. That is the reason it has no repository, and adding one would
be the signal that the model changed, not a convenience.

| Element | Kind | Note |
|---|---|---|
| `DrawingSession` | Aggregate root, entity | Owns sequence, counts, drawing time |
| `SessionConfig` | Value object (`readonly record struct`) | Immutable, validated upstream |
| `SessionSummary` | Value object | Snapshot projection for the summary screen (FD-007) |
| `PoseCountdown` | Entity | Owns remaining time, run/pause state, display format |
| `PoseSession<TImage>` | Aggregate | Composes the three above; owns the pose/break/complete state machine |
| `ViewerTools` | Entity | Owns the viewing aids and the zoom range; touches nothing the session counts |
| `SessionPlayer<TImage>` | Application service | Resolves ids to images, applies the unreadable-skip policy |
| image id (`string`) | Primitive standing in for a value object | See "candidate: `ImageRef`" below |

**Invariants the aggregate enforces.** These are the rules a change must not break; each has a
test in `DrawingSessionTests` / `DrawingSessionE2ETests`.

| Invariant | Enforced by |
|---|---|
| `Remaining` is never negative; `CompletedCount` never exceeds `TargetCount` | `Next` finishes at the target; `Remaining` clamps |
| Every image is shown once before any repeat | `Refill` rebuilds a full pass before dequeuing |
| Skip never advances `CompletedCount` and never banks time | `Skip` calls `Advance` only |
| `End` banks the current partial time but does not count the pose | `End` accumulates, then `Finish` |
| Drawing time excludes all skipped time | Time is banked only in `Next` and `End` |
| Drawing time excludes break, background and paused time | The session clock stops in `Pause`; `PoseSession` stops it for the whole break |
| Every operation is a no-op once `IsComplete` | Guard at the top of `Next` / `Skip` / `End` |
| An empty pool or a zero count completes immediately rather than hanging | `Advance` finishes when `_targetCount <= 0 \|\| _pool.Count == 0` |
| Time is monotonic and injectable | `Func<TimeSpan> clock`, never `DateTime.Now` (§4) |
| A skip raises `SkippedCount` and nothing else | `Skip` increments, then `Advance` |
| A break never counts a pose, and never follows the last one | `PoseSession.CompletePose` finishes at the target before entering a break |
| A skip lands on the next pose, never on a break | `PoseSession.Skip` calls `StartPose` |

**`PoseCountdown` is a second entity, not part of the root.** It is deliberately split out: the
countdown has its own lifecycle (pause/resume from the Android lifecycle) that the session does
not care about, and the session's time accounting is independent of what a pose *displays*.

**Closed: the pose-restart rule.** "The pose clock restarts whenever the current image changes" used
to be enforced by `SessionActivity.Advance` (`player.Next(); countdown.Restart();`) — a state machine
inside an Activity, and one the skip control would have had to repeat. It now lives in
`PoseSession<TImage>`, which composes session + player + countdown and exposes
`Next` / `Skip` / `End` / `Tick` / `Pause` / `Resume`, leaving the Activity with repaint, rendering
and lifecycle. Adding the between-poses break is what forced the issue: the pairing became a
three-state machine (pose → break → pose), which is not something a screen may own.

`PoseSessionTests` asserts the pairing directly, and `SessionScreenContractTests` asserts the
negative — that `SessionActivity` no longer constructs or restarts a `PoseCountdown` itself.

**Candidate: `ImageRef` value object.** Image ids are bare `string`s throughout. A one-field
`readonly record struct ImageRef(string Value)` would make "opaque id" enforceable rather than
conventional. Worth doing only if a second string-shaped concept enters the same signatures;
today it would be ceremony.

### Reference Library

`FolderImageEnumerator` is a **stateless domain service** — the classification rules (`IsImage`,
`IsDirectory`) and the traversal policy are genuine domain knowledge that belongs to no entity.
`IDocumentTree` is a **port**, `DocumentEntry` a **value object**. Invariants: traversal is
depth-first in encounter order, and the `visited` set guarantees termination on a provider that
reports a cycle.

The pool itself has no type — it leaves as `IReadOnlyList<string>`. A `ReferencePool` value object
(pool + root document id + count) is the natural home for FD-008's "which folder am I drawing
from" display and for the `LastCollection` write-back; add it when a second consumer needs the
folder identity alongside the images.

### Session Setup

Entirely stateless: a static domain service (`SessionSetup`) plus one value object holding the
evaluated result (`SessionSetupState`) and one holding the output (`SessionConfig`). The Start
gate (`CanStart`) is a domain rule, not a UI rule — the Android layer binds a button's
`Enabled` to it and owns nothing else. Parsing lives here too (`ParsePositive`), which is correct:
"what counts as a valid seconds input" is domain knowledge, and keeping it out of the `EditText`
handler is what makes it testable.

### Preferences

`AppSettings` is an **aggregate root** with a fixed identity (`Id == 1`) and `SettingsStore` is its
**repository** — a single-document store with `GetSettings` / `SaveSettings` and a create-on-first-
read policy.

Two deviations worth naming, neither urgent:

- **No port interface.** Core's persistence is a concrete class, so any future Core consumer would
  depend on LiteDB rather than on an abstraction. Today only Activities call it, so the seam has no
  second implementation to justify it; introduce `ISettingsRepository` at the moment a Core type
  needs to read settings.
- **Persistence attribute on the domain entity.** `AppSettings` carries LiteDB's `[BsonId]`, so the
  storage technology is visible on the model. Acceptable at one document and one collection; if
  `AppSettings` grows domain behaviour, split it into a domain type and a persisted DTO rather than
  spreading BSON attributes.

## 18. Domain events

**There is no event bus today, and that is the right call at this size.** Communication between
contexts is direct calls and observed state: the repaint loop polls `PoseCountdown.IsExpired` and
calls `Advance`; the screen reads `CurrentImage`, `IsComplete`, `CouldNotDisplayImage`. One
producer, one consumer, same thread — a publisher/subscriber layer would add indirection between
two objects that already know each other.

The events below are nevertheless **named**, because the names are the vocabulary FD-006/007 will
use in tickets, logs, and analytics whether or not a type exists:

| Event | Raised when | Consumed by |
|---|---|---|
| `PoseCompleted(imageId, duration)` | Timer expiry or manual done-tap | Counts, summary |
| `PoseSkipped(imageId)` | FD-006 skip control, or an unreadable image | Logging; never counts |
| `ImageUnreadable(imageId)` | Loader returned null | `onUnreadable` hook → `Log.Warn` |
| `SessionCompleted(summary)` | Target count reached | FD-007 summary screen |
| `SessionEndedEarly(summary)` | `End()` called | FD-007 summary screen |
| `PoolUndisplayable` | Consecutive-failure budget exhausted | Error state |

Two of these already exist in weaker form: `ImageUnreadable` as the injected `Action<string>?
onUnreadable` callback, and the terminal ones as the `IsComplete` / `CouldNotDisplayImage` flag
pair that `SessionActivity.Render` branches on.

**If events do become worth materializing** — the trigger is a *second* consumer, e.g. session
history persistence or streak tracking landing alongside the summary screen — do it as an
in-aggregate list, not a bus:

```csharp
public interface ISessionEvent;
public readonly record struct PoseCompleted(string ImageId, TimeSpan Duration) : ISessionEvent;

// on the aggregate root
public IReadOnlyList<ISessionEvent> DrainEvents();   // returns and clears
```

The screen drains after each command and dispatches. That keeps the events testable in Core, keeps
ordering deterministic, and adds no threading question. A static event bus would also violate §5's
"no statics or singletons for cross-object handoff".

## 19. Clean architecture layers

The skill's four layers map onto the projects of §2 as follows. Dependencies point inward only.

| Layer | Contains | Lives in |
|---|---|---|
| **Presentation** | `MainActivity`, `SessionActivity`, layouts, strings | App project |
| **Application** | Use-case orchestration: wiring a session, advancing a pose, launching a screen | Split — `SessionPlayer<TImage>` in Core, the rest inside the Activities |
| **Domain** | `DrawingSession`, `PoseCountdown`, `SessionSetup`, `FolderImageEnumerator`, value objects | `FigureDrawing.Core` |
| **Infrastructure** | `SettingsStore` (LiteDB), `ContentResolverDocumentTree` (SAF), `ImageDecoding` (BitmapFactory) | Core `Data/` + app project |

The domain layer has no outward dependency: `FigureDrawing.Core` references only LiteDB, and only
from `Data/`. Verified structurally by `AndroidBuildTests` and by the project references.

**The application layer is the blurry one, on purpose.** `SessionActivity.OnCreate` composes the
object graph and `Advance` sequences two domain calls — both are application-layer work living in
a presentation-layer class. At this size that is an accepted trade (§3: no ViewModel layer). The
threshold for extracting it is stated in §17: when the same multi-object sequence appears in two
screens or two handlers, it moves to Core.

## 20. Where the code deviates today

Live findings, ordered by how much they cost. None is a blocker; each has a stated trigger.

1. **`MainActivity` spans three contexts** — Reference Library (SAF picking, enumeration, the
   thumbnail grid), Session Setup (inputs, preset chips, Start gate), Preferences (opening the
   database, loading and saving settings). The Claude Design import made this literal: the three
   contexts are now the three tabs of one screen. Not a god object at this size, but it is the only
   class in the codebase that touches three contexts, so it is where the next rule will be tempted
   to land. On the next feature that touches it, split by context: keep view wiring in the Activity
   and move folder-loading and settings-syncing into their own adapter classes.
2. ~~The pose-restart rule lives in an Activity~~ — closed by `PoseSession` (§17).
3. **`SettingsStore` has no port interface** and `AppSettings` carries a LiteDB attribute (§17).
4. **Session state is not persisted across process death** (§5) — from a DDD angle, the session
   aggregate has no identity and no repository, which is exactly why rotation restarts a pose. If
   FD-008's rotation handling requires survival, that is the point at which `DrawingSession` gains
   an id and a snapshot/restore pair, not a `static`.

## 21. Testing the model, and what "done" looks like

The three tiers of §11 map cleanly onto the model, and the mapping is the rule for where a new test
goes:

- **Aggregate invariants** (the §17 table) → unit tests, one file per Core type, with injected
  clock and `Random`. Every row of that table is a test.
- **Cross-context flows** (setup → session → summary) → the `*E2ETests.cs` model tests, which drive
  the real Core objects with no Android.
- **Adapter conformance** (`IDocumentTree`, the bitmap loader) → in-memory fakes in unit tests. The
  Android implementations are covered by the contract tests only to the extent that their view ids
  and strings exist.
- **Nothing about the domain is tested through Appium.** A domain rule reachable only from a UI test
  is a rule in the wrong layer (§14).

Success criteria for the DDD structure, checkable rather than aspirational:

- [ ] `FigureDrawing.Core` has zero `Android.*` / `Java.*` references — guarded by project setup and `AndroidBuildTests`
- [ ] Each Core type belongs to exactly one context in the §16 table; new types are added to it
- [ ] Context dependencies stay acyclic and match the §16 map — Execution never reads settings, Setup never reads the pool's contents
- [ ] Every invariant in §17 has a named unit test
- [ ] No aggregate mutates through a public field or a setter — commands only
- [ ] No rule reachable only through an Activity (the §14 list, plus the §20 findings closed)
- [ ] Any new time or randomness in Core arrives through an injected `Func<TimeSpan>` / `Random`
