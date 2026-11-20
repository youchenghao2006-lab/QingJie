using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunLifecycleTests()
    {
        Run("closing to background preserves editor annotations queue and board contents", delegate
        {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Pattern(180, 120))
            {
                AppSettings settings = (AppSettings)PrivateField(main, "settings");
                settings.Closing = CloseBehavior.Background;
                InvokePrivate(main, "SetCurrentImage", image);
                AnnotationCanvas editor = (AnnotationCanvas)PrivateField(main, "canvas");
                CommitLifecycleRectangle(editor);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                workspace.AddImage(image);
                InvokePrivate(main, "RefreshBoardSource");
                long sourceId = (long)PrivateField(main, "currentSourceId");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                using (Bitmap editorBefore = editor.ExportImage())
                using (Bitmap boardBefore = board.ExportImage())
                {
                    FormClosingEventArgs closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                    InvokePrivate(main, "OnFormClosing", closing);
                    Assert(closing.Cancel && !main.IsDisposed && !main.Visible &&
                        (bool)PrivateField(main, "backgroundMode"), "Background close destroyed or restored the main window.");
                    Assert(editor.CanUndo && workspace.ContainsSourceId(sourceId) && board.ImageCount == 1,
                        "Background close discarded editor history, queue, or composition.");
                    using (Bitmap editorAfter = editor.ExportImage())
                    using (Bitmap boardAfter = board.ExportImage())
                    {
                        FastPixelsEqual(editorBefore, editorAfter, "background editor preservation");
                        FastPixelsEqual(boardBefore, boardAfter, "background board preservation");
                    }
                    ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                    FastPixelsEqual(editorBefore, queue.Find(sourceId).Image, "background queue preservation");
                    Assert(!main.IsHandleCreated, "Lifecycle test registered a real global hotkey.");
                }
            }
        });

        Run("settings default to F2 persist F1 or combinations and reject invalid shortcut records", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                string path = Path.Combine(directory, "settings.ini");
                AppSettings first = AppSettings.Load(path);
                Equal(Keys.F2, first.CaptureShortcut);
                Equal(CloseBehavior.Ask, first.Closing);
                first.CaptureShortcut = Keys.Control | Keys.Alt | Keys.K;
                first.Closing = CloseBehavior.Background;
                first.Save();
                AppSettings restored = AppSettings.Load(path);
                Equal(first.CaptureShortcut, restored.CaptureShortcut);
                Equal(CloseBehavior.Background, restored.Closing);
                Equal("Ctrl + Alt + K", restored.ShortcutText);
                Equal(0x4003U, restored.NativeModifiers);
                restored.CaptureShortcut = Keys.F1;
                restored.Closing = CloseBehavior.Exit;
                restored.Save();
                Equal(Keys.F1, AppSettings.Load(path).CaptureShortcut);
                Equal(CloseBehavior.Exit, AppSettings.Load(path).Closing);
                foreach (Keys key in new Keys[] { Keys.None, Keys.A, Keys.Shift | Keys.D5,
                    Keys.ControlKey, Keys.Alt | Keys.Tab, Keys.Escape, Keys.Control | Keys.Return,
                    Keys.LWin, (Keys)unchecked((int)0x80000071) })
                    Assert(!AppSettings.IsValidShortcut(key), "Accepted invalid capture shortcut " + key);
                foreach (Keys key in new Keys[] { Keys.F2, Keys.Shift | Keys.F9, Keys.Control | Keys.A,
                    Keys.Alt | Keys.D2, Keys.Control | Keys.Shift | Keys.Space })
                    Assert(AppSettings.IsValidShortcut(key), "Rejected supported capture shortcut " + key);
                File.WriteAllText(path, "CaptureShortcut=" + (int)Keys.A + "\r\nCloseBehavior=99\r\n");
                AppSettings invalid = AppSettings.Load(path);
                Equal(Keys.F2, invalid.CaptureShortcut);
                Equal(CloseBehavior.Ask, invalid.Closing);
            });
        });

        Run("configured capture shortcut is consumed without capture while modal busy closing or disabled", delegate
        {
            using (MainForm main = NewHiddenMain())
            {
                AppSettings settings = (AppSettings)PrivateField(main, "settings");
                settings.CaptureShortcut = Keys.Control | Keys.Alt | Keys.F8;
                InvokePrivate(main, "EnsureBoard");
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                for (int state = 0; state < 5; state++)
                {
                    if (state == 0) SetPrivateField(main, "capturing", true);
                    if (state == 1) SetPrivateField(main, "dialogOpen", true);
                    if (state == 2) SetPrivateField(workspace, "sourceDragging", true);
                    if (state == 3) SetPrivateField(main, "waitingForArchives", true);
                    if (state == 4) main.Enabled = false;
                    object[] arguments = { new Message(), settings.CaptureShortcut };
                    bool handled = (bool)typeof(MainForm).GetMethod("ProcessCmdKey",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, arguments);
                    Assert(handled, "The configured shortcut leaked through a busy guard.");
                    Assert(PrivateField(main, "pendingCapture") == null,
                        "A busy shortcut started a delayed or real screen capture.");
                    Equal(state == 0, (bool)PrivateField(main, "capturing"));
                    if (state == 0) SetPrivateField(main, "capturing", false);
                    if (state == 1) SetPrivateField(main, "dialogOpen", false);
                    if (state == 2) SetPrivateField(workspace, "sourceDragging", false);
                    if (state == 3) SetPrivateField(main, "waitingForArchives", false);
                    if (state == 4) main.Enabled = true;
                }
                Assert(!main.IsHandleCreated, "Shortcut guard test registered a global hotkey.");
            }
        });

        Run("delayed history completion marks screenshot A saved after the editor switches to B", delegate
        {
            RunDelayedHistoryLifecycle(false);
        });

        Run("delayed history completion cannot mark a newer edited screenshot revision saved", delegate
        {
            RunDelayedHistoryLifecycle(true);
        });

        Run("quick editor handoff follows its existing archive result without marking failed or newer revisions saved", delegate
        {
            for (int scenario = 0; scenario < 3; scenario++)
            {
                SynchronizationContext previous = SynchronizationContext.Current;
                LifecycleContext context = new LifecycleContext();
                TaskCompletionSource<bool> archive = new TaskCompletionSource<bool>();
                try
                {
                    using (MainForm main = NewHiddenMain())
                    using (Bitmap image = Pattern(180, 120))
                    {
                        SynchronizationContext.SetSynchronizationContext(context);
                        InvokePrivate(main, "SetCurrentImage", image);
                        long sourceId = (long)PrivateField(main, "currentSourceId");
                        BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                        Dictionary<long, long> versions =
                            (Dictionary<long, long>)PrivateField(main, "sourceVersions");
                        InvokePrivate(main, "MarkSourceAfterArchive", archive.Task, sourceId, versions[sourceId]);
                        Assert(!workspace.IsSourceSaved(sourceId), "An unfinished quick archive was reported as saved.");
                        if (scenario == 2)
                            CommitLifecycleRectangle((AnnotationCanvas)PrivateField(main, "canvas"));
                        archive.SetResult(scenario != 1);
                        Stopwatch watch = Stopwatch.StartNew();
                        while (context.Finished == 0 && watch.ElapsedMilliseconds < 5000)
                        {
                            context.Pump();
                            Thread.Sleep(1);
                        }
                        Assert(context.Finished > 0, "Quick handoff never observed its archive completion.");
                        Equal(scenario == 0, workspace.IsSourceSaved(sourceId));
                        Equal(scenario != 0, (bool)PrivateField(main, "dirty"));
                        Equal(1, workspace.SourceCount);
                        Assert(!main.IsHandleCreated, "Quick handoff test created a visible or hotkey-owning form.");
                    }
                }
                finally
                {
                    archive.TrySetResult(false);
                    context.Pump();
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }
        });
    }

    private static void RunDelayedHistoryLifecycle(bool editBeforeCompletion)
    {
        InHistoryDirectory(delegate(string directory)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            LifecycleContext context = new LifecycleContext();
            TaskCompletionSource<int> blocker = new TaskCompletionSource<int>();
            try
            {
                using (MainForm main = NewHiddenMain())
                using (HistoryStore store = new HistoryStore(directory))
                using (Bitmap first = Pattern(180, 120))
                using (Bitmap next = Solid(140, 100, Color.SteelBlue))
                {
                    ((HistoryStore)PrivateField(main, "history")).Dispose();
                    SetPrivateField(main, "history", store);
                    typeof(HistoryStore).GetField("tail", BindingFlags.Instance | BindingFlags.NonPublic)
                        .SetValue(store, blocker.Task);
                    // A deterministic test context applies async callbacks on this
                    // STA thread; no form handle, desktop capture, or native hotkey.
                    SynchronizationContext.SetSynchronizationContext(context);
                    InvokePrivate(main, "AcceptCapture", first);
                    long firstId = (long)PrivateField(main, "currentSourceId");
                    BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                    Assert(!workspace.IsSourceSaved(firstId), "The blocked archive was marked saved early.");
                    AnnotationCanvas editor = (AnnotationCanvas)PrivateField(main, "canvas");
                    if (editBeforeCompletion) CommitLifecycleRectangle(editor);
                    using (Bitmap expectedQueueImage = editor.ExportImage())
                    {
                        InvokePrivate(main, "SetCurrentImage", next);
                        long nextId = (long)PrivateField(main, "currentSourceId");
                        Dictionary<long, long> versions =
                            (Dictionary<long, long>)PrivateField(main, "sourceVersions");
                        Equal(editBeforeCompletion ? 1L : 0L, versions[firstId]);
                        blocker.SetResult(0);
                        Stopwatch timer = Stopwatch.StartNew();
                        while (context.Executed == 0 && timer.ElapsedMilliseconds < 5000)
                        {
                            context.Pump();
                            Thread.Sleep(1);
                        }
                        Assert(context.Executed > 0, "The archived screenshot completion never reached the UI context.");
                        store.FlushAsync().GetAwaiter().GetResult();
                        Equal(!editBeforeCompletion, workspace.IsSourceSaved(firstId));
                        Assert(!workspace.IsSourceSaved(nextId) && (bool)PrivateField(main, "dirty"),
                            "Saving A incorrectly marked B saved.");
                        ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                        FastPixelsEqual(expectedQueueImage, queue.Find(firstId).Image, "history completion preserves queue revision");
                        FastPixelsEqual(next, queue.Find(nextId).Image, "history completion preserves current screenshot");
                        using (Bitmap stored = store.LoadImage(store.List()[0].Path))
                            FastPixelsEqual(first, stored, "queued history saves the original accepted revision");
                        Assert(!main.IsHandleCreated, "History completion test registered a real hotkey.");
                    }
                }
            }
            finally
            {
                blocker.TrySetResult(0);
                context.Pump();
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
    }

    private static void CommitLifecycleRectangle(AnnotationCanvas canvas)
    {
        Type shapeType = typeof(AnnotationCanvas).GetNestedType("RectangleAnnotation", BindingFlags.NonPublic);
        object annotation = Activator.CreateInstance(shapeType, new object[] {
            new PointF(15, 18), new PointF(135, 85), Color.Lime, 5F
        });
        foreach (MethodInfo method in typeof(AnnotationCanvas).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            if (method.Name == "Commit" && method.GetParameters().Length == 1)
            {
                method.Invoke(canvas, new object[] { annotation });
                return;
            }
        throw new Exception("Could not commit the synthetic lifecycle annotation.");
    }

    private sealed class LifecycleContext : SynchronizationContext
    {
        private sealed class Work
        {
            internal SendOrPostCallback Callback;
            internal object State;
        }
        private readonly Queue<Work> pending = new Queue<Work>();
        internal int Executed;
        internal int Finished;
        public override SynchronizationContext CreateCopy() { return this; }
        public override void OperationCompleted() { Interlocked.Increment(ref Finished); }
        public override void Post(SendOrPostCallback callback, object state)
        {
            lock (pending) pending.Enqueue(new Work { Callback = callback, State = state });
        }
        internal void Pump()
        {
            while (true)
            {
                Work next;
                lock (pending)
                {
                    if (pending.Count == 0) return;
                    next = pending.Dequeue();
                }
                next.Callback(next.State);
                Executed++;
            }
        }
    }
}
