# Test sheet: the 1.0 release gate (two people about 1 hour, a 20-minute uncoached student, then a one-week pilot)

**A** = mentor (Imdad). **B** = a teammate on their own Windows PC with SOLIDWORKS 2026. **Both** = each of you on your own computer.

Already confirmed in real use, so not repeated here: installing and automatic add-in updates, signing in, Open Robot, Edit/Release Edit, Submit and the Submit window, FRCDesignLib inserts (including custom lengths), inserting into the right assembly, and Upgrade Robot Files.

This sheet covers what's **new since 0.13** and the **safety paths nobody has needed yet**. Everything runs in **2099-Robot, a disposable full copy of the 2026 robot** (same files and history, no locks), so nothing you do can touch the real robot, and the copy is removed at the end. Tick ☐ → ☑. If anything doesn't match "Expect", write the step and the exact message under [Problems found](#problems-found) and keep going. **Freeze features while testing**: only fix what this sheet finds.

---

## 0. Before you start (A)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 0.1 | A | https://cad.team5919.org/admin → **Add-in** → **Release to students** for the newest version. Both: install it when SOLIDWORKS offers it | The panel header shows the same version on both computers | ☐ |
| 0.2 | A | Create the test season (ask Claude, or on the Linux PC: `ssh deck@100.97.7.84 podman exec -u www-data joco-svn python3 /opt/joco/joco.py test-season create 2099-Robot 2026-Robot`) | "Created 2099-Robot as a copy of 2026-Robot … without locks" | ☐ |
| 0.3 | Both | Close all documents → **Tools → CAD Hub → Choose Robot** → **2099-Robot** → **Open Robot** | It downloads the copy (a few minutes) and opens it; the panel header says 2099-Robot. Every file named below is in `C:\JOCO-ROBOS\2099-Robot` | ☐ |

## 1. New account (a third person, or B on a spare Windows user)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 1.0a | New student | Start SOLIDWORKS (this Windows user never used JOCO) | **Team Server** asks which server. Type `cad.team5919.orgg` → Continue: "Couldn't reach…"; type `example.com`: "isn't a CAD Hub server"; type `cad.team5919.org`: the account form | ☐ |
| 1.0b | A | On your own (already set-up) PC, after installing this version | Never asked for a server; everything works as before. Tools → Sign In shows "Team server: cad.team5919.org  Change…" | ☐ |
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
| 2.6 | Both | Close the part from 2.3 (it stays yours: it has a saved change). Open `80_Unsorted\id2_1.SLDPRT`, click **Edit**, change nothing, close it | id2_1 is no longer listed under "You're editing" within a few seconds (unchanged → given back); the part from 2.3 still is | ☐ |

## 3. Library tab

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 3.1 | A | Mentor page → **Library** → upload a small part you'd want in the team Library anyway (the Library is shared, not part of the test season) | Uploaded | ☐ |
| 3.2 | A | SOLIDWORKS toolbar → **Library** | The panel jumps to the Library tab with the cursor in the search box | ☐ |
| 3.3 | A | Search the bracket's name | It's listed first, marked "Team Library", then FRCDesignLib results | ☐ |
| 3.4 | A | With **no document open**, pick it → **Insert** | It opens by itself; the panel says it's in your robot (90_COTS). Close it | ☐ |
| 3.5 | B | Download any vendor `.SLDPRT` (McMaster) → Library tab → **Import a downloaded CAD file…**, with your assembly being edited | Copied into `90_COTS\Imported\…` and inserted into the assembly | ☐ |
| 3.6 | Both | **Submit** everything so far (comment "TEST"); uncheck A's bracket copy if you don't want it | "✓ Submitted N files (#…)" in the panel | ☐ |

## 3b. Robot Files (Robot tab)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 3b.1 | Both | Robot tab, below the status: **ROBOT FILES** | The robot's top folders (00_Master, 10_Drivetrain…). On a computer without the robot: "Open Robot to download the robot files." | ☐ |
| 3b.2 | Both | Expand `30_Shooter` (or any folder), double-click a part, an assembly, and a drawing | Each opens in SOLIDWORKS, read-only as usual; nothing gets locked by opening. Double-clicking one that's already open just switches to it | ☐ |
| 3b.3 | Both | Type `motor` in the search box | Matching files from anywhere in the robot appear within a moment, each with its folder; double-click one to open it | ☐ |
| 3b.4 | A | **Edit** a part. B: wait for the status check (or **Check now**) | A sees ✎ on that file; B sees 🔒 with "imdad is editing this since …" in the tooltip | ☐ |
| 3b.5 | Both | Save a new part into `80_Unsorted` | It appears with ● within a few seconds | ☐ |
| 3b.6 | Both | **Choose Robot** → another season and back | The list switches with it; it never shows the other season's files | ☐ |
| 3b.7 | A | Full robot: open and close folders quickly, and type in search while rotating the model | SOLIDWORKS never hitches | ☐ |
| 3b.8 | Both | Look at the Robot tab with nothing open, then with a part open, then after changing and saving it | Nothing open: robot name and ✓ status on one line, then the files. A part open: a light card with its name, status and Edit appears above ROBOT FILES. Saved changes: Submit appears in that card. Close the part: the card goes away | ☐ |
| 3b.9 | Both | Look at the file tree, then type `frame` | Folder, part, assembly and drawing icons; roomy rows; no heavy border. Results start with "Search results for "frame"", each with its folder in grey. **Esc** clears the search; **↻** (tooltip "Refresh robot files") reloads | ☐ |
| 3b.10 | Both | Footer at the bottom of the Robot tab | **Check now · Diagnostics** on the left, the version on the right; both links work | ☐ |

## 4. Together: nobody overwrites anybody

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 4.1 | A | Open `ABS_MotorCover_v2` → **Edit** | "✎ You're editing" | ☐ |
| 4.2 | B | Open `ABS_MotorCover_v2` | "🔒 imdad is editing this. You can still look, measure, and reference it", and no Edit button | ☐ |
| 4.2b | B | In the panel card, click **Ask imdad for it** | "✓ Asked imdad…"; the card now says you asked. A: within 3 min (or **Check now**) "✋ B is waiting for ABS_MotorCover_v2" in A's panel | ☐ |
| 4.3 | B | Change something in it anyway | A warning right away that imdad is editing it. Close without saving | ☐ |
| 4.4 | A | Change it, save, **Submit** | Submitted | ☐ |
| 4.4b | B | After A's Submit, wait up to 3 min (or **Check now**) | "✓ ABS_MotorCover_v2 is free now… Open it and click Edit." A's panel no longer shows the request | ☐ |
| 4.4c | A | Web mentor page → **Seasons** → **Who's working** | A and B with "online", their robot "up to date" (or "N submits behind"), and what each is editing | ☐ |
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
| 5.2d | B | Change and save a part, open **Submit**, type a comment, click **Submit**, and while it says "Checking file references…" close the window with **X** | The window closes. A: **Seasons** → Recent submits in 2099-Robot shows **nothing new** (closing never commits). B: Submit again normally → submitted once | ☐ |
| 5.2c | B | Edit an assembly, insert a part from a USB stick or Desktop, save it, then delete or rename that original file in Explorer → **Submit** | "…uses a file that isn't on this computer", naming it; Submit is off until you fix it or click **Submit anyway** | ☐ |
| 5.3 | A | **Edit** a part, then end SOLIDWORKS in Task Manager. Restart | Still yours; the panel's "Give back the ones I didn't change" releases it | ☐ |
| 5.4 | Both | Save a change without submitting, then close SOLIDWORKS | Asks once "Submit now before exiting?" → **No** closes normally | ☐ |
| 5.4b | A | Sign in as the same account on a second computer (or a second Windows user). Submit a small change on computer 1 | Computer 2's panel shows "⬇ 1 new change", from "you (another computer)", and gets it | ☐ |
| 5.4c | A | Mentor page → **Add-in** → set the team SOLIDWORKS version to `2025` → Save. In SOLIDWORKS, **Check now**, then try **Edit** on a part | Red panel note "This computer has SOLIDWORKS 2026, but the team uses 2025…"; Edit refuses. Set it back to `2026`: editing works again | ☐ |
| 5.4d | A | Publish a new add-in as **required** while B has a saved, unsubmitted change | B can still **Submit** it; new Edits ask to install the update first | ☐ |
| 5.4e | Both | Panel → **Diagnostics**, then paste into Notepad. A: mentor page → **Diagnostics** → View | "Sent to your mentors"; the same report on the mentor page and in Notepad: versions, account name, robot state, recent errors; no password, no setup code | ☐ |
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
| 6.7 | **Edit** `00_Master\Robot.SLDASM`, move one component, save → **Submit**: seconds of "Checking file references" | |
| 6.8 | During 6.7, click in SOLIDWORKS (rotate the model): does it respond, or freeze? | yes / no |

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
| 8.1 | A | Web **Seasons** → Recent submits → **Undo…** on the **newest** 2099-Robot submit → **Undo** | "Undid … as new revision". B: close documents; the automatic update brings the undone version back | ☐ |
| 8.2 | Both | Submit or Set Aside anything left; give back anything still locked | The panel shows no changes waiting and nothing you're editing | ☐ |
| 8.3 | Both | Close all documents → **Choose Robot** → **Current season** → **Open Robot** | Back on 2026-Robot, which nothing in this sheet touched | ☐ |
| 8.4 | A | Remove the test season (ask Claude, or: `ssh deck@100.97.7.84 podman exec -u www-data joco-svn python3 /opt/joco/joco.py test-season remove 2099-Robot`). Both: delete `C:\JOCO-ROBOS\2099-Robot` | Gone from Choose Robot; 2026-Robot unchanged | ☐ |

## 9. Pilot (a week, 2–3 students, features frozen)

After this sheet passes, 2–3 students use 0.14.x on the real robot for normal work for about a week. Nobody adds features. Write down every problem and every "why did I have to click that?" below; fix only what could lose or overwrite work, break someone else's robot, block recovery, or make Open → CAD → Submit confusing. Then tag 1.0.0.

| Date | Who | What happened |
|---|---|---|
| | | |
| | | |

---

## Problems found

| Step | Who | What happened (exact message, or a screenshot name) |
|---|---|---|
| | | |
| | | |
| | | |

Send this list to Claude; each problem gets fixed and you retest just that step.
