# Test sheet: two people, about 45 minutes

**A** = mentor (Imdad). **B** = a teammate on their own Windows PC with SOLIDWORKS 2026. **Both** = each of you on your own computer.

Already confirmed in real use, so not repeated here: installing and automatic add-in updates, signing in, Open Robot, Edit/Release Edit, Submit and the Submit window, FRCDesignLib inserts (including custom lengths), inserting into the right assembly, and Upgrade Robot Files.

This sheet covers what's **new in 0.13** and the **safety paths nobody has needed yet**. Tests use low-risk files (motor covers, `80_Unsorted`); at the end A undoes every test submit from the web page. Tick ☐ → ☑. If anything doesn't match "Expect", write the step and the exact message under [Problems found](#problems-found) and keep going.

---

## 0. Before you start (A)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 0.1 | A | https://cad.imdad.stream/admin → **Add-in** → **Release to students** for the newest version. Both: install it when SOLIDWORKS offers it | The panel header shows the same version on both computers | ☐ |
| 0.2 | A | **Seasons** → write down 2026-Robot's revision: **r____** | Everything after it is a test submit, undone in section 6 | ☐ |

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
| 2.6 | Both | Close the part without submitting, reopen it, click **Edit** → change nothing → close it | Within a few seconds it's no longer listed under "You're editing" (unchanged → given back) | ☐ |

## 3. Library tab

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 3.1 | A | Mentor page → **Library** → upload any small part (e.g. a bracket) | Uploaded | ☐ |
| 3.2 | A | SOLIDWORKS toolbar → **Library** | The panel jumps to the Library tab with the cursor in the search box | ☐ |
| 3.3 | A | Search the bracket's name | It's listed first, marked "Team Library", then FRCDesignLib results | ☐ |
| 3.4 | A | With **no document open**, pick it → **Insert** | It opens by itself; the panel says it's in your robot (90_COTS). Close it | ☐ |
| 3.5 | B | Download any vendor `.SLDPRT` (McMaster) → Library tab → **Import a downloaded CAD file…**, with your assembly being edited | Copied into `90_COTS\Imported\…` and inserted into the assembly | ☐ |
| 3.6 | Both | **Submit** everything so far (comment "TEST"); uncheck A's bracket copy if you don't want it | "✓ Submitted N files as rN" in the panel | ☐ |

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
| 4.9 | A | Web **Locks** → **Release** B's `TPU_MotorCover_v2`, then **Edit** it in SOLIDWORKS | A holds it | ☐ |
| 4.10 | B | **Submit** | A yellow note that TPU_MotorCover_v2 can't be submitted because imdad is editing it; nothing else blocked. **Cancel** | ☐ |
| 4.11 | B | Close it → **Tools → Set Aside My Changes** → check it → Set aside | A copy in `C:\JOCO-ROBOS\Set Aside\…`; the team's version is back. A: **Tools → Release Edit** it | ☐ |
| 4.12 | Both | Close documents. In Explorer delete `80_Unsorted\pvc1intube.SLDPRT` → **Tools → Restore Deleted Files** | Listed → Restore → back | ☐ |

## 5. When things go wrong

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 5.1 | B | Change and save a part you're editing. Turn Wi-Fi off → **Submit** | The window says it didn't complete; edits and locks are kept | ☐ |
| 5.2 | B | Wi-Fi on → **Submit** again | Submitted. A: **Seasons** → Recent submits shows it **once** | ☐ |
| 5.3 | A | **Edit** a part, then end SOLIDWORKS in Task Manager. Restart | Still yours; the panel's "Give back the ones I didn't change" releases it | ☐ |
| 5.4 | Both | Save a change without submitting, then close SOLIDWORKS | Asks once "Submit now before exiting?" → **No** closes normally | ☐ |
| 5.4b | A | Sign in as the same account on a second computer (or a second Windows user). Submit a small change on computer 1 | Computer 2's panel shows "⬇ 1 new change", from "you (another computer)", and gets it | ☐ |
| 5.5 | Both | Wi-Fi off, then look at the panel and try **Edit** | "Can't reach the server" with a reason; nothing becomes writable. Wi-Fi back on | ☐ |

## 6. Finish (A)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 6.1 | Both | Submit or Set Aside anything left; give back anything still locked | The panel shows no changes waiting and nothing you're editing | ☐ |
| 6.2 | A | Web **Seasons** → Recent submits → **Undo…** each test submit after r____ (step 0.2), **newest first** | Each says "Undid rN as new revision" | ☐ |
| 6.3 | Both | Close documents and wait for the automatic update (or **Tools → Update**) | The test changes are gone from both computers | ☐ |
| 6.4 | A | Web **Locks** | No test locks left | ☐ |

---

## Problems found

| Step | Who | What happened (exact message, or a screenshot name) |
|---|---|---|
| | | |
| | | |
| | | |

Send this list to Claude; each problem gets fixed and you retest just that step.
