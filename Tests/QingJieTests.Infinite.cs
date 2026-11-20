using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunInfiniteBoardTests()
    {
        Run("infinite export rounds content outwards with exactly 32 pixels of white padding", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.AddImageAt(image, new PointF(-200.25f, -100.5f));
                Equal(new RectangleF(-300.25f, -175.5f, 200, 150), board.ContentBounds);
                Equal(new RectangleF(-333, -208, 265, 215), board.ExportBounds);
                Equal(new Size(265, 215), board.ExportSize);
                Assert(board.HasContent, "An inserted image was not exportable.");
                using (Bitmap before = board.ExportImage())
                {
                    Equal(board.ExportSize, before.Size);
                    Equal(Color.Red.ToArgb(), PixelAtWorld(board, before, new PointF(-200, -100)).ToArgb());
                    AssertWhiteExportBorder(before, 30);
                    board.ZoomAt(new Point(270, 190), 220);
                    board.PanBy(1900, -1700);
                    board.FitToWindow();
                    using (Bitmap after = board.ExportImage()) FastPixelsEqual(before, after, "infinite view-only export");
                }
            }
        });
        Run("select left-drag pans empty space while image left-drag moves only the image", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(160, 120, Color.Blue))
            {
                board.AddImageAt(image, PointF.Empty);
                RectangleF original = BoardImageBounds(board, 0);
                int history = BoardHistoryCount(board);
                PointF origin = board.BoardToClient(PointF.Empty);
                SendBoardMouseClient(board, "OnMouseDown", MouseButtons.Left, new Point(80, 80));
                SendBoardMouseClient(board, "OnMouseMove", MouseButtons.Left, new Point(210, 155));
                SendBoardMouseClient(board, "OnMouseUp", MouseButtons.Left, new Point(210, 155));
                Near(new PointF(origin.X + 130, origin.Y + 75), board.BoardToClient(PointF.Empty), .01f, "blank left pan");
                Equal(original, BoardImageBounds(board, 0));
                Equal(history, BoardHistoryCount(board));
                PointF camera = board.BoardToClient(PointF.Empty);
                BoardDrag(board, PointF.Empty, new PointF(-120, -80));
                Near(camera, board.BoardToClient(PointF.Empty), .01f, "image move camera");
                Near(new PointF(-120, -80), Center(BoardImageBounds(board, 0)), 2, "image left drag");
                board.Undo();
                Equal(original, BoardImageBounds(board, 0));
            }
        });
        Run("bitmap drop accepts every viewport corner in a distant negative world view", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(100, 80, Color.Red))
            {
                CenterBoardView(board, new PointF(-6000, -8000), 50);
                Point[] corners = { new Point(1, 1), new Point(board.Width - 2, 1),
                    new Point(1, board.Height - 2), new Point(board.Width - 2, board.Height - 2) };
                foreach (Point corner in corners)
                {
                    PointF expected = board.ClientToBoard(corner, false);
                    Equal(DragDropEffects.Copy, SendBoardDragClient(board, "OnDragDrop",
                        new DataObject(DataFormats.Bitmap, image), corner, DragDropEffects.Copy).Effect);
                    Near(expected, Center(BoardImageBounds(board, board.ImageCount - 1)), .01f, "viewport edge drop");
                }
                Equal(4, board.ImageCount);
                Assert(board.ContentBounds.Right < -4000 && board.ContentBounds.Bottom < -6000,
                    "Negative view drops were clamped to a fixed paper.");
            }
        });
        Run("automatic image insertion stays near the current view and preserves the camera", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(220, 160, Color.Blue))
            {
                CenterBoardView(board, new PointF(9000, -7000), 80);
                float zoom = board.ZoomPercent;
                PointF origin = board.BoardToClient(PointF.Empty);
                board.AddImage(image);
                Near(origin, board.BoardToClient(PointF.Empty), .01f, "first image camera");
                Equal(zoom, board.ZoomPercent);
                PointF firstCenter = board.BoardToClient(Center(BoardImageBounds(board, 0)));
                Assert(board.ClientRectangle.Contains(Point.Round(firstCenter)), "New image was added outside the current view.");
                board.AddImage(image);
                Near(origin, board.BoardToClient(PointF.Empty), .01f, "second image camera");
                Equal(zoom, board.ZoomPercent);
                Assert(!BoardImageBounds(board, 0).IntersectsWith(BoardImageBounds(board, 1)),
                    "Automatic insertion hid the first image directly beneath the second.");
            }
        });
        Run("fit includes negative images and distant arrow pen rectangle and text annotations", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.AddImageAt(image, new PointF(-2500, -1800));
                CenterBoardView(board, new PointF(4200, 2400), 50);
                board.Tool = DrawingTool.Arrow;
                board.StrokeWidth = 18;
                BoardDrag(board, new PointF(3900, 2200), new PointF(4500, 2600));
                board.Tool = DrawingTool.Pen;
                BoardDrag(board, new PointF(4000, 2700), new PointF(4400, 2750));
                board.Tool = DrawingTool.Rectangle;
                BoardDrag(board, new PointF(3950, 2300), new PointF(4300, 2550));
                board.Tool = DrawingTool.Text;
                board.RequestText = delegate { return "Outside the old paper"; };
                BoardMouse(board, "OnMouseDown", new PointF(4050, 2800));
                Assert(board.ContentBounds.Left <= -2600 && board.ContentBounds.Top <= -1875 &&
                    board.ContentBounds.Right >= 4500 && board.ContentBounds.Bottom > 2800,
                    "True content bounds omitted distant images or annotations.");
                board.FitToWindow();
                RectangleF world = board.ContentBounds;
                PointF topLeft = board.BoardToClient(world.Location);
                PointF bottomRight = board.BoardToClient(new PointF(world.Right, world.Bottom));
                Assert(topLeft.X >= -1 && topLeft.Y >= -1 &&
                    bottomRight.X <= board.Width + 1 && bottomRight.Y <= board.Height + 1,
                    "Fit left some content outside the viewport.");
                Equal(1, board.ImageCount);
                Equal(5, BoardHistoryCount(board));
            }
        });
        Run("annotation-only negative content exports without clipping and undo returns to empty", delegate {
            using (BoardCanvas board = NewBoard())
            {
                board.Tool = DrawingTool.Pen;
                board.StrokeColor = Color.Red;
                board.StrokeWidth = 20;
                BoardDrag(board, new PointF(-500, -300), new PointF(-200, -150));
                Assert(board.HasContent && board.ImageCount == 0, "Annotation-only board was considered empty.");
                Assert(board.ContentBounds.Left < -500 && board.ContentBounds.Top < -300,
                    "Pen stroke thickness was omitted from bounds.");
                using (Bitmap image = board.ExportImage())
                {
                    Assert(CountNonWhite(board, image, board.ContentBounds) > 1500, "Negative pen was missing from export.");
                    AssertWhiteExportBorder(image, 28);
                }
                board.Undo();
                Assert(!board.HasContent && !board.CanUndo, "Undo retained annotation-only content.");
                Equal(Size.Empty, board.ExportSize);
            }
        });
        Run("oversized infinite exports fail before allocating an invalid bitmap", delegate {
            using (Bitmap dot = Solid(10, 10, Color.Black))
            {
                using (BoardCanvas wide = NewBoard())
                {
                    wide.AddImageAt(dot, PointF.Empty);
                    wide.AddImageAt(dot, new PointF(40000, 0));
                    Throws<InvalidOperationException>(delegate { wide.ExportImage(); });
                    Equal(2, wide.ImageCount);
                }
                using (BoardCanvas area = NewBoard())
                {
                    area.AddImageAt(dot, new PointF(-5000, -5000));
                    area.AddImageAt(dot, new PointF(5000, 5000));
                    Throws<InvalidOperationException>(delegate { area.ExportImage(); });
                    Equal(2, area.ImageCount);
                }
            }
        });
        Run("embedded workspace roundtrips preserve infinite view position and zoom", delegate {
            using (MainForm main = NewHiddenMain())
            using (Bitmap image = Solid(300, 200, Color.Red))
            {
                InvokePrivate(main, "SetCurrentImage", image);
                InvokePrivate(main, "OpenBoard", false);
                BoardWorkspace workspace = (BoardWorkspace)PrivateField(main, "board");
                BoardCanvas board = (BoardCanvas)PrivateField(workspace, "canvas");
                workspace.AddImage(image);
                CenterBoardView(board, new PointF(-2600, 4300), 135);
                PointF origin = board.BoardToClient(PointF.Empty);
                float zoom = board.ZoomPercent;
                for (int i = 0; i < 3; i++)
                {
                    InvokePrivate(main, "ShowScreenshotEditor");
                    InvokePrivate(main, "OpenBoard", false);
                    Equal(zoom, board.ZoomPercent);
                    Near(origin, board.BoardToClient(PointF.Empty), .01f, "workspace view roundtrip");
                }
            }
        });
    }

    private static int BoardHistoryCount(BoardCanvas board)
    {
        return ((IList)typeof(BoardCanvas).GetField("_undo",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board)).Count;
    }
    private static void CenterBoardView(BoardCanvas board, PointF world, float percent)
    {
        Point center = new Point(board.Width / 2, board.Height / 2);
        board.ZoomAt(center, percent);
        PointF actual = board.BoardToClient(world);
        board.PanBy(center.X - actual.X, center.Y - actual.Y);
        FinishBoardView(board);
    }
    private static void AssertWhiteExportBorder(Bitmap image, int width)
    {
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (x < width || y < width || x >= image.Width - width || y >= image.Height - width)
                    Equal(Color.White.ToArgb(), image.GetPixel(x, y).ToArgb());
    }
}
