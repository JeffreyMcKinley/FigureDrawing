# PRDs — FigureDrawing

Requirements docs for work that is planned but not built. One file per unit of work, written
against [ARCHITECTURE.md](../ARCHITECTURE.md) and [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) — a PRD that
contradicts them is wrong, not visionary. Written with the `prd-generator` skill; the templates live
in `.claude/skills/prd-generator/references/`.

FD ids are continuous with the MVP stories FD-001..FD-008, which are shipped. Never reuse or
renumber an id.

## Open

| ID | Title | Context |
|----|-------|---------|
| [FD-009](FD-009-async-reference-library.md) | Reference library loads without freezing the screen | Reference Library |
| [FD-010](FD-010-pose-decode-off-the-tick.md) | A pose boundary never blocks the repaint loop | Session Execution |
| [FD-011](FD-011-tick-reports-what-changed.md) | The session says what changed, not just that something did | Session Execution |

All three come out of the repo-wide review of 2026-08-16. FD-009 and FD-010 are threading work in
the Android layer; FD-009 is the prerequisite in practice, since it establishes how this app does
background work and cancellation, and FD-010 follows the same shape on the player screen. FD-011 is
small and independent — a rule currently living in an Activity moving into Core — but it touches the
same `Tick` call site as FD-010, so do it first if both are in flight.

## Shipped

FD-001 (folder selection), FD-002 (session setup), FD-003 (session engine), FD-004 (player screen),
FD-005 (countdown), FD-006 (skip), FD-007 (end + summary), FD-008 (foldable layout). Their acceptance
criteria are the invariant tables in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) and the suites named in
[ARCHITECTURE.md §11](../ARCHITECTURE.md#11-testing-strategy). The original ticket stubs were an
early experiment and were never committed.

**Remembering the folder** is an FD-001 follow-on rather than an id of its own: the library the
artist last opened is restored on launch and is where the picker reopens. Its criteria are
`INV-SET-P5` and `INV-X-11` in [DOMAIN-MODEL.md §5.1 / §7](../DOMAIN-MODEL.md), enforced by
`LibraryReference` and covered by `LibraryReferenceTests`, `FolderMemoryContractTests` and the
folder-memory tests in `FolderPickerUiTests`.
