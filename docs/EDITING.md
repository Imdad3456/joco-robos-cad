# Editing and saving team files

Students change a part and press Ctrl+S; CAD Hub takes care of the lock. This page records what SOLIDWORKS actually does
(measured in SOLIDWORKS 2026 SP4.1 through its API, in an isolated instance on throwaway files, 2026-10-09) and the rules
CAD Hub follows because of it. Code: `SaveRules.cs` (pure rules, unit-tested), `AddinSave.cs` (SOLIDWORKS side),
`SaveAsChoiceDialog.cs`.

## What SOLIDWORKS does

| Situation | SOLIDWORKS 2026 |
|---|---|
| Ctrl+S in a changed **read-only** file | Doesn't save: turns into **Save As** (a window, or a file elsewhere). The pre-save event never fires; the pre-Save-As event does, before its window |
| Save All, or Ctrl+S in an assembly, with a changed read-only file in it | Shows its **read-only files window** before any per-document event |
| Answering 1 to the **pre-command** event (Save 2, Save All 19, Save As 620) | The command doesn't run: no window, nothing written, every document stays changed where it was |
| Answering 1 to the **pre-Save-As** event | Cancelled cleanly (works for Ctrl+S's Save As and File → Save As, read-only or not) |
| Clear the file's read-only flag, then `SetReadOnlyState(false)` on a changed document | **Changes kept**; the next save writes them to the original file |
| Plain **Save As** of a part an open assembly uses | The window **moves to the copy** and the assembly **now points at the copy** (marked changed) |
| **Save As Copy** of the same part | Window stays the team's file; the assembly is untouched |
| `ReplaceComponents2` in a (locked) assembly | Swaps the component explicitly; the saved assembly references the new file |
| A **forced rebuild** of an assembly | Marks read-only parts and the assembly **changed**, fires the change event: no student change at all |
| Leaving a sketch without changing it | Marks the part changed |
| Adding a note to a drawing | Changed, but **no change event** |
| The change event after a document is already marked changed | Doesn't fire again |
| Opening a feature or a sketch to edit it | `FeatureEditPreNotify` / `FeatureSketchEditPreNotify` fire **before** anything changes |
| API saves (`Save3`, `SaveAs3`) | Don't fire the pre-command or pre-Save-As events (so CAD Hub's own saves never re-trigger it) |

So: a change event isn't evidence of editing, and SOLIDWORKS offers no event before an arbitrary change. Locking inside a
SOLIDWORKS event isn't possible for read-only files (the pre-save event doesn't fire), and network work inside an event would
freeze SOLIDWORKS anyway.

## The rules

1. **Lock early only on clear evidence.** Opening a feature or sketch to edit, or starting a modeling command (Sketch,
   Extrude, Cut, Fillet, Hole Wizard), in a read-only team part or drawing takes its SVN lock in the background. SOLIDWORKS'
   read-only state is left alone then (an editor is opening) and switched at save. A plain "changed" never locks: the panel
   says *Changed · Ctrl+S locks it*, and a teammate's lock is warned about at once. Assemblies never lock by themselves.
2. **Ctrl+S and Save All.** When the documents a save covers include a changed read-only team file, CAD Hub stops the
   command (pre-command event), and after SOLIDWORKS' event has finished:
   - plans each document (`SaveRules.Plan`): *save now* (writable), *lock then save* (the window Ctrl+S was pressed in, or a
     file with clear evidence of editing), *ask then lock* (an assembly saved on purpose in its own window), *leave alone*
     (only a rebuild may have changed it), or *blocked* (a teammate's lock, a newer version on the server, the student's own lock
     on another computer);
   - takes a recovery copy, locks (network, outside the event), makes the file editable keeping its changes, saves the original;
   - one save at a time (`SaveGate`); a second Ctrl+S meanwhile says "Still saving";
   - reports: saved files and files left alone in the panel's line; anything blocked or failed in a message, each still open
     with its changes. A failed lock (offline, a teammate was quicker) changes nothing; Ctrl+S again retries.
3. **File → Save As on a team file** is stopped and replaced by three choices:
   - **Save the team's file** (lock if needed, then save), unavailable with the reason when blocked;
   - **Experimental copy**: Save As Copy to `C:\JOCO-ROBOS\Experiments\Name (experiment).SLDPRT` (numbered when taken),
     never inside the robot, never over a file, never a name SOLIDWORKS has open; the window and every assembly keep the team's
     file; the panel marks copies *Experimental copy · not part of the robot*;
   - **New team part**: a name no file in the robot has, inside the robot, never over a file; Save As Copy, then, only if the
     student ticks it, CAD Hub locks the open assembly and swaps the component (`ReplaceComponents2`).
   Save As on files outside the robot is left to SOLIDWORKS.
4. **Backstop:** a read-only team file that reaches Save As any other way is stopped by the pre-Save-As event and saved as in 2.
5. **Edit** stays: it locks the open file (or the selected component) at any time.

## Verified end to end (isolated SOLIDWORKS 2026, the production rules compiled in)

Ctrl+S in a part and in a drawing (locked, original saved, no Save As, no copies) · Save All with two parts being edited, one
part and the assembly only marked changed (the two saved; the others never locked, still changed, reported) · a teammate's lock,
a newer version, the server unreachable (nothing written, still changed, read-only; retried later, the same work saved) · a second
Ctrl+S while saving (refused, no window) · File → Save As → experimental copy (window and assembly unchanged) · new team part
with and without replacing it in the assembly · Ctrl+S in an assembly while editing a part in it, the assembly declined · Ctrl+S
in a locked assembly with a read-only part only a rebuild changed (assembly saved, part untouched, no window).

## Not verifiable without a person at the keyboard (TESTING.md 2.23–2.30)

The real Ctrl+S key and toolbar Save (assumed to be the same command, 2); editing a read-only component in place (Edit Part
refused through the API on a read-only component); the feature and sketch editors opening while CAD Hub locks in the background;
SOLIDWORKS' Auto-recover and "Save As copy" option interplay.
