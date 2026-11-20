using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunBoardAsyncTests()
    {
        Run("background view snapshot preserves alpha and negative-world annotation positions", delegate {
            using (Bitmap source = Solid(40, 30, Color.FromArgb(128, 0, 0, 255)))
            using (BoardViewSnapshot snapshot = new BoardViewSnapshot())
            {
                snapshot.ViewSize = new Size(240, 180);
                snapshot.DpiScale = 1;
                snapshot.Scale = 1;
                snapshot.Origin = new PointF(100, 80);
                snapshot.Background = Color.White;
                snapshot.Images.Add(new BoardSnapshotImage {
                    Pixels = source, Bounds = new RectangleF(-20, -10, 40, 30), IsOpaque = false
                });
                int visible = 0, outside = 0;
                snapshot.Annotations.Add(new BoardSnapshotAnnotation {
                    Bounds = new RectangleF(-90, -70, 15, 12),
                    Draw = delegate(Graphics graphics) { visible++; graphics.FillRectangle(Brushes.Lime, -90, -70, 15, 12); }
                });
                snapshot.Annotations.Add(new BoardSnapshotAnnotation {
                    Bounds = new RectangleF(9000, 9000, 10, 10),
                    Draw = delegate(Graphics graphics) { outside++; }
                });
                using (Bitmap rendered = snapshot.Render(CancellationToken.None))
                using (Bitmap reference = new Bitmap(240, 180, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(reference))
                    {
                        graphics.Clear(Color.White);
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.DrawImage(source, new Rectangle(80, 70, 40, 30), 0, 0, 40, 30, GraphicsUnit.Pixel);
                    }
                    Equal(reference.GetPixel(105, 85).ToArgb(), rendered.GetPixel(105, 85).ToArgb());
                    Equal(Color.Lime.ToArgb(), rendered.GetPixel(15, 15).ToArgb());
                    Equal(Color.White.ToArgb(), rendered.GetPixel(225, 165).ToArgb());
                    Equal(1, visible);
                    Equal(0, outside);
                }
            }
        });
        Run("cancelled background snapshots reject rendering and release retained images exactly once", delegate {
            int released = 0;
            using (Bitmap image = Solid(8, 8, Color.Red))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                BoardViewSnapshot snapshot = NewViewSnapshot();
                snapshot.Images.Add(new BoardSnapshotImage {
                    Pixels = image, Bounds = new RectangleF(0, 0, 8, 8),
                    Release = delegate { released++; }
                });
                cancellation.Cancel();
                Throws<OperationCanceledException>(delegate { snapshot.Render(cancellation.Token); });
                snapshot.Dispose();
                snapshot.Dispose();
                Equal(1, released);
                Equal(Color.Red.ToArgb(), image.GetPixel(3, 3).ToArgb());
                Throws<ObjectDisposedException>(delegate { snapshot.Render(CancellationToken.None); });
            }
        });
        Run("cancelled background rendering can leave a locked image without waiting for the UI owner", delegate {
            using (Bitmap image = Solid(8, 8, Color.Red))
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (BoardViewSnapshot snapshot = NewViewSnapshot())
            using (ManualResetEvent started = new ManualResetEvent(false))
            {
                object gate = new object();
                snapshot.Images.Add(new BoardSnapshotImage {
                    Pixels = image, Bounds = new RectangleF(0, 0, 8, 8), SyncRoot = gate
                });
                Task<bool> worker = null;
                Monitor.Enter(gate);
                try
                {
                    worker = Task.Factory.StartNew(delegate {
                        started.Set();
                        try { using (Bitmap result = snapshot.Render(cancellation.Token)) { } return false; }
                        catch (OperationCanceledException) { return true; }
                    });
                    Assert(started.WaitOne(3000), "Worker did not start.");
                    Thread.Sleep(30);
                    cancellation.Cancel();
                    Assert(worker.Wait(3000), "Cancelled worker remained blocked on an image owned by the UI.");
                    Assert(worker.Result, "Cancelled worker completed a stale render.");
                }
                finally
                {
                    Monitor.Exit(gate);
                    if (worker != null && !worker.IsCompleted) worker.Wait(3000);
                }
            }
        });
        Run("coalesced pointer bursts preserve final camera and Escape restores the starting view", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap source = Pattern(600, 400))
            {
                board.AddImageAt(source, PointF.Empty);
                IntPtr handle = board.Handle;
                using (Bitmap before = board.ExportImage())
                {
                    using (Bitmap warm = RenderBoard(board)) { }
                    PointF origin = board.BoardToClient(PointF.Empty);
                    Point start = new Point(400, 250);
                    SendBoardMouseClient(board, "OnMouseDown", MouseButtons.Middle, start);
                    for (int i = 1; i <= 500; i++)
                        SendBoardMouseClient(board, "OnMouseMove", MouseButtons.Middle, new Point(400 + i, 250 + i / 2));
                    Near(new PointF(origin.X + 500, origin.Y + 250),
                        board.BoardToClient(PointF.Empty), .01f, "coalesced final camera");
                    using (Bitmap painted = RenderBoard(board)) { }
                    CompareBoardCacheWithRebuild(board, "coalesced pointer burst");
                    bool handled = (bool)typeof(BoardCanvas).GetMethod("ProcessCmdKey",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(board,
                        new object[] { new Message(), Keys.Escape });
                    Assert(handled, "Escape did not cancel the active camera gesture.");
                    Near(origin, board.BoardToClient(PointF.Empty), .01f, "cancelled burst camera");
                    using (Bitmap after = board.ExportImage()) FastPixelsEqual(before, after, "burst content invariant");
                    Equal(1, BoardHistoryCount(board));
                    Point anchor = new Point(350, 240);
                    PointF anchoredWorld = board.ClientToBoard(anchor, false);
                    float beforeWheel = board.ZoomPercent;
                    for (int i = 0; i < 100; i++) SendBoardWheel(board, anchor, i % 2 == 0 ? 120 : -120);
                    Near(anchoredWorld, board.ClientToBoard(anchor, false), .01f, "coalesced wheel anchor");
                    Assert(Math.Abs(beforeWheel - board.ZoomPercent) < .02f, "Coalesced wheel events lost zoom deltas.");
                    using (Bitmap wheelPaint = RenderBoard(board)) { }
                    CompareBoardCacheWithRebuild(board, "coalesced wheel final view");
                }
            }
        });
        Run("late high-quality results cannot overwrite newer pan undo clear or replacement content", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap source = Pattern(1400, 900))
            using (Bitmap green = Solid(300, 200, Color.Lime))
            {
                board.Size = new Size(1200, 800);
                IntPtr handle = board.Handle;
                board.AddImageAt(source, new PointF(-240, 0));
                board.AddImageAt(source, new PointF(240, 0));
                IList images = (IList)PrivateField(board, "_images");
                object firstImage = images[0], secondImage = images[1];
                board.ZoomAt(new Point(600, 400), 160);
                using (Bitmap fast = RenderBoard(board)) { }
                FinishBoardView(board);
                Assert((bool)typeof(BoardCanvas).GetProperty("IsRefinementPending",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board, null),
                    "Race fixture did not schedule refinement.");
                board.PanBy(170, -60);
                board.ZoomAt(new Point(450, 280), 95);
                board.Undo();
                board.ClearBoard();
                board.AddImageAt(green, new PointF(-200, 80));
                board.FitToWindow();
                SettleBoardRefinement(board);
                using (Bitmap export = board.ExportImage())
                    Equal(Color.Lime.ToArgb(), PixelAtWorld(board, export, new PointF(-200, 80)).ToArgb());
                CompareRefinedBoardCache(board, "latest view after superseded refinement");
                Equal(1, board.ImageCount);
                Equal(0, (int)PrivateField(firstImage, "pixelReferences"));
                Equal(0, (int)PrivateField(secondImage, "pixelReferences"));
            }
        });
        Run("disposing a canvas with an active worker releases retained source pixels and late results", delegate {
            BoardCanvas board = NewBoard();
            object image = null, work = null;
            try
            {
                board.Size = new Size(1400, 900);
                IntPtr handle = board.Handle;
                using (Bitmap source = Pattern(1800, 1200)) board.AddImageAt(source, PointF.Empty);
                image = ((IList)PrivateField(board, "_images"))[0];
                board.ZoomAt(new Point(700, 450), 220);
                using (Bitmap fast = RenderBoard(board)) { }
                FinishBoardView(board);
                work = PrivateField(board, "_refineWork");
                Assert(work != null, "Disposal fixture did not start a worker.");
                board.Dispose();
                Stopwatch timeout = Stopwatch.StartNew();
                while (!(bool)work.GetType().GetField("Completed").GetValue(work))
                {
                    Assert(timeout.ElapsedMilliseconds < 10000, "Cancelled disposed worker never completed.");
                    Application.DoEvents();
                    Thread.Sleep(1);
                }
                Equal(0, (int)PrivateField(image, "pixelReferences"));
                Assert(PrivateField(work, "result") == null, "Disposed canvas retained a late rendered bitmap.");
            }
            finally { board.Dispose(); }
        });
        Run("zero-distance clicks after wheel navigation still produce a refined final view", delegate {
            for (int target = 0; target < 2; target++)
            using (BoardCanvas board = NewBoard())
            using (Bitmap source = Pattern(1600, 1200))
            {
                IntPtr handle = board.Handle;
                board.AddImageAt(source, PointF.Empty);
                board.ZoomAt(new Point(500, 360), 60);
                using (Bitmap fast = RenderBoard(board)) { }
                Point point = target == 0 ? new Point(15, 15) : new Point(500, 360);
                SendBoardMouseClient(board, "OnMouseDown", MouseButtons.Left, point);
                SendBoardMouseClient(board, "OnMouseUp", MouseButtons.Left, point);
                SettleBoardRefinement(board);
                CompareRefinedBoardCache(board, "zero-distance click target " + target);
            }
        });
        Run("annotations committed before the pending camera paints use the latest world transform", delegate {
            foreach (DrawingTool tool in new DrawingTool[] { DrawingTool.Text, DrawingTool.Pen })
            using (BoardCanvas board = NewBoard())
            using (Bitmap source = Pattern(600, 400))
            {
                IntPtr handle = board.Handle;
                board.AddImageAt(source, PointF.Empty);
                SettleBoardRefinement(board);
                board.PanBy(130, -60); // Deliberately no paint before the next editing event.
                board.Tool = tool;
                board.StrokeColor = Color.Lime;
                board.StrokeWidth = 10;
                board.RequestText = delegate { return "Latest camera"; };
                BoardDrag(board, new PointF(-150, 220), new PointF(200, 280));
                using (Bitmap current = RenderBoard(board)) { }
                CompareBoardCacheWithRebuild(board, "pending camera commit " + tool);
                SettleBoardRefinement(board);
                CompareRefinedBoardCache(board, "pending camera refined " + tool);
                using (Bitmap exported = board.ExportImage())
                    Assert(CountNonWhite(board, exported, new RectangleF(-170, 205, 1000, 150)) > 100,
                        "Annotation did not remain at its world coordinates.");
                Equal(2, BoardHistoryCount(board));
            }
        });
    }

    private static BoardViewSnapshot NewViewSnapshot()
    {
        return new BoardViewSnapshot {
            ViewSize = new Size(120, 90), DpiScale = 1, Scale = 1,
            Origin = PointF.Empty, Background = Color.White
        };
    }
    private static void SettleBoardRefinement(BoardCanvas board)
    {
        PropertyInfo pending = typeof(BoardCanvas).GetProperty("IsRefinementPending",
            BindingFlags.Instance | BindingFlags.NonPublic);
        using (Bitmap quick = RenderBoard(board)) { }
        FinishBoardView(board);
        Stopwatch timeout = Stopwatch.StartNew();
        while ((bool)pending.GetValue(board, null))
        {
            FinishBoardView(board);
            Application.DoEvents();
            using (Bitmap current = RenderBoard(board)) { }
            Assert(timeout.ElapsedMilliseconds < 10000, "Background refinement never settled.");
            Thread.Sleep(1);
        }
        using (Bitmap settled = RenderBoard(board)) { }
    }
    private static void CompareRefinedBoardCache(BoardCanvas board, string description)
    {
        using (Bitmap reference = (Bitmap)typeof(BoardCanvas).GetMethod("BuildRefinedViewportForValidation",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(board, null))
            FastPixelsEqual(reference, (Bitmap)PrivateField(board, "_viewportCache"), description);
    }
}
