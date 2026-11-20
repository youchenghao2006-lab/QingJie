using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunBoardViewTests()
    {
        Run("board zoom keeps the board point under the mouse fixed", delegate {
            using (BoardCanvas board = NewBoard())
            {
                Point anchor = new Point(353, 281);
                PointF before = board.ClientToBoard(anchor, false);
                board.ZoomAt(anchor, 175f);
                Near(before, board.ClientToBoard(anchor, false), .005f, "zoom anchor inverse");
                Near(new PointF(anchor.X, anchor.Y), board.BoardToClient(before), .005f, "zoom anchor forward");
                Assert(Math.Abs(board.ZoomPercent - 175f) < .01f, "Zoom percentage did not use board pixels.");
                board.ZoomAt(anchor, 35f);
                Near(before, board.ClientToBoard(anchor, false), .005f, "zoom out anchor");
            }
        });
        Run("board pan uses client pixels and keeps coordinate transforms inverse", delegate {
            using (BoardCanvas board = NewBoard())
            {
                board.ZoomAt(new Point(500, 350), 125f);
                PointF original = new PointF(1150, 830);
                PointF before = board.BoardToClient(original);
                board.PanBy(137, -92);
                PointF after = board.BoardToClient(original);
                Near(new PointF(before.X + 137, before.Y - 92), after, .005f, "client-pixel pan");
                Point sample = Point.Round(after);
                PointF back = board.ClientToBoard(sample, false);
                Assert(Math.Abs(back.X - original.X) <= .5f &&
                    Math.Abs(back.Y - original.Y) <= .5f, "Pan inverse lost the board point.");
            }
        });
        Run("board fit restores the centered view and manual zoom obeys limits", delegate {
            using (BoardCanvas board = NewBoard())
            {
                RectangleF fit = board.ViewportBoardBounds;
                float fitZoom = board.ZoomPercent;
                board.ZoomAt(new Point(200, 200), 1f);
                Assert(Math.Abs(board.ZoomPercent - 10f) < .01f, "Zoom minimum was not enforced.");
                board.ZoomAt(new Point(700, 500), 1000f);
                Assert(Math.Abs(board.ZoomPercent - 400f) < .01f, "Zoom maximum was not enforced.");
                board.PanBy(2500, -1700);
                board.FitToWindow();
                Near(new PointF(fit.X, fit.Y), new PointF(board.ViewportBoardBounds.X,
                    board.ViewportBoardBounds.Y), .01f, "fit origin");
                Assert(Math.Abs(fitZoom - board.ZoomPercent) < .01f, "Fit failed to restore its scale.");
                board.ZoomIn();
                Assert(board.ZoomPercent > fitZoom, "ZoomIn did not enlarge the view.");
                board.ResetView();
                Assert(Math.Abs(fitZoom - board.ZoomPercent) < .01f, "ResetView did not fit.");
                using (Bitmap large = Solid(1000, 1000, Color.Red))
                    board.AddImageAt(large, PointF.Empty);
                board.Size = new Size(100, 100);
                board.FitToWindow();
                float smallFit = board.ZoomPercent;
                Assert(smallFit < 10f, "Small fit fixture did not reach the special below-minimum range.");
                board.ZoomOut();
                Equal(smallFit, board.ZoomPercent);
                SendBoardWheel(board, new Point(50, 50), -120);
                Equal(smallFit, board.ZoomPercent);
            }
        });
        Run("board resize follows fit mode but preserves a manually positioned camera", delegate {
            using (BoardCanvas board = NewBoard())
            {
                board.Size = new Size(1200, 800);
                RectangleF fitted = board.ViewportBoardBounds;
                Assert(Math.Abs((fitted.Left + fitted.Right) / 2 - 600) < .01f &&
                    Math.Abs((fitted.Top + fitted.Bottom) / 2 - 400) < .01f, "Fit resize did not stay centered.");
                board.ZoomAt(new Point(400, 300), 80f);
                board.PanBy(90, -35);
                PointF before = board.BoardToClient(new PointF(1000, 700));
                board.Size = new Size(1400, 900);
                Near(before, board.BoardToClient(new PointF(1000, 700)), .01f, "manual resize camera");
                Assert(Math.Abs(board.ZoomPercent - 80f) < .01f, "Manual resize changed zoom.");
            }
        });
        Run("zoom pan and fit leave original-resolution export and undo history unchanged", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                board.Tool = DrawingTool.Arrow;
                board.StrokeColor = Color.Lime;
                board.StrokeWidth = 14;
                BoardDrag(board, Center(BoardImageBounds(board, 0)), Center(BoardImageBounds(board, 1)));
                int history = ((IList)typeof(BoardCanvas).GetField("_undo",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board)).Count;
                using (Bitmap before = board.ExportImage())
                {
                    board.ZoomAt(new Point(550, 400), 200f);
                    board.PanBy(170, -280);
                    board.ZoomOut();
                    board.FitToWindow();
                    using (Bitmap after = board.ExportImage()) FastPixelsEqual(before, after, "view-only export");
                }
                Equal(history, ((IList)typeof(BoardCanvas).GetField("_undo",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board)).Count);
                board.Undo();
                Equal(2, board.ImageCount);
                using (Bitmap noArrow = board.ExportImage())
                {
                    PointF middle = new PointF((Center(BoardImageBounds(board, 0)).X +
                        Center(BoardImageBounds(board, 1)).X) / 2, Center(BoardImageBounds(board, 0)).Y);
                    Equal(Color.White.ToArgb(), PixelAtWorld(board, noArrow, middle).ToArgb());
                }
            }
        });
        Run("bitmap drops use the current zoomed and panned board coordinates", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.ZoomAt(new Point(500, 350), 90f);
                board.PanBy(70, -45);
                Point target = new Point(620, 420);
                PointF expected = board.ClientToBoard(target, false);
                DataObject data = new DataObject(DataFormats.Bitmap, image);
                Equal(DragDropEffects.Copy, SendBoardDragClient(board, "OnDragDrop", data,
                    target, DragDropEffects.Copy).Effect);
                Equal(1, board.ImageCount);
                Near(expected, Center(BoardImageBounds(board, 0)), .01f, "zoomed drop center");
            }
        });
        Run("moving an image under zoom uses board distance and undo remains exact", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.AddImageAt(image, PointF.Empty);
                board.ZoomAt(new Point(500, 360), 80f);
                board.PanBy(40, 20);
                RectangleF original = BoardImageBounds(board, 0);
                using (Bitmap before = board.ExportImage())
                {
                    BoardDrag(board, Center(original), new PointF(120, 100));
                    PointF moved = Center(BoardImageBounds(board, 0));
                    Assert(Math.Abs(moved.X - 120) < 2 && Math.Abs(moved.Y - 100) < 2,
                        "Zoomed image movement used viewport coordinates as board pixels.");
                    board.Undo();
                    using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, "zoomed move undo");
                }
            }
        });
        Run("middle-button panning changes view without moving selected image", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.AddImageAt(image, PointF.Empty);
                RectangleF original = BoardImageBounds(board, 0);
                PointF before = board.BoardToClient(PointF.Empty);
                SendBoardMouseClient(board, "OnMouseDown", MouseButtons.Middle, new Point(500, 350));
                SendBoardMouseClient(board, "OnMouseMove", MouseButtons.Middle, new Point(620, 430));
                SendBoardMouseClient(board, "OnMouseUp", MouseButtons.Middle, new Point(620, 430));
                Near(new PointF(before.X + 120, before.Y + 80),
                    board.BoardToClient(PointF.Empty), .01f, "middle-button pan");
                Equal(original, BoardImageBounds(board, 0));
                board.Undo();
                Equal(0, board.ImageCount);
            }
            using (BoardWorkspace workspace = new BoardWorkspace())
            {
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                Control tools = (Control)PrivateField(workspace, "tools");
                Button pen = null;
                foreach (Control control in tools.Controls)
                    if (control.Tag is DrawingTool && (DrawingTool)control.Tag == DrawingTool.Pen)
                        pen = (Button)control;
                Assert(pen != null, "Pen tool button is missing.");
                pen.PerformClick();
                Equal(DrawingTool.Pen, board.Tool);
                board.PanMode = true;
                pen.PerformClick();
                Assert(!board.PanMode && board.Tool == DrawingTool.Pen,
                    "Returning from pan to the same drawing tool toggled it off.");
            }
        });
        Run("wheel zoom preserves its anchor and ignores wheel events outside the canvas", delegate {
            using (BoardCanvas board = NewBoard())
            {
                Point anchor = new Point(400, 280);
                PointF original = board.ClientToBoard(anchor, false);
                float before = board.ZoomPercent;
                SendBoardWheel(board, anchor, 120);
                Assert(board.ZoomPercent > before, "In-canvas wheel did not zoom.");
                Near(original, board.ClientToBoard(anchor, false), .005f, "wheel zoom anchor");
                float zoom = board.ZoomPercent;
                PointF origin = board.BoardToClient(PointF.Empty);
                SendBoardWheel(board, new Point(-30, 100), 120);
                SendBoardWheel(board, new Point(board.Width + 20, 100), -120);
                Equal(zoom, board.ZoomPercent);
                Near(origin, board.BoardToClient(PointF.Empty), .005f, "out-of-canvas wheel");
            }
        });
        Run("integer pan cache matches complete repaint for transparent layers arrows and empty state", delegate {
            for (int content = 0; content < 2; content++)
            using (BoardCanvas board = NewBoard())
            {
                if (content == 1)
                {
                    using (Bitmap first = Pattern(640, 480))
                    using (Bitmap second = Solid(400, 300, Color.FromArgb(135, 20, 80, 230)))
                    {
                        board.AddImageAt(first, new PointF(-100, 0));
                        board.AddImageAt(second, new PointF(0, 50));
                    }
                    board.Tool = DrawingTool.Arrow;
                    board.StrokeColor = Color.Yellow;
                    board.StrokeWidth = 13;
                    BoardDrag(board, new PointF(-400, -150), new PointF(150, 150));
                }
                board.ZoomAt(new Point(500, 360), 85);
                FinishBoardView(board);
                using (Bitmap initial = RenderBoard(board)) { }
                Point[] deltas = { new Point(350, 200), new Point(-210, -150), new Point(93, -70),
                    new Point(-170, 130), new Point(1100, 900), new Point(-1100, -900) };
                foreach (Point delta in deltas)
                {
                    board.PanBy(delta.X, delta.Y);
                    using (Bitmap shifted = RenderBoard(board)) { }
                    CompareBoardCacheWithRebuild(board, "pan " + delta + " content=" + content);
                }
                if (content == 0)
                {
                    SendBoardMouseClient(board, "OnMouseDown", MouseButtons.Left, new Point(80, 80));
                    for (int step = 1; step <= 12; step++)
                    {
                        SendBoardMouseClient(board, "OnMouseMove", MouseButtons.Left,
                            new Point(80 + step * 17, 80 + step * 9));
                        using (Bitmap dragged = RenderBoard(board)) { }
                        CompareBoardCacheWithRebuild(board, "empty left-drag step " + step);
                    }
                    SendBoardMouseClient(board, "OnMouseUp", MouseButtons.Left, new Point(284, 188));
                    Assert(!board.HasContent && !board.CanUndo, "Empty-space pan created content or undo history.");
                }
                FinishBoardView(board);
                using (Bitmap settled = RenderBoard(board)) { }
                CompareBoardCacheWithRebuild(board, "settled high-quality pan content=" + content);
            }
        });
    }

    private static void Near(PointF expected, PointF actual, float tolerance, string message)
    {
        Assert(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance,
            message + ": expected " + expected + ", actual " + actual);
    }
    private static void SendBoardMouseClient(BoardCanvas board, string method, MouseButtons button, Point point)
    {
        typeof(BoardCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(board, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
    }
    private static void SendBoardWheel(BoardCanvas board, Point point, int delta)
    {
        typeof(BoardCanvas).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(board, new object[] { new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, delta) });
    }
    private static void FinishBoardView(BoardCanvas board)
    {
        typeof(BoardCanvas).GetMethod("FinishViewInteraction", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(board, new object[] { null, EventArgs.Empty });
    }
    private static void CompareBoardCacheWithRebuild(BoardCanvas board, string description)
    {
        BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Bitmap cached = (Bitmap)typeof(BoardCanvas).GetField("_viewportCache", fields).GetValue(board);
        FieldInfo interacting = typeof(BoardCanvas).GetField("_viewInteracting", fields);
        bool before = (bool)interacting.GetValue(board);
        bool qualityOfCache = (bool)typeof(BoardCanvas).GetField("_viewportInteractive", fields).GetValue(board);
        interacting.SetValue(board, qualityOfCache);
        try
        {
            using (Bitmap rebuilt = (Bitmap)typeof(BoardCanvas).GetMethod("BuildViewport", fields)
                .Invoke(board, new object[] { null, true }))
                FastPixelsEqual(rebuilt, cached, description);
        }
        finally { interacting.SetValue(board, before); }
    }
}
