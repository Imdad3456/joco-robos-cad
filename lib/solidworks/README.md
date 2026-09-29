# SOLIDWORKS API files for automated builds

GitHub's build machines don't have SOLIDWORKS. Put these three files from a SOLIDWORKS 2026 installation here, once:

- `SolidWorks.Interop.sldworks.dll`
- `SolidWorks.Interop.swconst.dll`
- `SolidWorks.Interop.swpublished.dll`

They are in `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`. That folder is SOLIDWORKS' redistributable API, which add-in installers ship anyway. Keep this repository private.

Easiest way: on GitHub, open this `lib/solidworks` folder → **Add file → Upload files** → drag the three DLLs in → **Commit changes**. Replace them when the team moves to a newer SOLIDWORKS version.
