using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Watches team CAD documents (including assembly components) for their first change and for closing.
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
        private readonly Dictionary<string, Watched> documents = new Dictionary<string, Watched>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Forms.Timer settleCheck = new System.Windows.Forms.Timer { Interval = 2000 };

        internal DocumentWatcher(SldWorks application, Func<string, bool> isTeamFile, Action<ModelDoc2> firstChange, Action<string> closed)
        {
            this.application = application;
            this.isTeamFile = isTeamFile;
            this.firstChange = firstChange;
            this.closed = closed;
            application.DocumentLoadNotify2 += OnLoad;
            settleCheck.Tick += (s, e) => CheckSettled();
            settleCheck.Start();
        }

        // A student who edits within the first seconds would otherwise never be asked. Once a document has settled,
        // a part or drawing that changed meanwhile and is still unsaved and read-only gets the question after all.
        // Assemblies are skipped: loading and rebuilding them is exactly what marks them changed by itself.
        private void CheckSettled()
        {
            foreach (var watched in new List<Watched>(documents.Values)) // A close event may change the list.
            {
                try
                {
                    if (watched.Reported || !watched.ChangedWhileSettling || DateTime.UtcNow - watched.LoadedAt < Settle) continue;
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
                if (String.IsNullOrEmpty(path) || documents.ContainsKey(path) || !isTeamFile(path)) return 0;
                var doc = application.GetOpenDocumentByName(path) as ModelDoc2;
                if (doc != null) Hook(doc, path);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        private void Hook(ModelDoc2 doc, string path)
        {
            var watched = new Watched { Doc = doc, Path = path, LoadedAt = DateTime.UtcNow };
            switch (doc.GetType())
            {
                case (int)swDocumentTypes_e.swDocPART:
                    var part = (PartDoc)doc;
                    DPartDocEvents_ModifyNotifyEventHandler partModify = () => Modified(watched);
                    DPartDocEvents_DestroyNotify2EventHandler partDestroy = type => Destroyed(watched, type);
                    part.ModifyNotify += partModify;
                    part.DestroyNotify2 += partDestroy;
                    watched.Unhook = () => { part.ModifyNotify -= partModify; part.DestroyNotify2 -= partDestroy; };
                    break;
                case (int)swDocumentTypes_e.swDocASSEMBLY:
                    var assembly = (AssemblyDoc)doc;
                    DAssemblyDocEvents_ModifyNotifyEventHandler assemblyModify = () => Modified(watched);
                    DAssemblyDocEvents_DestroyNotify2EventHandler assemblyDestroy = type => Destroyed(watched, type);
                    assembly.ModifyNotify += assemblyModify;
                    assembly.DestroyNotify2 += assemblyDestroy;
                    watched.Unhook = () => { assembly.ModifyNotify -= assemblyModify; assembly.DestroyNotify2 -= assemblyDestroy; };
                    break;
                case (int)swDocumentTypes_e.swDocDRAWING:
                    var drawing = (DrawingDoc)doc;
                    DDrawingDocEvents_ModifyNotifyEventHandler drawingModify = () => Modified(watched);
                    DDrawingDocEvents_DestroyNotify2EventHandler drawingDestroy = type => Destroyed(watched, type);
                    drawing.ModifyNotify += drawingModify;
                    drawing.DestroyNotify2 += drawingDestroy;
                    watched.Unhook = () => { drawing.ModifyNotify -= drawingModify; drawing.DestroyNotify2 -= drawingDestroy; };
                    break;
                default:
                    return;
            }
            documents[path] = watched;
        }

        private int Modified(Watched watched)
        {
            try
            {
                if (watched.Reported || !watched.Doc.IsOpenedReadOnly()) return 0;
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
                documents.Remove(watched.Path);
                closed(watched.Path);
            }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO watcher: " + exception); }
            return 0;
        }

        public void Dispose()
        {
            settleCheck.Stop();
            settleCheck.Dispose();
            try { application.DocumentLoadNotify2 -= OnLoad; }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            foreach (var watched in documents.Values)
            {
                try { watched.Unhook(); }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); }
            }
            documents.Clear();
        }
    }
}
