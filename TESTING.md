# Test sheet: two people, about 2 hours

**A** = mentor (Imdad). **B** = a friend on their own Windows PC with SOLIDWORKS 2026. **Both** = each of you on your own computer.

Tests run on the real **2026-Robot**, but only touch low-risk files (motor covers, the battery mount, `80_Unsorted`). At the end, A **undoes every test submit** from the web page, which returns the robot exactly to how it started, with history kept. A full backup of the robot was taken right after import, so nothing here can lose it. Tick ☐ → ☑ as you go. If anything doesn't match "Expect", **write the step number and the exact message** in [Problems found](#problems-found) and keep going.

---

## 0. Before you start (A, 5 minutes)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 0.1 | A | https://cad.imdad.stream/admin → **Add-in** → **Release to students** (if a version is waiting) | "Published add-in …" | ☐ |
| 0.2 | A | **Accounts** → add B's username (for example `sam`) | A setup code like `K7QM-3XRP-9TDW` next to the name | ☐ |
| 0.3 | A | Send B the username and setup code privately | | ☐ |
| 0.4 | A | **Seasons** → Health | All ✓: server, Deck backup, off-device backup, disk | ☐ |
| 0.5 | A | **Seasons** → write down 2026-Robot's revision here: **r____** | Everything after this number is a test submit, undone in section 8 | ☐ |

