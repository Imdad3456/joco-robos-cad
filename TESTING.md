# Test sheet: the 1.0 release gate (two people about 1 hour, plus a 20-minute uncoached student)

**A** = mentor (Imdad). **B** = a teammate on their own Windows PC with SOLIDWORKS 2026. **Both** = each of you on your own computer.

Already confirmed in real use, so not repeated here: installing and automatic add-in updates, signing in, Open Robot, Edit/Release Edit, Submit and the Submit window, FRCDesignLib inserts (including custom lengths), inserting into the right assembly, and Upgrade Robot Files.

This sheet covers what's **new in 0.13** and the **safety paths nobody has needed yet**. Tests use low-risk files (motor covers, `80_Unsorted`); at the end A undoes every test submit from the web page. Tick ☐ → ☑. If anything doesn't match "Expect", write the step and the exact message under [Problems found](#problems-found) and keep going.

---

## 0. Before you start (A)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 0.1 | A | https://cad.imdad.stream/admin → **Add-in** → **Release to students** for the newest version. Both: install it when SOLIDWORKS offers it | The panel header shows the same version on both computers | ☐ |
| 0.2 | A | **Seasons** → write down 2026-Robot's revision: **r____** | Everything after it is a test submit, undone in section 8 | ☐ |

## 1. New account (a third person, or B on a spare Windows user)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 1.1 | New student | Start SOLIDWORKS → username (e.g. `test.student`), password twice → **Send request** | The form stays open: "✓ Request sent. Ask a mentor for your code" | ☐ |
| 1.2 | A | **Accounts** → "Students waiting for a code" | `test.student`, the time, and a code | ☐ |
| 1.3 | New student | Type a wrong code → **Finish** | "That code isn't the one for test.student"; the form comes back with the name filled in | ☐ |
| 1.4 | New student | The right code (and the same password) → **Finish** | "You're set up as test.student". A: the request is gone from the list | ☐ |
| 1.5 | A | **Accounts** → **Delete** `test.student` | Deleted | ☐ |

## 2. The panel does the work

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 2.1 | Both | Close all documents | Panel: "✓ Up to date" and only **Open Robot** | ☐ |
| 2.2 | Both | **Open Robot**, then open `80_Unsorted\Part1test.SLDPRT` (A) or `80_Unsorted\18ish.SLDPRT` (B) | "Read-only. Nobody else is editing it" and an **Edit Part1test** button | ☐ |
| 2.3 | Both | Wait 10 seconds, then change a dimension **without** clicking Edit | No question. The panel says "✎ You're editing …" (locked by itself), and your change is still there | ☐ |
| 2.4 | Both | Save | "1 change waiting" and a **Submit 1** button | ☐ |
| 2.5 | Both | Open your assembly (A: `50_Electrical\battery holder`, B: `motor cover asssewmbly`) read-only and drag a component | Assemblies still ask "Lock it for editing now?" → **No**. Close it without saving | ☐ |
| 2.5b | Both | Open your assembly read-only, change a mate right away (first 5 seconds), and click **No** if asked to lock. Then Submit something else | The panel keeps warning about the unsaved assembly changes; you can't easily forget them (write down anything that felt too quiet) | ☐ |
| 2.6 | Both | Close the part without submitting, reopen it, click **Edit** → change nothing → close it | Within a few seconds it's no longer listed under "You're editing" (unchanged → given back) | ☐ |

## 3. Library tab

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 3.1 | A | Mentor page → **Library** → upload any small part (e.g. a bracket) | Uploaded | ☐ |
| 3.2 | A | SOLIDWORKS toolbar → **Library** | The panel jumps to the Library tab with the cursor in the search box | ☐ |
| 3.3 | A | Search the bracket's name | It's listed first, marked "Team Library", then FRCDesignLib results | ☐ |
| 3.4 | A | With **no document open**, pick it → **Insert** | It opens by itself; the panel says it's in your robot (90_COTS). Close it | ☐ |
| 3.5 | B | Download any vendor `.SLDPRT` (McMaster) → Library tab → **Import a downloaded CAD file…**, with your assembly being edited | Copied into `90_COTS\Imported\…` and inserted into the assembly | ☐ |
| 3.6 | Both | **Submit** everything so far (comment "TEST"); uncheck A's bracket copy if you don't want it | "✓ Submitted N files (#…)" in the panel | ☐ |

## 4. Together: nobody overwrites anybody

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 4.1 | A | Open `ABS_MotorCover_v2` → **Edit** | "✎ You're editing" | ☐ |
| 4.2 | B | Open `ABS_MotorCover_v2` | "🔒 imdad is editing this. You can still look, measure, and reference it", and no Edit button | ☐ |
| 4.3 | B | Change something in it anyway | A warning right away that imdad is editing it. Close without saving | ☐ |
| 4.4 | A | Change it, save, **Submit** | Submitted | ☐ |
| 4.5 | B | Keep `Robot.SLDASM`, a sub-assembly and a part open in their own windows, part active. Wait up to 3 min (or **Check now**) | "⬇ 1 new change on the server", A's comment, and **Close & Update** | ☐ |
| 4.6 | B | **Close & Update** | All three close, update, and reopen, with the part active again; "✓ Up to date" | ☐ |
| 4.7 | A | Close **all** documents. B: change and **Submit** any small file | Within 3 min A's panel says "Getting 1 new change…" then "✓ Got 1 new change", with no clicks | ☐ |
| 4.8 | B | **Edit** `TPU_MotorCover_v2`, change it, save, don't Submit | | ☐ |
| 4.9 | A | Web **Locks**: B's `TPU_MotorCover_v2` shows since when. Click **Release** without ticking "they're done", then tick it and **Release**. Then **Edit** it in SOLIDWORKS | First refused ("Tick the box first"), then released; A holds it | ☐ |
| 4.10 | B | **Submit** | A yellow note that TPU_MotorCover_v2 can't be submitted because imdad is editing it; nothing else blocked. **Cancel** | ☐ |
| 4.11 | B | Close it → **Tools → Set Aside My Changes** → check it → Set aside | A copy in `C:\JOCO-ROBOS\Set Aside\…`; the team's version is back. A: **Tools → Release Edit** it | ☐ |
| 4.11b | B | Open `ABS_MotorCover_v2` → Tools → **File History** | A's submits with time and comment. Pick an older one → **Save this version as a copy…** → Desktop | A copy named "… (version N)" on the Desktop; saving it into `C:\JOCO-ROBOS` is refused | ☐ |
| 4.12 | Both | Close documents. In Explorer delete `80_Unsorted\pvc1intube.SLDPRT` → **Tools → Restore Deleted Files** | Listed → Restore → back | ☐ |

## 5. When things go wrong

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 5.1 | B | Change and save a part you're editing. Turn Wi-Fi off → **Submit** | The window says it didn't complete; edits and locks are kept | ☐ |
| 5.2 | B | Wi-Fi on → **Submit** again | Submitted. A: **Seasons** → Recent submits shows it **once** | ☐ |
| 5.2b | B | Change and save a part. Click **Submit** in the window, and the moment it says "Submitting…" end SOLIDWORKS in Task Manager. Restart | The panel says "An earlier Submit was interrupted" with a **Submit** button → it explains whether it reached the team; nothing is lost or sent twice | ☐ |
| 5.2c | B | Edit an assembly, insert a part from a USB stick or Desktop, save it, then delete or rename that original file in Explorer → **Submit** | "…uses a file that isn't on this computer", naming it; Submit is off until you fix it or click **Submit anyway** | ☐ |
| 5.3 | A | **Edit** a part, then end SOLIDWORKS in Task Manager. Restart | Still yours; the panel's "Give back the ones I didn't change" releases it | ☐ |
| 5.4 | Both | Save a change without submitting, then close SOLIDWORKS | Asks once "Submit now before exiting?" → **No** closes normally | ☐ |
| 5.4b | A | Sign in as the same account on a second computer (or a second Windows user). Submit a small change on computer 1 | Computer 2's panel shows "⬇ 1 new change", from "you (another computer)", and gets it | ☐ |
| 5.4c | A | Mentor page → **Add-in** → set the team SOLIDWORKS version to `2025` → Save. In SOLIDWORKS, **Check now**, then try **Edit** on a part | Red panel note "This computer has SOLIDWORKS 2026, but the team uses 2025…"; Edit refuses. Set it back to `2026`: editing works again | ☐ |
| 5.4d | A | Publish a new add-in as **required** while B has a saved, unsubmitted change | B can still **Submit** it; new Edits ask to install the update first | ☐ |
| 5.4e | Both | Panel → **Copy diagnostics for a mentor**, paste into Notepad | Versions, account name, robot state, recent errors; no password, no setup code | ☐ |
| 5.5 | Both | Wi-Fi off, then look at the panel and try **Edit** | "Can't reach the server" with a reason; nothing becomes writable. Wi-Fi back on | ☐ |

## 6. Timing on the real robot (A)

Write the times down; anything that makes SOLIDWORKS look stuck for more than a few seconds is a 1.0 bug. Slow operations are also logged automatically (see Copy Diagnostics).

| # | Do | Seconds |
|---|---|---|
| 6.1 | **Open Robot** with everything closed (already downloaded) | |
| 6.2 | Click **Edit** on a part | |
| 6.3 | **Submit** with 3 changed files: from click until "✓ Ready to submit" | |
| 6.4 | **Close & Update** with Robot.SLDASM open | |
| 6.5 | Library tab: type `kraken` until results appear; insert one already in the Library | |
| 6.6 | Switch between two open documents: does the panel update instantly? | |

## 7. Uncoached student (a teammate who hasn't seen JOCO)

Give them only the installer link and one sentence: "Install this, then use SOLIDWORKS as usual; the JOCO panel on the right tells you what to do." Then **don't explain anything**. Watch and write down every moment they ask "what do I click?", hesitate, or misunderstand something: each one is a 1.0 bug. Don't redesign things they understood.

| # | They should | Where they got stuck (write it down) | ✓ |
|---|---|---|---|
| 7.1 | Install, start SOLIDWORKS, ask for an account (you give the code when they ask) | | ☐ |
| 7.2 | Open the robot | | ☐ |
| 7.3 | Change one part in `80_Unsorted` | | ☐ |
| 7.4 | Add a part from the Library tab (FRCDesignLib or team Library) | | ☐ |
| 7.5 | Save and Submit | | ☐ |
| 7.6 | Open a file you're editing (A: **Edit** it first) and try to change it | | ☐ |
| 7.7 | Recover from one problem you set up on purpose (for example: you delete a file they need, or turn off their Wi-Fi during Submit) | | ☐ |

## 8. Finish (A)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 8.1 | Both | Submit or Set Aside anything left; give back anything still locked | The panel shows no changes waiting and nothing you're editing | ☐ |
| 8.2 | A | Web **Seasons** → Recent submits → **Undo…** each test submit after r____ (step 0.2), **newest first** | Each says "Undid rN as new revision" | ☐ |
| 8.3 | Both | Close documents and wait for the automatic update (or **Tools → Update**) | The test changes are gone from both computers | ☐ |
| 8.4 | A | Web **Locks** | No test locks left | ☐ |

---

## Problems found

| Step | Who | What happened (exact message, or a screenshot name) |
|---|---|---|
| | | |
| | | |
| | | |

Send this list to Claude; each problem gets fixed and you retest just that step.
