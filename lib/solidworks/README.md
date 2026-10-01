# SOLIDWORKS API files

The add-in is built against three files from SOLIDWORKS' redistributable API folder,
`C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`:

- `SolidWorks.Interop.sldworks.dll`
- `SolidWorks.Interop.swconst.dll`
- `SolidWorks.Interop.swpublished.dll`

They belong to Dassault Systèmes, so they aren't in this public repository (and `.gitignore` keeps them out).

- **Building on a PC with SOLIDWORKS:** nothing to do. The project finds them in SOLIDWORKS' own folder.
- **GitHub's build machines** (no SOLIDWORKS): the workflow copies them from the private repository
  `Imdad3456/joco-build-files`, using the read-only deploy key in the secret `SOLIDWORKS_FILES_KEY`. When the team moves to a
  newer SOLIDWORKS, replace the three files there.
- **Building elsewhere** (Linux, a fork's CI): copy the three files here, or pass
  `-p:SolidWorksInteropDir=<folder with them>`.