## 1. Install and sign in

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 1.1 | B | Install the newest `JOCO-ROBOS-CAD-Setup-….exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest). Windows warning → **More info → Run anyway** | Installs; SOLIDWORKS not needed open | ☐ |
| 1.2 | A | In SOLIDWORKS, accept the update offer (or wait up to 3 min / click **Check now**). Approve Windows, close SOLIDWORKS | SOLIDWORKS reopens by itself | ☐ |
| 1.3 | B | Start SOLIDWORKS | Welcome screen: username, setup code, new password twice → "You're set up as sam" | ☐ |
| 1.4 | A | Refresh **Accounts** | B's setup code is gone; within a few minutes B shows the add-in version, "today", and B's computer name | ☐ |
| 1.5 | Both | Look at the **JOCO ROBOS CAD** panel (right side) | Header shows the same version, **without** "(dev build)"; **Robot** tab says 2026-Robot | ☐ |
| 1.6 | A | If `C:\JOCO-ROBOS\2026-Robot` still holds the folder you fixed links in, rename it to `2026-Robot-old` first | Open Robot can download the team copy there | ☐ |
| 1.7 | Both | **Open Robot** | The 2026 robot downloads (first time ~110 MB) and `00_Master\Robot.SLDASM` opens read-only with all components. **Write down how long it took** | ☐ |

## 2. Solo editing (Both, each on your own computer)

A works in `50_Electrical\battery holder.SLDASM` and B works in `50_Electrical\Motor covers\motor cover asssewmbly.SLDASM`, so you don't collide yet. Open each assembly from the robot's tree (right-click → Open) or with File → Open.

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 2.1 | Both | In `Robot.SLDASM`'s tree, A clicks `batterymount3dmount` and B clicks `underplate electronics`. Then **Edit** | "Locked by <you>"; with that part open, the panel shows ✎ **You are editing this** | ☐ |
| 2.2 | Both | Change a dimension on that part → **File → Save All** | Within a few seconds the panel says "1 change waiting to submit" and the button reads **Submit (1)** | ☐ |
| 2.3 | Both | Open your assembly (A: `battery holder`, B: `motor cover asssewmbly`) → **Edit** → make a small new part, save it as `C:\JOCO-ROBOS\2026-Robot\80_Unsorted\Test-Spacer-<yourname>.SLDPRT`, insert it, **Save All** | Saves normally | ☐ |
| 2.4 | Both | **Submit** | One window: "✓ Ready to submit", **Changed**: your part + assembly, **New**: your spacer. Comment "TEST ..." → **Submit 3** → the window closes, no OK box; the panel shows "✓ Submitted 3 files as rN" for a few seconds; those files become read-only | ☐ |
| 2.5 | Both | **Edit** your part again, change it, **don't save** → **Submit**. Type the comment "TEST unsaved" first | The window says "1 document needs to be saved" and **Submit** is greyed out → **Save it and continue** → the warning disappears, your part appears under Changed, the comment is still there → **Submit 1** | ☐ |
| 2.5b | Both | **Edit** your part, change it, save, **Submit**, but leave the window open. Click back into SOLIDWORKS, change the part again and save, then click the Submit window | It rechecks by itself (you can also click **Check again**). **Cancel**, then **Submit** again from the panel: your comment is still there. Submit it | ☐ |
| 2.6 | Both | **Edit** your assembly, save a new part named `Claw.SLDPRT` into `80_Unsorted` | Right after saving: "A team file named Claw.SLDPRT already exists … 40_Climber\Coil climb\Claw.SLDPRT" | ☐ |
| 2.6b | Both | Insert it, save → **Submit** | The window shows "Claw.SLDPRT has the same name as another file" with **Show mine** / **Show existing** (each opens Explorer on the file); Submit is greyed out. **Cancel**, delete that part from the assembly, save, delete the file in Explorer, then **Release Edit** your assembly | ☐ |
| 2.7 | Both | **File → Open** `80_Unsorted\Part1test.SLDPRT` (A) or `80_Unsorted\18ish.SLDPRT` (B), without Edit. Wait 10 seconds, change a dimension | "You're changing … Lock it now?" → **No** → the panel turns yellow: "⚠ Unsaved changes in a read-only file" | ☐ |
| 2.8 | Both | Now click **Edit** | Locked; your change is still there (tell A if SOLIDWORKS reloaded it instead). Close **without saving** | ☐ |
| 2.8b | Both | Open `80_Unsorted\id2_1.SLDPRT` and change a dimension **right away** (within 5 seconds), without Edit | A few seconds later you are still asked "Lock it now?" → **No**, close without saving | ☐ |
| 2.9 | Both | Look at "Your locked files" in the panel | That part disappears within a few seconds (unchanged file closed → lock released) | ☐ |
| 2.9b | Both | **Edit** two parts, change neither, then close SOLIDWORKS completely. Reopen → panel → **Release my unchanged files** | Both released without opening them; "Your locked files" is empty | ☐ |
| 2.10 | Both | Close SOLIDWORKS documents. In Explorer, delete `2026-Robot\80_Unsorted\pvc1intube.SLDPRT`. **Tools → Restore Deleted Files** | Lists `pvc1intube.SLDPRT` → Restore → it's back | ☐ |

## 3. Together (the important part)

This is the test that proves nobody can overwrite anyone.

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 3.1 | Both | **Update** (documents closed), then **Open Robot** | Each of you gets the other's spacer and changes from section 2 | ☐ |
| 3.2 | A | Select `ABS_MotorCover_v2` → **Edit** | Locked by imdad | ☐ |
| 3.3 | B | Select `ABS_MotorCover_v2` → **Edit** | Refused: "Locked by imdad". B's panel: 🔒 **Locked by imdad** | ☐ |
| 3.4 | B | Open `ABS_MotorCover_v2`, change something without Edit | A warning right away that imdad is editing it. Close without saving | ☐ |
| 3.5 | A | Change `ABS_MotorCover_v2`, save, **Submit** | Submitted | ☐ |
| 3.6 | B | Wait up to 3 minutes (or **Check now** in the panel) | "⬇ 1 update available", with A's comment | ☐ |
| 3.7 | B | Close documents → **Update** → **Open Robot** → **Edit** `ABS_MotorCover_v2` | Has A's change; the lock now works for B. **Tools → Release Edit** | ☐ |
| 3.8 | B | **Edit** `TPU_MotorCover_v2`, change it, **save**, don't Submit | | ☐ |
| 3.9 | A | Web **Locks** → **Release** B's `TPU_MotorCover_v2` lock. Then in SOLIDWORKS, **Edit** `TPU_MotorCover_v2` | A now holds it | ☐ |
| 3.10 | B | **Submit** | A yellow note: "TPU_MotorCover_v2.SLDPRT can't be submitted … imdad is editing it". It doesn't block anything else. **Cancel** | ☐ |
| 3.11 | B | Close it. **Tools → Set Aside My Changes** → check it → Set aside | Copy saved in `C:\JOCO-ROBOS\Set Aside\…`; the team's version is back. A: **Release Edit** it | ☐ |
| 3.12 | B | Change and save `TPU_MotorCover_v2` again without Edit, then **Set Aside** it again right away | A second, separate folder in `Set Aside` (no error) | ☐ |

## 4. Library and FRCDesignLib

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 4.1 | A | Open `battery holder` → **Edit**. Panel → **Library** tab → type `breaker` | Results with pictures as you type; making the panel wider or taller makes everything bigger | ☐ |
| 4.2 | A | Click **120A Main Breaker** → **Insert** | First time: "Preparing…" → "Adding… to the team Library" → inserted at the origin. **Tell B whether SOLIDWORKS asked any import questions** | ☐ |
| 4.3 | A | Explorer: `C:\JOCO-ROBOS\Library\FRCDesignLib\Control System\` and `2026-Robot\90_COTS\FRCDesignLib\Control System\` | `120A Main Breaker.SLDPRT` in both | ☐ |
| 4.4 | B | Open `motor cover asssewmbly` → **Edit** → Library tab → `kraken` → **Kraken X44** → set **Back Cap Type** to *ReFire Powerpole*, then *Standard* | "Include ReFire Board Case" appears only for ReFire Powerpole | ☐ |
| 4.5 | B | **Insert** (with *Standard*) | Prepared, then inserted as one part: `Kraken X44 Brushless Motor (Standard).SLDPRT` | ☐ |
| 4.6 | A | Insert the same Kraken with the same options into `battery holder` | **Instant**: no "Preparing" (it's in the team Library now) | ☐ |
| 4.7 | Both | At the same moment, both pick **Kraken X60** (default options) and click **Insert** | One of you prepares it; the other gets "being prepared by …". The second try is instant | ☐ |
| 4.7b | Both | Search `flanged radial bearing` → change its size options | Some choices disappear or reappear as other options change, like in FRCDesignApp | ☐ |
| 4.8 | A | Panel → **Team Library…** → pick any Library part | Copied into `90_COTS` and inserted | ☐ |
| 4.9 | B | Download any vendor `.SLDPRT` (for example from McMaster) → **Tools → Insert External Part** | Lands in `90_COTS\Imported\<name>\` and inserts | ☐ |
| 4.10 | B | Save a part to your **Desktop**, insert it the normal way (Insert → Component), save → **Submit**, type a comment | "motor cover asssewmbly.SLDASM uses a file outside 2026-Robot", Submit greyed out | ☐ |
| 4.11 | B | Click **Import into robot** in that window | "Copied 1 outside file(s) into 2026-Robot\90_COTS…", the part appears under New, the comment is kept. If it asks to save, **Save it and continue**. Then **Submit** | ☐ |
| 4.12 | Both | Save and **Submit** everything from this section | Submitted | ☐ |

## 5. Mentor web page (A, B watching)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 5.1 | A | **Seasons** → Recent submits | Both of your submits with comments | ☐ |
| 5.2 | A | **Undo…** on one of B's submits → **Undo rN** | "Undid rN as new revision" | ☐ |
| 5.3 | B | **Update** | That change is gone again | ☐ |
| 5.4 | A | **Library** tab | "FRCDesignLib imports": Main Breaker, Kraken X44 (Standard), Kraken X60, with who imported each | ☐ |
| 5.5 | A | **Accounts** → **New setup code** for B | A new code appears | ☐ |
| 5.6 | B | **Tools → Test Connection** | Fails: password not accepted | ☐ |
| 5.7 | B | **Tools → Sign In** → "First time? Set up your account…" → new code + a new password | Works again | ☐ |
| 5.8 | B | **Tools → Change Password** | Changed; SOLIDWORKS doesn't ask again later | ☐ |

## 6. Add-in update (A, about 10 minutes)

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 6.1 | A | Ask Claude to release a new patch version (or bump `<Version>` and push a tag yourself) → when GitHub finishes: **Add-in** → **Release to students** | "Published add-in …" | ☐ |
| 6.2 | Both | Accept the update offer, approve Windows, close SOLIDWORKS | SOLIDWORKS reopens with the new version in the panel header; no further offer | ☐ |
| 6.3 | A | **Add-in** tab | "2 of 2 students … have x.y.z" | ☐ |

## 7. When things go wrong

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 7.1 | B | **Edit** a part, change it, save. Turn off Wi-Fi → **Submit** | "Submit did not complete. Your edits and locks are kept" | ☐ |
| 7.2 | B | Wi-Fi on → **Submit** | Submitted. A: Recent submits shows it **once** | ☐ |
| 7.3 | A | **Edit** a part, then end SOLIDWORKS in Task Manager. Restart, Open Robot | Still locked by you; Submit or Release Edit works | ☐ |
| 7.4 | Both | Save a change without submitting, then close SOLIDWORKS | Asks once "Submit now before exiting?" → **No** closes normally (tell A if nothing was asked) | ☐ |
| 7.5 | Both | Wi-Fi off → **Update**, **Edit** | Clear errors; nothing becomes writable; the panel says "Can't reach the server". Wi-Fi back on | ☐ |

## 8. Finish

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| 8.1 | Both | Submit or **Set Aside** anything left over; **Release Edit** anything still locked | The panel shows no unsubmitted changes and no locked files | ☐ |
| 8.2 | A | Web **Seasons** → Recent submits → **Undo…** each test submit after r____ (step 0.5), **newest first** | Each says "Undid rN as new revision". If one is refused, undo the newer one first | ☐ |
| 8.3 | Both | Close documents → **Update** → **Open Robot** | The robot is exactly as before testing; test spacers and inserted parts are gone | ☐ |
| 8.4 | A | Web **Locks** | No test locks left. (FRCDesignLib parts imported during testing stay in the team Library, where they're useful) | ☐ |

---

## Problems found

| Step | Who | What happened (exact message, or a screenshot name) |
|---|---|---|
| | | |
| | | |
| | | |

Send this list to Claude; each problem gets fixed and you retest just that step.
