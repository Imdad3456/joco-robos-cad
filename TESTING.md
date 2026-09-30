# Test plan

Work top to bottom on the Windows VM. Each step says what should happen. **If anything differs, stop and send the exact message text or a screenshot.** Don't delete or revert files to make a step pass.

Legend: 🧑 you in SOLIDWORKS · 🌐 admin web page (https://cad.imdad.stream/admin) · ⚙ PowerShell

## A. Install

1. Close SOLIDWORKS. If you ever used the development build, run `.\scripts\Unregister-Dev.ps1` as administrator first. Then install the newest `JOCO-ROBOS-CAD-Setup-x.y.z.exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest), or let the automatic updater bring you to the newest released version.
2. 🧑 Start SOLIDWORKS. The panel header shows the version you installed, without "(dev build)". Expect the toolbar to show **Open Robot, Update, Edit, Submit, Insert from Library**, and a **JOCO ROBOS CAD** tab (blue "J" icon) in the task pane on the right.
   - The pane shows **2027-Robot** and either **✓ Up to date** or **N updates available**.

## B. Basics (regression)

3. 🧑 Close all documents → **Update**. Expect "2027-Robot is at revision …". A new folder `C:\JOCO-ROBOS\Library` appears with Motors, Gearboxes, and similar folders.
4. 🧑 **Open Robot** → `full assembly.SLDASM` opens read-only.
5. 🧑 Click the `coxa` component in the tree → **Edit**. Expect "Locked by imdad … coxa.SLDPRT" plus the component tip. The pane (with coxa active) or the 🌐 **Locks** page shows the lock.
6. 🧑 Clear the selection and click the part again. **Tools → JOCO ROBOS CAD → Release Edit** works while it's unchanged.

## C. Submit

7. 🧑 Open `coxa.SLDPRT` in its own window → **Edit** → change a dimension → **Save**.
8. 🧑 Open `leg.SLDASM` → **Edit**. New part → save it as `C:\JOCO-ROBOS\2027-Robot\00_Master\TestSpacer.SLDPRT`, insert it into the leg → **Save**.
9. 🧑 **Submit**. Expect Modified `coxa`, `leg` and New `TestSpacer`. Type a comment → Submit. Expect "2027-Robot: submitted as revision 3", and all three documents become read-only.
10. 🌐 **Seasons** page → *Recent submits* shows your comment and the files.
11. Refusals (each must upload nothing):
    - 🧑 Edit a file, change it, don't save → Submit. Expect "Save these documents first".
    - 🧑 In the dialog, uncheck a new part the assembly uses. Expect "uses the new file … unchecked".
    - 🧑 Insert a part saved on the Desktop into a locked assembly → Submit. Expect "uses a file outside 2027-Robot".

## D. Library

12. 🌐 **Library** → upload any small part into `Hardware`.
13. 🧑 **Update** (with the documents from 2027-Robot closed, or just Library docs closed). The part appears in `C:\JOCO-ROBOS\Library\Hardware`.
14. 🧑 Open `leg.SLDASM` → **Edit** → **Insert from Library** → pick the part. Expect it copied to `2027-Robot\90_COTS\Hardware\` and inserted at the origin. Save → **Submit** lists it as New.
15. 🧑 Insert the same part again. It must reuse the existing copy, not make a second file.
16. 🌐 **Library → Copy a robot part into the library** → pick `tibia.SLDPRT` → folder `Mechanisms`. Then 🧑 **Update** shows it in the Library.

## E. Two students (the CacheCAD test)

17. 🌐 **Accounts** → add `testkid` with a 10+ character password.
18. Create a second Windows user on the VM (or use a second VM), sign in there as `testkid`, and install the newest release (step 24). Installing is per-PC, so each Windows user only needs to sign in.
19. As **imdad**: Edit `femur.SLDPRT`. As **testkid**: open `femur` → Edit. Expect "Locked by imdad". The pane shows 🔒 **Locked by imdad**, and the file stays read-only.
20. As **imdad**: change `femur`, save, Submit. As **testkid**: the pane shows "1 update available — imdad: …" within 3 minutes (or click *Check now*) → close docs → **Update** → **Edit** `femur` now succeeds.
21. 🌐 **Locks** → release testkid's `femur` lock. testkid's Submit of femur must now be refused (their lock is gone).

## F. Seasons

22. 🌐 **Seasons** → create `2028-Robot` and **don't** make it active. 🧑 Nothing changes for students.
23. 🌐 Make `2028-Robot` active → 🧑 **Open Robot** says "Switched to 2028-Robot" and creates `C:\JOCO-ROBOS\2028-Robot` (empty folders). **Tools → Choose Robot** can pick 2027 again. While 2028 is current, **Tools → Open Old Robot** → 2027 opens `full assembly` read-only without updating; the pane says "Reference copy from 2027-Robot", Edit is refused with a Library hint, and Open Old Robot/Open Robot refuse while the other season's documents are open. 🌐 Make **2027** active again, then **Delete (empty)** on 2028.

## G. Installer

24. Download the newest setup file from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest). Check that its size matches the one shown on the release page; a partial download won't run.
25. Run the setup file. Expect the Windows "unknown publisher" warning → More info → Run anyway. With SOLIDWORKS open it asks you to close it. After install, start SOLIDWORKS: the add-in loads without visiting Tools → Add-Ins.
26. Uninstall from Windows Settings → Apps. The add-in is gone, and `C:\JOCO-ROBOS` is untouched.

## G2. Automatic updates

- ⚙ Bump `<Version>` in `src/JocoRobos.Cad/JocoRobos.Cad.csproj`, commit, and push a matching tag (for example `v0.6.7`). GitHub builds it and stages it on the server.
- 🌐 On the **Add-in** page, under "Waiting from GitHub", click **Release to students**.
- 🧑 Within 3 minutes (or after *Check now*), SOLIDWORKS asks "x.y.z is available". Answer Yes → Windows permission prompt (it may be behind SOLIDWORKS) → "Update is ready". Close SOLIDWORKS; it should reopen by itself within about a minute. The panel header shows the new version, and no update is offered anymore.
- If the version didn't change, the add-in says so at the next start and points to the installer log in `%LOCALAPPDATA%\JocoRobos.Cad\updates`.
- 🌐 Release the next version as **Required**. 🧑 Clicking Edit without updating is refused with the install prompt.

## G3. Changes without Edit

- 🧑 Open a part (read-only), change a dimension without clicking Edit, then click **Edit**. A backup appears in `C:\JOCO-ROBOS\Set Aside\…`, and the file is locked. Tell me whether the message says your changes are still in the window or that SOLIDWORKS reloaded it.
- 🧑 As testkid, Edit a part. As imdad, change the same part without Edit, then click Edit. It's refused with "Locked by testkid" plus the backup location.
- 🧑 Close SOLIDWORKS. In Explorer, clear read-only on a part, change it in SOLIDWORKS, and save. **Submit** lists it as "not locked yet; Submit locks it" and submits it.
- 🧑 Repeat while testkid holds the lock: Submit shows it under "Cannot be submitted". **Tools → Set Aside My Changes** (with the document closed) saves your copy, restores the team's version, and Update works again.

## G4. Lock on first change, release on close

- 🧑 **File → Open** a robot part (not locked). Wait 10 seconds, then change a dimension. Expect "You're changing … Lock it for editing now?" → Yes → it's locked and writable, and your change is still there.
- 🧑 As testkid, Edit a part. As imdad, open it, wait, and change it. Expect a warning right away that testkid is editing it.
- 🧑 Edit a part, don't change it, close it. Within a few seconds the pane's "Your locked files" no longer lists it, and 🌐 Locks doesn't show it.
- 🧑 Open the whole robot and just look around (rotate, zoom, open subassemblies). You should **not** get any lock questions. Tell me if you do.

## I. New in 0.7

- 🧑 **Unsubmitted reminder:** change and save a locked part. The panel shows "⚠ 1 saved change not submitted". Close SOLIDWORKS: it asks once whether to Submit first. "No" closes normally, and the lock stays.
- 🧑 **Read-only warning:** change a part without Edit and answer **No** to "Lock it now?". The panel shows "⚠ Unsaved changes in a read-only file".
- 🧑 **Other computer:** Edit a part on the VM, then try Edit on the same part while signed in as imdad on another PC (or after deleting and re-downloading the robot folder). Expect "You already locked this file from another computer".
- 🧑 **Duplicate names:** save a new part as `C:\JOCO-ROBOS\2027-Robot\20_Intake\coxa.SLDPRT` (same name as an existing part), then Submit. Expect a "same name" refusal.
- 🧑 **Lightweight:** set the robot to open lightweight (Tools → Options → Performance), select a component, and click Edit. It resolves and locks.
- 🧑 **Restore:** close SOLIDWORKS, delete a part in Explorer, then **Tools → Restore Deleted Files**. It comes back read-only.
- 🧑 **External parts:** download any vendor STEP/SLDPRT (for example a REV part) → **Tools → Insert External Part**. It lands in `90_COTS\Imported\<name>` and inserts. Then insert a Desktop part the normal way → Submit refuses → **Tools → Import Outside References** fixes it.
- 🌐 **Undo:** on Recent submits, click **Undo…** on a test submit → **Undo rN**. After Update, students have the previous versions. Undo is refused while someone has one of those files locked.
- 🌐 **Health:** the Seasons tab shows Deck backup, off-device backup (both today), and disk space.
- 🌐 **Versions:** the Accounts tab shows your add-in version, last seen, and computer name. The Add-in tab says how many students run the published version.
- 🌐 **Season change with work outstanding:** with a saved-but-unsubmitted change in 2027, create and activate `2028-Robot`. The page notes your lock. 🧑 Open Robot keeps you on 2027 with an explanation → Submit → the next Open Robot switches you to 2028. Then 🌐 re-activate 2027 and delete 2028.
- 🧑 **Shared PC:** sign in to Windows as a second user and install. That user's robot goes to `C:\Users\<them>\JOCO-ROBOS`, and they can't open the first user's `C:\JOCO-ROBOS`.

## J. FRCDesignLib (new in 0.8)

1. 🧑 Open the robot, select an assembly, and click **Edit**. In the panel, click the **Library** tab → type `bearing`. Results with small pictures appear as you type.
2. 🧑 Click a simple non-configurable part (for example `120A Main Breaker`). You get a big picture, vendor, and part number. Click **Insert**.
   - First time: "Preparing … (first time only)", then "Adding … to the team Library", and the part is inserted at the origin.
   - Check that `C:\JOCO-ROBOS\Library\FRCDesignLib\Control System\120A Main Breaker.SLDPRT` exists, plus a copy in `2027-Robot\90_COTS\FRCDesignLib\Control System\`.
   - Tell me whether SOLIDWORKS showed any import questions or diagnostics.
3. 🧑 Insert the same part again: instant, no "Preparing".
4. 🧑 Search `kraken` → **Kraken X44** → change **Back Cap Type** to *Standard*. **Include ReFire Board Case** appears only for *ReFire Powerpole*. Click Insert. It arrives as one part named `Kraken X44 Brushless Motor (Standard).SLDPRT`.
5. 🧑 Save → **Submit**: the new files in `90_COTS` are listed as New.
6. 🌐 **Library** tab: "FRCDesignLib imports" lists both parts with who imported them; exports today is 2.
7. 🧑 As testkid, insert the Kraken with the same options. It comes from the Library (no export); the export count doesn't change.
8. 🧑 Turn off the network and click Insert: a clear error, and nothing changes in the robot or Library.

## H. Failure cases

27. 🧑 Edit + change + save a part. Turn off the VM's network → Submit. Expect "Submit did not complete. Your edits and locks are kept". Network back on → Submit succeeds once. 🌐 Recent submits shows exactly one revision.
28. 🧑 Network off → Update / Edit. Clear errors, and nothing becomes writable. The pane says "Can't reach the server".
29. 🧑 Edit a part, then kill SOLIDWORKS in Task Manager. Restart → the part is still locked by you, and Edit/Submit still work.
30. 🧑 Open the largest real assembly you have, then time **Update** and a **Submit** of a large part through Cloudflare. Note the times and any errors (the upload limit on the web page is 100 MB, but Submit has no such limit—report if it fails).
