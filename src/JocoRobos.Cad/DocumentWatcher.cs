using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Watches team CAD documents (including assembly components) for their first change and for closing,
    /// and every document (new ones too) for saving, since Save As can turn any of them into a team file.
    /// Every handler swallows its own errors: a watcher problem must never disturb SOLIDWORKS.
    /// </summary>
    internal sealed class DocumentWatcher : IDisposable
    {
        private sealed class Watched
        {
            internal ModelDoc2 Doc;
            internal string Path;
            internal DateTime LoadedAt;
            internal bool Reported;
            internal bool ChangedWhileSettling;
            internal Action Unhook;
        }

        // Opening and rebuilding can mark a document changed without the student doing anything.
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);
        private readonly SldWorks application;
        private readonly Func<string, bool> isTeamFile;
        private readonly Action<ModelDoc2> firstChange;
        private readonly Action<string> closed;
        private readonly Action<string> saved;
        private readonly List<Watched> documents = new List<Watched>();
        private readonly System.Windows.Forms.Timer settleCheck = new System.Windows.Forms.Timer { Interval = 2000 };

        // Set while CAD Hub saves a copy elsewhere (recovery and safety copies): the document itself wasn't saved.
        internal bool Quiet;
        // A watched team document went from saved to changed (the panel's "unsaved").
        internal Action Dirtied;

        internal DocumentWatcher(SldWorks application, Func<string, bool> isTeamFile, Action<ModelDoc2> firstChange, Action<string> closed, Action<string> saved)
        {
            this.application = application;
            this.isTeamFile = isTeamFile;
            this.firstChange = firstChange;
            this.closed = closed;
            this.saved = saved;
            application.DocumentLoadNotify2 += OnLoad;
            application.FileNewNotify2 += OnNew;
            settleCheck.Tick += (s, e) => CheckSettled();
            settleCheck.Start();
        }

        // A student who edits within the first seconds would otherwise never be asked. Once a document has settled,
        // a part or drawing that changed meanwhile and is still unsaved and read-only gets the question after all.
        // Assemblies are skipped: loading and rebuilding them is exactly what marks them changed by itself.
        private void CheckSettled()
        {
            foreach (var watched in new List<Watched>(documents)) // A close event may change the list.
            {
                try
                {
                    if (watched.Reported || !watched.ChangedWhileSettling || DateTime.UtcNow - watched.LoadedAt < Settle) continue;
                    if (!IsTeam(watched)) continue;
                    watched.ChangedWhileSettling = false;
                    if (watched.Doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY) continue;
                    if (watched.Doc.GetSaveFlag() && watched.Doc.IsOpenedReadOnly())
                    {
                        watched.Reported = true;
                        firstChange(watched.Doc);
                    }
                }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            }
        }

        private int OnLoad(string title, string path)
        {
            try
            {
                if (String.IsNullOrEmpty(path)) return 0;
                var doc = application.GetOpenDocumentByName(path) as ModelDoc2;
                if (doc != null) Hook(doc, path);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        // File → New: not a team file yet, but Save As can put it in the robot.
        private int OnNew(object newDoc, int type, string template)
        {
            try
            {
                var doc = newDoc as ModelDoc2;
                if (doc != null) Hook(doc, doc.GetPathName());
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        // Where the document is now: Save As moves it.
        private bool IsTeam(Watched watched)
        {
            string path = watched.Doc.GetPathName();
            return !String.IsNullOrEmpty(path) && isTeamFile(path);
        }

        private void Hook(ModelDoc2 doc, string path)
        {
            if (documents.Exists(w => ReferenceEquals(w.Doc, doc) || (!String.IsNullOrEmpty(path) && String.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase)))) return;
            var watched = new Watched { Doc = doc, Path = path, LoadedAt = DateTime.UtcNow };
            switch (doc.GetType())
            {
                case (int)swDocumentTypes_e.swDocPART:
                    var part = (PartDoc)doc;
                    DPartDocEvents_ModifyNotifyEventHandler partModify = () => Modified(watched);
                    DPartDocEvents_DestroyNotify2EventHandler partDestroy = type => Destroyed(watched, type);
                    DPartDocEvents_FileSavePostNotifyEventHandler partSave = (type, name) => Saved(watched, name);
                    part.ModifyNotify += partModify;
                    part.DestroyNotify2 += partDestroy;
                    part.FileSavePostNotify += partSave;
                    watched.Unhook = () => { part.ModifyNotify -= partModify; part.DestroyNotify2 -= partDestroy; part.FileSavePostNotify -= partSave; };
                    break;
                case (int)swDocumentTypes_e.swDocASSEMBLY:
                    var assembly = (AssemblyDoc)doc;
                    DAssemblyDocEvents_ModifyNotifyEventHandler assemblyModify = () => Modified(watched);
                    DAssemblyDocEvents_DestroyNotify2EventHandler assemblyDestroy = type => Destroyed(watched, type);
                    DAssemblyDocEvents_FileSavePostNotifyEventHandler assemblySave = (type, name) => Saved(watched, name);
                    assembly.ModifyNotify += assemblyModify;
                    assembly.DestroyNotify2 += assemblyDestroy;
                    assembly.FileSavePostNotify += assemblySave;
                    watched.Unhook = () => { assembly.ModifyNotify -= assemblyModify; assembly.DestroyNotify2 -= assemblyDestroy; assembly.FileSavePostNotify -= assemblySave; };
                    break;
                case (int)swDocumentTypes_e.swDocDRAWING:
                    var drawing = (DrawingDoc)doc;
                    DDrawingDocEvents_ModifyNotifyEventHandler drawingModify = () => Modified(watched);
                    DDrawingDocEvents_DestroyNotify2EventHandler drawingDestroy = type => Destroyed(watched, type);
                    DDrawingDocEvents_FileSavePostNotifyEventHandler drawingSave = (type, name) => Saved(watched, name);
                    drawing.ModifyNotify += drawingModify;
                    drawing.DestroyNotify2 += drawingDestroy;
                    drawing.FileSavePostNotify += drawingSave;
                    watched.Unhook = () => { drawing.ModifyNotify -= drawingModify; drawing.DestroyNotify2 -= drawingDestroy; drawing.FileSavePostNotify -= drawingSave; };
                    break;
                default:
                    return;
            }
            documents.Add(watched);
        }

        private int Modified(Watched watched)
        {
            try
            {
                if (!watched.Doc.IsOpenedReadOnly() && IsTeam(watched)) Dirtied?.Invoke();
                if (watched.Reported || !watched.Doc.IsOpenedReadOnly() || !IsTeam(watched)) return 0;
                if (DateTime.UtcNow - watched.LoadedAt < Settle) { watched.ChangedWhileSettling = true; return 0; }
                watched.Reported = true;
                firstChange(watched.Doc);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        private int Destroyed(Watched watched, int type)
        {
            try
            {
                if (type != (int)swDestroyNotifyType_e.swDestroyNotifyDestroy) return 0;
                watched.Unhook();
                documents.Remove(watched);
                if (!String.IsNullOrEmpty(watched.Path) && isTeamFile(watched.Path)) closed(watched.Path);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        private int Saved(Watched watched, string fileName)
        {
            try
            {
                if (Quiet) return 0;
                // After Save As the document has its new name; a "save as copy" leaves it unchanged.
                string path = watched.Doc.GetPathName();
                if (String.IsNullOrEmpty(path)) path = fileName;
                if (String.IsNullOrEmpty(path)) return 0;
                watched.Path = path;
                saved(path);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        public void Dispose()
        {
            settleCheck.Stop();
            settleCheck.Dispose();
            try { application.DocumentLoadNotify2 -= OnLoad; application.FileNewNotify2 -= OnNew; }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            foreach (var watched in documents)
            {
                try { watched.Unhook(); }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            }
            documents.Clear();
        }
    }
}
