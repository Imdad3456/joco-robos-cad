# Test plan — v0.5

Work top to bottom on the Windows VM. Each step says what should happen. **If anything differs, stop and send the exact message text or a screenshot.** Don't delete or revert files to make a step pass.

Legend: 🧑 you in SOLIDWORKS · 🌐 admin web page (https://cad.imdad.stream/admin) · ⚙ PowerShell

## A. Install (dev build first, then the real installer)

1. ⚙ Close SOLIDWORKS. Download the latest ZIP from GitHub into `C:\Dev\joco-robos-cad`, then run `.\scripts\Build.ps1` and, as administrator, `.\scripts\Register-Dev.ps1`.
2. 🧑 Start SOLIDWORKS. Expect the toolbar to show **Open Robot, Update, Edit, Submit, Insert from Library**, and a **JOCO ROBOS CAD** tab (blue "J" icon) in the task pane on the right.
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
18. Create a second Windows user on the VM (or use a second VM), sign in there as `testkid`, and use the dev build or the installer from step 24.
19. As **imdad**: Edit `femur.SLDPRT`. As **testkid**: open `femur` → Edit. Expect "Locked by imdad". The pane shows 🔒 **Locked by imdad**, and the file stays read-only.
20. As **imdad**: change `femur`, save, Submit. As **testkid**: the pane shows "1 update available — imdad: …" within 3 minutes (or click *Check now*) → close docs → **Update** → **Edit** `femur` now succeeds.
21. 🌐 **Locks** → release testkid's `femur` lock. testkid's Submit of femur must now be refused (their lock is gone).

## F. Seasons

22. 🌐 **Seasons** → create `2028-Robot` and **don't** make it active. 🧑 Nothing changes for students.
23. 🌐 Make `2028-Robot` active → 🧑 **Open Robot** says "Switched to 2028-Robot" and creates `C:\JOCO-ROBOS\2028-Robot` (empty folders). **Tools → Choose Robot** can pick 2027 again. 🌐 Make **2027** active again, then **Delete (empty)** on 2028.

## G. Installer

24. ⚙ `winget install JRSoftware.InnoSetup`, reopen PowerShell, then run `.\scripts\Unregister-Dev.ps1` as admin and `.\scripts\Build-Installer.ps1`. Expect `installer\Output\JOCO-ROBOS-CAD-Setup-0.5.0.exe`.
25. Run the setup file. Expect the Windows "unknown publisher" warning → More info → Run anyway. With SOLIDWORKS open it asks you to close it. After install, start SOLIDWORKS: the add-in loads without visiting Tools → Add-Ins.
26. Uninstall from Windows Settings → Apps. The add-in is gone, and `C:\JOCO-ROBOS` is untouched.

## H. Failure cases

27. 🧑 Edit + change + save a part. Turn off the VM's network → Submit. Expect "Submit did not complete. Your edits and locks are kept". Network back on → Submit succeeds once. 🌐 Recent submits shows exactly one revision.
28. 🧑 Network off → Update / Edit. Clear errors, and nothing becomes writable. The pane says "Can't reach the server".
29. 🧑 Edit a part, then kill SOLIDWORKS in Task Manager. Restart → the part is still locked by you, and Edit/Submit still work.
30. 🧑 Open the largest real assembly you have, then time **Update** and a **Submit** of a large part through Cloudflare. Note the times and any errors (the upload limit on the web page is 100 MB, but Submit has no such limit—report if it fails).
