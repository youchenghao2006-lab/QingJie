using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunScreenshotQueueTests()
    {
        Run("screenshot queue upserts stable IDs without duplicating or reordering entries", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(160, 120, Color.Blue))
            using (Bitmap green = Solid(180, 130, Color.Green))
            {
                workspace.SetSourceImage(red, 101, true);
                workspace.SetSourceImage(blue, 202, true);
                Equal(2, workspace.SourceCount);
                Equal(202L, workspace.SelectedSourceId);
                ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                workspace.SetSourceImage(green, 101, false);
                Equal(2, workspace.SourceCount);
                Equal(101L, queue.Entries[0].Id);
                Equal(202L, queue.Entries[1].Id);
                Equal(101L, workspace.SelectedSourceId);
                Assert(!workspace.IsSourceSaved(101) && workspace.IsSourceSaved(202), "Upsert corrupted per-image save state.");
                PixelsEqual(green, queue.Find(101).Image, Point.Empty);
                green.SetPixel(90, 65, Color.Black);
                Equal(Color.Green.ToArgb(), queue.Find(101).Image.GetPixel(90, 65).ToArgb());
            }
        });
        Run("queue selection and save state changes reuse full images and cached thumbnails", delegate {
            using (ScreenshotQueueControl queue = new ScreenshotQueueControl())
            using (Bitmap red = Solid(800, 600, Color.Red))
            using (Bitmap blue = Solid(800, 600, Color.Blue))
            {
                queue.Size = new Size(240, 500);
                queue.Upsert(red, 11, false);
                queue.Upsert(blue, 22, false);
                using (Bitmap paint = RenderControl(queue)) { }
                Bitmap firstImage = queue.Find(11).Image, firstThumbnail = queue.Find(11).Thumbnail;
                Bitmap secondImage = queue.Find(22).Image, secondThumbnail = queue.Find(22).Thumbnail;
                Assert(firstThumbnail != null && secondThumbnail != null, "Visible queue rows did not create thumbnails.");
                for (int i = 0; i < 4; i++)
                {
                    queue.SelectId(11);
                    queue.Find(11).Saved = true;
                    using (Bitmap paint = RenderControl(queue)) { }
                    queue.SelectId(22);
                    using (Bitmap paint = RenderControl(queue)) { }
                }
                Assert(Object.ReferenceEquals(firstImage, queue.Find(11).Image) &&
                    Object.ReferenceEquals(secondImage, queue.Find(22).Image), "Pure selection copied full-resolution images.");
                Assert(Object.ReferenceEquals(firstThumbnail, queue.Find(11).Thumbnail) &&
                    Object.ReferenceEquals(secondThumbnail, queue.Find(22).Thumbnail), "Pure selection rebuilt thumbnails.");
                queue.Upsert(blue, 11, false);
                using (Bitmap paint = RenderControl(queue)) { }
                Assert(!Object.ReferenceEquals(firstThumbnail, queue.Find(11).Thumbnail),
                    "Edited image reused a stale thumbnail.");
                Assert(Object.ReferenceEquals(secondThumbnail, queue.Find(22).Thumbnail),
                    "Updating one entry rebuilt another thumbnail.");
            }
        });
        Run("deleting a saved queue entry preserves board pixels and selects a remaining screenshot", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(200, 150, Color.Blue))
            {
                workspace.SetSourceImage(red, 1, true);
                InvokePrivate(workspace, "AddSourceImage");
                workspace.SetSourceImage(blue, 2, true);
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                using (Bitmap before = board.ExportImage())
                {
                    long removed = 0;
                    workspace.SourceRemoved += delegate(long id) { removed = id; };
                    Assert(workspace.RemoveSource(1), "Saved queue image could not be removed.");
                    Equal(1L, removed);
                    Equal(1, workspace.SourceCount);
                    Equal(2L, workspace.SelectedSourceId);
                    Equal(1, board.ImageCount);
                    using (Bitmap after = board.ExportImage()) FastPixelsEqual(before, after, "queue removal board pixels");
                }
                Assert(workspace.RemoveSource(2), "Last saved queue image could not be removed.");
                Equal(0, workspace.SourceCount);
                Equal(0L, workspace.SelectedSourceId);
                Assert(PrivateField(workspace, "sourceImage") == null, "Empty queue retained a selected image.");
                Equal(1, board.ImageCount);
            }
        });
        Run("queue edit action emits the selected stable ID and its owned snapshot", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(200, 150, Color.Blue))
            {
                workspace.SetSourceImage(red, 1, true);
                workspace.SetSourceImage(blue, 2, true);
                workspace.SelectSource(1);
                long edited = 0;
                Bitmap selected = null;
                workspace.EditQueueImageRequested += delegate(long id, Bitmap image) { edited = id; selected = image; };
                InvokePrivate(workspace, "EditSelectedSource");
                Equal(1L, edited);
                Assert(Object.ReferenceEquals(selected, PrivateField(workspace, "sourceImage")),
                    "Edit callback did not reference the selected owned snapshot.");
                PixelsEqual(red, selected, Point.Empty);
            }
        });
        Run("new screenshots enter queue immediately and replacement needs no board insertion", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(200, 150, Color.Blue))
            {
                InvokePrivate(main, "SetCurrentImage", red);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                long firstId = (long)PrivateField(main, "currentSourceId");
                Equal(1, workspace.SourceCount);
                Assert(workspace.ContainsSourceId(firstId), "New screenshot was not queued immediately.");
                Assert((bool)InvokePrivate(main, "CanReplaceImage"), "Queued screenshot could not be replaced.");
                InvokePrivate(main, "SetCurrentImage", blue);
                long secondId = (long)PrivateField(main, "currentSourceId");
                Assert(secondId != firstId, "Different captures reused the same stable ID.");
                Equal(2, workspace.SourceCount);
                Equal(0, ((BoardCanvas)PrivateField(workspace, "canvas")).ImageCount);
                Assert(workspace.ContainsSourceId(firstId) && workspace.ContainsSourceId(secondId),
                    "Replacing screenshot lost an earlier queue item.");
            }
        });
        Run("editing an older queued screenshot keeps its ID and updates that queue entry", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(200, 150, Color.Blue))
            {
                InvokePrivate(main, "SetCurrentImage", red);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                long firstId = (long)PrivateField(main, "currentSourceId");
                InvokePrivate(main, "SetCurrentImage", blue);
                long secondId = (long)PrivateField(main, "currentSourceId");
                InvokePrivate(main, "OpenBoard", false);
                workspace.SelectSource(firstId);
                InvokePrivate(workspace, "EditSelectedSource");
                Equal(firstId, (long)PrivateField(main, "currentSourceId"));
                Assert(!(bool)PrivateField(main, "boardMode"), "Editing queue image did not return to editor.");
                AnnotationCanvas canvas = (AnnotationCanvas)PrivateField(main, "canvas");
                IntPtr handle = canvas.Handle;
                canvas.Tool = DrawingTool.Rectangle;
                canvas.StrokeColor = Color.Lime;
                canvas.StrokeWidth = 5;
                Drag(canvas, new PointF(20, 20), new PointF(160, 100));
                using (Bitmap expected = canvas.ExportImage())
                {
                    InvokePrivate(main, "OpenBoard", false);
                    ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                    PixelsEqual(expected, queue.Find(firstId).Image, Point.Empty);
                    PixelsEqual(blue, queue.Find(secondId).Image, Point.Empty);
                }
                Equal(2, workspace.SourceCount);
            }
        });
        Run("warm mode switches preserve screenshot board and thumbnail cache instances", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Pattern(500, 300))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                InvokePrivate(main, "OpenBoard", false);
                main.PerformLayout();
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                AnnotationCanvas editor = (AnnotationCanvas)PrivateField(main, "canvas");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                IntPtr editorHandle = editor.Handle;
                IntPtr boardHandle = board.Handle;
                using (Bitmap view = RenderControl(queue)) { }
                using (Bitmap view = RenderControl(editor)) { }
                using (Bitmap view = RenderControl(board)) { }
                object source = PrivateField(workspace, "sourceImage");
                object thumbnail = queue.SelectedEntry.Thumbnail;
                object editorCache = PrivateField(editor, "_viewportCache");
                object boardCache = PrivateField(board, "_viewportCache");
                long revision = (long)PrivateField(main, "boardSourceRevision");
                for (int i = 0; i < 5; i++)
                {
                    InvokePrivate(main, "ShowScreenshotEditor");
                    InvokePrivate(main, "OpenBoard", false);
                    main.PerformLayout();
                    using (Bitmap view = RenderControl(queue)) { }
                    using (Bitmap view = RenderControl(editor)) { }
                    using (Bitmap view = RenderControl(board)) { }
                }
                Assert(Object.ReferenceEquals(source, PrivateField(workspace, "sourceImage")),
                    "Warm mode switch cloned its queue snapshot.");
                Assert(Object.ReferenceEquals(thumbnail, queue.SelectedEntry.Thumbnail),
                    "Warm mode switch rebuilt its thumbnail.");
                Assert(Object.ReferenceEquals(editorCache, PrivateField(editor, "_viewportCache")) &&
                    Object.ReferenceEquals(boardCache, PrivateField(board, "_viewportCache")),
                    "Warm mode switch rebuilt a canvas viewport cache.");
                Equal(revision, (long)PrivateField(main, "boardSourceRevision"));
                Equal(1, workspace.SourceCount);
            }
        });
        Run("editing a saved screenshot marks its revision unsaved and preserves it when switching queue items", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap blue = Solid(200, 150, Color.Blue))
            {
                InvokePrivate(main, "SetCurrentImage", red);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                long first = (long)PrivateField(main, "currentSourceId");
                workspace.SetSourceSaved(first, true);
                SetPrivateField(main, "dirty", false);
                InvokePrivate(main, "SetCurrentImage", blue);
                long second = (long)PrivateField(main, "currentSourceId");
                InvokePrivate(main, "OpenBoard", false);
                workspace.SelectSource(first);
                InvokePrivate(workspace, "EditSelectedSource");
                Assert(!(bool)PrivateField(main, "dirty"), "Reopening a saved queue item marked it dirty.");
                AnnotationCanvas editor = (AnnotationCanvas)PrivateField(main, "canvas");
                IntPtr handle = editor.Handle;
                editor.Tool = DrawingTool.Rectangle;
                editor.StrokeColor = Color.Lime;
                editor.StrokeWidth = 5;
                Drag(editor, new PointF(25, 25), new PointF(170, 110));
                Assert((bool)PrivateField(main, "dirty") && !workspace.IsSourceSaved(first),
                    "Changing a saved screenshot did not mark the new revision unsaved.");
                using (Bitmap changed = editor.ExportImage())
                {
                    // Changing the queue selection before editing another item must
                    // first flush the former editor revision to its own stable ID.
                    workspace.SelectSource(second);
                    InvokePrivate(workspace, "EditSelectedSource");
                    Equal(second, (long)PrivateField(main, "currentSourceId"));
                    Equal(second, workspace.SelectedSourceId);
                    ScreenshotQueueControl queue = (ScreenshotQueueControl)PrivateField(workspace, "sourcePreview");
                    PixelsEqual(changed, queue.Find(first).Image, Point.Empty);
                    PixelsEqual(blue, queue.Find(second).Image, Point.Empty);
                    workspace.SelectSource(first);
                    InvokePrivate(workspace, "EditSelectedSource");
                    using (Bitmap reopened = editor.ExportImage()) PixelsEqual(changed, reopened, Point.Empty);
                    Assert((bool)PrivateField(main, "dirty"), "Edited unsaved revision reopened as saved.");
                }
            }
        });
        Run("deleting current screenshot clears editor and never resurrects its queue entry", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                long removed = (long)PrivateField(main, "currentSourceId");
                workspace.SetSourceSaved(removed, true);
                SetPrivateField(main, "dirty", false);
                workspace.AddImage(image);
                InvokePrivate(main, "DeleteCurrentImage");
                AnnotationCanvas editor = (AnnotationCanvas)PrivateField(main, "canvas");
                Assert(!editor.HasImage && !editor.CanUndo, "Deleting current screenshot retained editor pixels.");
                Equal(0L, (long)PrivateField(main, "currentSourceId"));
                Equal(0, workspace.SourceCount);
                InvokePrivate(main, "OpenBoard", false);
                InvokePrivate(main, "ShowScreenshotEditor");
                InvokePrivate(main, "OpenBoard", false);
                Assert(!workspace.ContainsSourceId(removed), "Mode switch recreated a deleted queue item.");
                Equal(0, workspace.SourceCount);
                Equal(1, ((BoardCanvas)PrivateField(workspace, "canvas")).ImageCount);
                InvokePrivate(main, "SetCurrentImage", image);
                Assert((long)PrivateField(main, "currentSourceId") > removed, "New screenshot reused deleted ID.");
                Equal(1, workspace.SourceCount);
            }
        });
        Run("queue tracks unsaved exit state even when board itself has no edits", delegate {
            using (BoardWorkspace workspace = new BoardWorkspace())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                workspace.SetSourceImage(image, 1, false);
                workspace.SetSourceImage(image, 2, true);
                Assert(!(bool)PrivateField(workspace, "dirty"), "Queue-only additions changed board edit state.");
                Assert(!workspace.IsSourceRetained(1) && workspace.IsSourceRetained(2),
                    "Queue did not distinguish unretained screenshots from saved ones.");
                // The unsaved branch is statically reviewed to display its confirmation;
                // this test deliberately exercises only the nonmodal, fully saved branch.
                workspace.SetSourceSaved(1, true);
                Assert(workspace.ConfirmDiscard(), "Fully saved queue with unchanged board blocked clean exit.");
                workspace.SetSourceSaved(1, false);
                Assert(!workspace.IsSourceRetained(1), "Revised unsaved source retained a stale saved state.");
            }
        });
    }

    private static Bitmap RenderControl(Control control)
    {
        Bitmap result = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height), PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using (Graphics graphics = Graphics.FromImage(result))
            control.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(control, new object[] { new PaintEventArgs(graphics, control.ClientRectangle) });
        return result;
    }
}
