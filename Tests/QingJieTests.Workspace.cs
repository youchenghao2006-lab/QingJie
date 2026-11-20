using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunWorkspaceFeatureTests()
    {
        Run("embedded workspace owns source snapshots and clearing source preserves composition", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            {
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                using (Bitmap image = Pattern(100, 70))
                {
                    workspace.SetSourceImage(image);
                    workspace.AddImage(image);
                    Bitmap snapshot = (Bitmap)PrivateField(workspace, "sourceImage");
                    PixelsEqual(image, snapshot, Point.Empty);
                    int original = snapshot.GetPixel(30, 20).ToArgb();
                    image.SetPixel(30, 20, Color.Magenta);
                    Equal(original, snapshot.GetPixel(30, 20).ToArgb());
                }
                using (Bitmap exported = board.ExportImage()) Equal(board.BoardSize, exported.Size);
                workspace.SetSourceImage(null);
                Assert(PrivateField(workspace, "sourceImage") == null, "Null source retained an old snapshot.");
                Equal(1, board.ImageCount);
                Assert(board.CanUndo, "Clearing the left preview altered the board history.");
            }
        });
        Run("main switches to embedded workspace without opening a second form", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Pattern(100, 70))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                Assert(workspace != null && workspace.FindForm() == main, "Workspace is not embedded in main form.");
                Equal(0, main.OwnedForms.Length);
                Assert((bool)PrivateField(main, "boardMode"), "Opening workspace did not switch mode.");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                Equal(0, board.ImageCount);
                workspace.AddImage(image);
                InvokePrivate(main, "ShowScreenshotEditor");
                Assert(!(bool)PrivateField(main, "boardMode"), "Returning did not switch to screenshot editor.");
                InvokePrivate(main, "OpenBoard", false);
                Assert(Object.ReferenceEquals(workspace, PrivateField(main, "board")), "Returning replaced the board.");
                Equal(1, board.ImageCount);
                Equal(0, main.OwnedForms.Length);
            }
        });
        Run("embedded source refresh includes latest screenshot annotations without changing board", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Pattern(150, 110))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                workspace.AddImage(image);
                InvokePrivate(main, "ShowScreenshotEditor");
                AnnotationCanvas canvas = (AnnotationCanvas)PrivateField(main, "canvas");
                IntPtr canvasHandle = canvas.Handle;
                canvas.PerformLayout();
                canvas.Tool = DrawingTool.Rectangle;
                canvas.StrokeColor = Color.Lime;
                canvas.StrokeWidth = 5;
                Drag(canvas, new PointF(20, 20), new PointF(120, 80));
                using (Bitmap expected = canvas.ExportImage())
                {
                    Assert(ChangedPixels(image, expected) > 100, "Synthetic annotation did not modify screenshot.");
                    InvokePrivate(main, "OpenBoard", false);
                    Bitmap snapshot = (Bitmap)PrivateField(workspace, "sourceImage");
                    PixelsEqual(expected, snapshot, Point.Empty);
                }
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                Equal(1, board.ImageCount);
                using (Bitmap exported = board.ExportImage())
                {
                    RectangleF bounds = BoardImageBounds(board, 0);
                    Equal(image.GetPixel(75, 55).ToArgb(), PixelAtCenter(board, exported, bounds).ToArgb());
                }
            }
        });
        Run("new capture updates embedded left source without automatically inserting on board", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap first = Solid(200, 150, Color.Red))
            using (Bitmap next = Solid(140, 100, Color.Blue))
            {
                InvokePrivate(main, "SetCurrentImage", first);
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                workspace.AddImage(first);
                InvokePrivate(main, "AcceptCapture", next);
                Assert((bool)PrivateField(main, "boardMode"), "Capturing unexpectedly left the workspace.");
                PixelsEqual(next, (Bitmap)PrivateField(workspace, "sourceImage"), Point.Empty);
                AnnotationCanvas sourceCanvas = (AnnotationCanvas)PrivateField(main, "canvas");
                using (Bitmap source = sourceCanvas.ExportImage()) PixelsEqual(next, source, Point.Empty);
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                Equal(1, board.ImageCount);
                using (Bitmap result = board.ExportImage())
                    Equal(Color.Red.ToArgb(), PixelAtCenter(board, result, BoardImageBounds(board, 0)).ToArgb());
            }
        });
        Run("adding current source reports retained snapshot and undo invalidates that report", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                int additions = 0;
                workspace.SourceAddedToBoard += delegate { additions++; };
                workspace.SetSourceImage(image);
                Assert(!workspace.SourceIsRetained, "Fresh source was reported as already retained.");
                InvokePrivate(workspace, "AddSourceImage");
                Equal(1, additions);
                Assert(workspace.SourceIsRetained, "Successful source addition was not reported.");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                Equal(1, board.ImageCount);
                board.Undo();
                Assert(!workspace.SourceIsRetained, "Undo still claimed source pixels were retained.");
                Equal(0, board.ImageCount);
                InvokePrivate(workspace, "AddSourceImage");
                Equal(2, additions);
                workspace.SetSourceImage(image);
                Assert(!workspace.SourceIsRetained, "Replacing the source retained stale ownership state.");
                Equal(1, board.ImageCount);
            }
        });
        Run("workspace modal capture and drag busy guards suppress capture edits and shortcuts", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                int requests = 0;
                workspace.ScreenshotRequested += delegate { requests++; };
                workspace.SetSourceImage(image);
                workspace.AddImage(image);
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                for (int state = 0; state < 3; state++)
                {
                    if (state == 0) InvokePrivate(workspace, "SetDialogBusy", true);
                    if (state == 1) workspace.CaptureInProgress = true;
                    if (state == 2)
                    {
                        SetPrivateField(workspace, "sourceDragging", true);
                        InvokePrivate(workspace, "UpdateInteraction");
                    }
                    Assert(workspace.IsBusy, "Busy state was not exposed to the parent.");
                    Assert(!workspace.ConfirmDiscard(), "Busy workspace permitted a discard flow.");
                    InvokePrivate(workspace, "RequestCapture", false);
                    InvokePrivate(workspace, "AddSourceImage");
                    Assert(!workspace.HandleShortcut(Keys.Control | Keys.Shift | Keys.A),
                        "Workspace retained a fixed shortcut instead of the host's configurable shortcut.");
                    Assert(workspace.HandleShortcut(Keys.Control | Keys.Z), "Busy undo shortcut leaked.");
                    Equal(0, requests);
                    Equal(1, board.ImageCount);
                    if (state < 2) Assert(!board.ImageDropEnabled, "Modal/capture state accepted drops.");
                    else Assert(board.ImageDropEnabled, "Internal drag disabled its own valid drop target.");
                    if (state == 0) InvokePrivate(workspace, "SetDialogBusy", false);
                    if (state == 1) workspace.CaptureInProgress = false;
                    if (state == 2)
                    {
                        SetPrivateField(workspace, "sourceDragging", false);
                        InvokePrivate(workspace, "UpdateInteraction");
                    }
                }
                Assert(!workspace.IsBusy, "Workspace failed to leave its busy state.");
                InvokePrivate(workspace, "RequestCapture", false);
                Equal(1, requests);
            }
        });
        Run("main cannot switch workspace or add images while an internal drag is busy", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                SetPrivateField(workspace, "sourceDragging", true);
                InvokePrivate(main, "ShowScreenshotEditor");
                Assert((bool)PrivateField(main, "boardMode"), "Main switched away during a drag.");
                InvokePrivate(main, "OpenBoard", true);
                Equal(0, board.ImageCount);
                SetPrivateField(workspace, "sourceDragging", false);
                InvokePrivate(main, "ShowScreenshotEditor");
                Assert(!(bool)PrivateField(main, "boardMode"), "Main did not recover after drag.");
            }
        });
        Run("retained source survives board edits and follows remaining image copies and undo", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                workspace.SetSourceImage(image);
                InvokePrivate(workspace, "AddSourceImage");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                IntPtr handle = board.Handle;
                long firstId = board.LastAddedImageId;
                Assert(firstId > 0 && board.ContainsImageId(firstId), "Added image did not receive a stable identity.");
                PointF start = Center(BoardImageBounds(board, 0));
                BoardDrag(board, start, new PointF(start.X + 120, start.Y + 100));
                Assert(workspace.SourceIsRetained, "Moving the retained screenshot lost its identity.");
                board.Tool = DrawingTool.Rectangle;
                BoardDrag(board, new PointF(-200, 200), new PointF(200, 300));
                Assert(workspace.SourceIsRetained, "Annotating the board lost the screenshot identity.");
                board.DeleteSelected();
                Assert(!workspace.SourceIsRetained && !board.ContainsImageId(firstId),
                    "Deleting the last copy still reported source pixels retained.");
                board.Undo();
                Assert(workspace.SourceIsRetained && board.ContainsImageId(firstId),
                    "Undo deletion did not restore the original retained identity.");
                InvokePrivate(workspace, "AddSourceImage");
                long secondId = board.LastAddedImageId;
                Assert(secondId > firstId, "New image reused an existing identity.");
                board.DeleteSelected();
                Assert(workspace.SourceIsRetained && board.ContainsImageId(firstId) &&
                    !board.ContainsImageId(secondId), "Removing one copy discarded another retained copy.");
                board.ClearBoard();
                Assert(!workspace.SourceIsRetained && !board.ContainsImageId(firstId),
                    "Clear retained stale image identities.");
                InvokePrivate(workspace, "AddSourceImage");
                Assert(board.LastAddedImageId > secondId, "Clear caused image identity reuse.");
                Assert(workspace.SourceIsRetained, "Fresh source addition after clear was not tracked.");
                board.Undo();
                Assert(!workspace.SourceIsRetained, "Undo addition retained the removed source copy.");
            }
        });
        Run("closing during capture delay cancels pending work and queues close after capture", delegate {
            using (MainForm main = NewHiddenMain())
            using (CancellationTokenSource pending = new CancellationTokenSource())
            {
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                workspace.CaptureInProgress = true;
                SetPrivateField(main, "capturing", true);
                SetPrivateField(main, "pendingCapture", pending);
                FormClosingEventArgs closing = new FormClosingEventArgs(CloseReason.UserClosing, false);
                InvokePrivate(main, "OnFormClosing", closing);
                Assert(closing.Cancel, "Capture was disposed before async cleanup could finish.");
                Assert(pending.IsCancellationRequested, "Closing did not cancel the pending capture delay.");
                Assert((bool)PrivateField(main, "closeAfterCapture"), "Close was discarded instead of deferred.");
                Assert(!main.IsDisposed, "Synthetic close unexpectedly disposed the active capture host.");
                SetPrivateField(main, "pendingCapture", null);
                SetPrivateField(main, "capturing", false);
                workspace.CaptureInProgress = false;
            }
        });
    }

    private static MainForm NewHiddenMain()
    {
        MainForm main = new MainForm();
        main.ShowInTaskbar = false;
        main.Opacity = 0;
        ((NotifyIcon)PrivateField(main, "tray")).Visible = false;
        // Only managed layout is needed for these tests. The visible-window count
        // is checked separately by the opacity-zero layout fixture.
        main.PerformLayout();
        return main;
    }

    private static object PrivateField(object obj, string name)
    {
        return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(obj);
    }

    private static object InvokePrivate(object obj, string method, params object[] args)
    {
        return obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
    private static void SetPrivateField(object obj, string name, object value)
    {
        obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
    }
}
