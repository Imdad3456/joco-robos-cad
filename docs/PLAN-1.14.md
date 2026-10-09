# Plan for 1.14: recovery copies, a clearer panel, CAD health

Written 2026-10-09 after reviewing the code at 1.13.8. Each step ships on its own, keeps **Open Robot → Edit → Submit**
unchanged, and never touches SVN locking, Update or Submit's safeguards. Server changes (if any) wait for a mentor to deploy.

## What already exists (reused, not rebuilt)

| Need | Already there |
|---|---|
| Copy of unsaved work | `SaveSafetyCopy` (Edit on a dirty file, closing an unlocked dirty file) saves to `C:\JOCO-ROBOS\Set Aside` |
| Keep my version, restore the team's | Set Aside My Changes (`SvnSubmit.SetAside`) |
| Interrupted submit | `pending-submit-*.txt`, finished by the next Submit |
| Exit reminder | `OnSolidWorksClosing` offers Submit when work is unsubmitted |
| Document events | `DocumentWatcher` (first change, save, close) |
| Health | `HealthCheck` (Check This Computer: server, versions, folder, missing references, disk) and `SubmitCheck` (missing/temporary/outside references, rebuild problems, blocking only where needed) |
| Collaboration data | `WorkspaceSnapshot` (locks with owner and since, incoming changes, mine/changed/new), edit requests ("Ask … for it"), `PaneState` (pure, tested), `RobotFilesPanel` (✎ 🔒 ●) |

## Step 1: automatic recovery copies (this branch)

Gap: nothing protects **unsaved** edits in a locked file from a SOLIDWORKS crash or power loss, and SOLIDWORKS' own
Auto-recover is often off or slow to find.

- `Recovery.cs` (pure, tested): where copies go, when a round is due, pruning, which copies hold work newer than the
  saved file, and an "SOLIDWORKS is running" marker to tell a crash from a normal close.
- `AddinRecovery.cs`: every 30 s, if due, saves a copy of each open team file **the student is editing** (not
  read-only) **with unsaved changes**, using the same Save-as-copy call as `SaveSafetyCopy`.
  - Due = something was done since the last round, 5 min have passed (longer if copies are slow), and there's been
    no mouse/keyboard input for 20 s, so SOLIDWORKS never pauses under the student's hand.
  - Skipped while CAD Hub is busy, a CAD Hub side panel is open, a SOLIDWORKS command is running, a sketch is open,
    or a part is being edited inside an assembly; skipped on low disk.
  - Stored outside the robot: `%LOCALAPPDATA%\JocoRobos.Cad\Recovery\<Season>\<time>\<path in robot>`, so SVN,
    Submit, Update, Set Aside, Robot Files and the reference checks never see them. Last 5 per file, 14 days.
  - The document stays dirty and in place; the watcher ignores these saves (so the panel doesn't count a file as
    saved when it isn't). That also fixes the same miscount after `SaveSafetyCopy`.
- After an unexpected close, the next start lists files whose copy is newer than the saved file and opens the folder.
- Tools → CAD Hub → **Recovery Copies** opens the folder any time. Diagnostics reports how many there are.

## Step 2: task pane (collaboration awareness)

- "Who's editing" section from the existing lock data: teammates' locks grouped by person with "since", mine first,
  stale locks (> 3 days) marked for a mentor.
- Recent activity: the last few submits (author, message, time) from the SVN log the status refresh already reads.
- Clear lock state for the active file in one line (yours / free / teammate since …, with Ask).
- All text built in `PaneState` so it stays unit-tested; `StatusPane` only draws it.

## Step 3: CAD health checks

- A `CadHealth` pass over the open robot (or active assembly): suppressed/unresolved components, missing files,
  references outside the robot, rebuild errors and warnings (`GetWhatsWrong`), mates in error, and files from a
  newer SOLIDWORKS. Pure evaluation (like `HealthCheck`) with SOLIDWORKS gathering kept thin.
- Shown from the panel ("Check the robot") and folded into Check This Computer; Submit keeps its current checks and
  blocking rules (warnings stay warnings).

## Testing

- After each step: `dotnet run --project tests/Addin.Tests -c Release` and an add-in build; CI also runs the server tests.
- Inside SOLIDWORKS: new TESTING.md sections for each step, run in the `2099-Robot` test season.
