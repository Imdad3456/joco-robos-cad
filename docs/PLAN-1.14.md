# CAD Hub 1.14: the experience, not more features

Revised 2026-10-09 after a full review of the task pane (`StatusPane`, `PaneState`, `RobotFilesPanel`, `FrcLibraryPanel`)
and the status check (`SvnWorkspace.Snapshot`). Nothing here replaces SVN sync, locking or Submit: every change reads the
data those already produce and presents it better. Nothing is released or deployed without a mentor.

## What a student sees today, and what gets in the way

Robot tab, top to bottom: update banner · green "flash" · red warning · robot name and sync status · a grey paragraph
(incoming submits as `#123 sarah: …`, how to get them, "Checked 2:41 PM") · Open Robot / Close & Update · one card
mixing four things (the open file's status paragraph, Edit, Ask, History, teammate requests, "3 changes waiting",
Submit, "You're editing A, B, C, D, …", Give back) · the file tree (✎ 🔒 ● with names only in tooltips) · Check now ·
Diagnostics.

1. **Prose instead of state.** The open file's status is two or three sentences, and the same how-to text repeats
   every time ("When you save: if SOLIDWORKS lists read-only files, tick…" shows whenever an assembly is editable).
2. **My work isn't a list.** What I'm editing is a comma-separated sentence that cuts off at four names. I can't click
   it, can't see which files are unsaved, saved, or untouched, and Submit's count isn't tied to anything visible.
3. **Ownership is hidden.** The tree shows 🔒 but who has it is only in a tooltip; folders say nothing, so finding
   "who's in the intake" means expanding everything. Nothing marks the files that teammates changed and I don't have yet.
4. **Notifications vanish.** "Sarah is waiting for X" and "X is free now" are 15-second flashes. Miss them and they're gone.
5. **Noise.** "Checked 2:41 PM" is always there; "Check now" competes with Diagnostics; the incoming list is SVN
   revision text.

## The design

One rule: **show the next step and the things that need me; everything else is one hover or one click away.**

```
 2027-Robot                    ✓ Up to date  ↻        ← hover: checked 2:41 PM; ⬇ 3 new / ⚠ Offline when that's true
 ─────────────────────────────────────────────
 [ attention strip: at most one, only when it matters: required update · wrong SOLIDWORKS · interrupted Submit ·
   work recovered after a crash · "Close & Update" when teammates' changes are waiting ]

 ┌ Intake Plate.SLDPRT ───────────────────────┐     ← THIS FILE: only while a robot file is active
 │ 🔒 sarah is editing · since 2:10 PM         │        one status line, one action
 │ [ Ask sarah for it ]            History     │        (Edit · Ask · nothing), a hint only when it applies
 └─────────────────────────────────────────────┘

 MY WORK                       Give back 1 unchanged  ← only when I hold locks or have changes
  ✎ Intake Plate        unsaved                        click a row to open it
  ✎ Shooter Hood        saved   ✋ sarah is waiting   requests stay on the row until handled · Not yet
  ● Camera Mount        new
 [ Submit 2 ]

 ROBOT FILES                                           ← owner and incoming on the row, folders summarize
  ▸ 20_Intake            🔒 2  ⬇ 1
  ▾ 30_Shooter
      Shooter Hood       ✎ you
      Flywheel Plate     🔒 sarah
      Hood Gear          ⬇ new version
  right-click: Open · Ask sarah for it · File History · Where Used · Show in Explorer
 ─────────────────────────────────────────────
 Diagnostics                                   1.14.0
```

* **This file** follows the active document (and the selected component in an assembly, step 3). The tree selects
  the active file too, so the two always agree.
* **My work** is the to-do list: Submit lives with the files it submits; unsaved files are flagged before Submit
  finds them; teammate requests sit on the file they're about.
* **Robot files** answer "who's working where" without opening anything; *Ask for it* works from the tree.
* **Library** keeps its search and insert. Its long hint text moves into a tooltip and the "Browse"/"Import" links
  into one row.
* **Activity** (step 2) holds what used to flash and vanish plus recent team submits: an in-pane notification list,
  reached from the sync line's "⬇ 3 new". It's a third tab only if it earns its place in testing; otherwise a
  section under Robot files.

### A new student's first session
Install → SOLIDWORKS opens with CAD Hub showing only **Open Robot** → sign-in and download (unchanged) → the robot
opens; **This file** says "Read-only · nobody's editing it. Start changing it to edit." → they change a part, CAD Hub
offers the lock (unchanged) → the card says "✎ Editing · unsaved" and **My work** lists the part → Save → "saved",
**Submit 1** → the Submit window (unchanged) → "✓ Submitted". At every point there is one obvious button.

### SOLIDWORKS assembly tree
CAD Hub won't paint ownership into the FeatureManager tree: the only API routes change the documents (colors,
display states, names) or hook SOLIDWORKS' own window, which risks dirtying files and breaking with every SOLIDWORKS
update. Instead, selecting a component shows its owner in **This file** with Edit or Ask (step 3), which is where the
student is already looking.

## Recovery as a workflow
Verified in SOLIDWORKS 2026 SP4.1 (API test, 2026-10-09): the save-as-copy call CAD Hub uses writes the **in-memory**
model (a feature added after the last save is in the copy, not in the robot file), leaves the open document unsaved
at its own path, and doesn't touch the robot file. Next:
* After a crash, the attention strip says "Recovered work: 2 files". **Review** opens a list (file, copy time, saved
  file's time, who holds the lock now).
* **Restore** only when the student still holds that file's lock and the file isn't open; the current robot file
  goes to Set Aside first. Never automatic, never onto a teammate's file.
* **Save a copy to…** for everything else (lock gone, or to compare: SOLIDWORKS can't open two files with the same
  name, so a copy can't be opened next to the original).

## CAD health, where the student already is
Reuse `SubmitCheck` (missing, temporary and outside references, rebuild problems) and `HealthCheck`:
* On save of a file in My work, check that one document cheaply (rebuild errors, missing references for an
  assembly) and show it on its row: "⚠ 2 rebuild errors". Submit's window and blocking rules stay as they are.
* Check This Computer keeps its findings; a problem that blocks work (lost lock, conflict, low disk) also shows in
  the attention strip.
* No new "run the checker" button.

## Order of work
1. **Robot tab** (this step): data for incoming files; PaneState rebuilt around the next step, This file, My work;
   StatusPane redrawn; file tree with owners, incoming, folder summaries, follow-active, Ask from the tree. Tests for
   every state.
2. **Activity**: persistent notices (requests, freed files, recovered work) and recent submits.
3. **Assembly selection** in This file.
4. **Recovery review and restore.**
5. **Health on save** in My work.

After each: `dotnet run --project tests/Addin.Tests -c Release`, an add-in build, and TESTING.md steps for what only
SOLIDWORKS can show.

## Step 0 (done): automatic recovery copies
Copies of files the student is editing with unsaved changes, every 5 minutes while they pause, outside the robot
(`%LOCALAPPDATA%\JocoRobos.Cad\Recovery`), offered after a crash. See `Recovery.cs`, `AddinRecovery.cs`, TESTING.md 5b.
