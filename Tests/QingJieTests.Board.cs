using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunBoardFeatureTests()
    {
        Run("empty infinite board has no exportable content and rejects allocation", delegate {
            using (BoardCanvas board = NewBoard())
            {
                Equal(0, board.ImageCount);
                Assert(!board.HasSelection && !board.CanUndo, "Empty board retained editing state.");
                Assert(!board.HasContent, "Empty board reported content.");
                Equal(Size.Empty, board.ExportSize);
                Throws<InvalidOperationException>(delegate { board.ExportImage(); });
            }
        });
        Run("board combines independently owned images on a white background", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                Equal(2, board.ImageCount);
                RectangleF first = BoardImageBounds(board, 0);
                RectangleF second = BoardImageBounds(board, 1);
                Assert(!first.IntersectsWith(second), "Automatically placed images overlap.");
                using (Bitmap exported = board.ExportImage())
                {
                    Equal(board.ExportSize, exported.Size);
                    Equal(Color.Red.ToArgb(), PixelAtCenter(board, exported, first).ToArgb());
                    Equal(Color.Blue.ToArgb(), PixelAtCenter(board, exported, second).ToArgb());
                    Equal(Color.White.ToArgb(), exported.GetPixel(0, 0).ToArgb());
                    Point exportPoint = ExportPoint(board, Center(first));
                    exported.SetPixel(exportPoint.X, exportPoint.Y, Color.Black);
                }
                using (Bitmap exported = board.ExportImage())
                    Equal(Color.Red.ToArgb(), PixelAtCenter(board, exported, first).ToArgb());
            }
        });
        Run("board moves selected image and undo restores exact composition", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                RectangleF beforeBounds = BoardImageBounds(board, 0);
                using (Bitmap before = board.ExportImage())
                {
                    PointF center = Center(beforeBounds);
                    BoardDrag(board, center, new PointF(center.X + 120, center.Y + 100));
                    RectangleF moved = BoardImageBounds(board, 0);
                    Assert(board.HasSelection, "Dragging did not select the image.");
                    Assert(moved.X > beforeBounds.X + 110 && moved.Y > beforeBounds.Y + 90,
                        "Image did not move to the dragged position.");
                    board.Undo();
                    Equal(beforeBounds, BoardImageBounds(board, 0));
                    using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, "move undo");
                }
            }
        });
        Run("board corner resize and undo retain image proportions and placement", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                RectangleF original = BoardImageBounds(board, 0);
                BoardDrag(board, Center(original), Center(original));
                Assert(board.HasSelection, "Click did not select image.");
                BoardDrag(board, new PointF(original.Right, original.Bottom),
                    new PointF(original.Right + 120, original.Bottom + 90));
                RectangleF resized = BoardImageBounds(board, 0);
                Assert(resized.Width > original.Width + 50 && resized.Height > original.Height + 40,
                    "Corner drag did not enlarge the image.");
                Assert(Math.Abs(resized.Width / resized.Height - original.Width / original.Height) < .03,
                    "Corner resize unexpectedly distorted the aspect ratio.");
                board.Undo();
                Equal(original, BoardImageBounds(board, 0));
            }
        });
        Run("board delete affects only selected image and undo restores it", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                RectangleF first = BoardImageBounds(board, 0);
                RectangleF second = BoardImageBounds(board, 1);
                using (Bitmap before = board.ExportImage())
                {
                    BoardDrag(board, Center(first), Center(first));
                    board.DeleteSelected();
                    Equal(1, board.ImageCount);
                    Assert(!board.HasSelection, "Deleted image remained selected.");
                    using (Bitmap exported = board.ExportImage())
                    {
                        Assert(!board.ContentBounds.Contains(Center(first)), "Deleted image still expands export bounds.");
                        Equal(Color.Blue.ToArgb(), PixelAtCenter(board, exported, second).ToArgb());
                    }
                    board.Undo();
                    Equal(2, board.ImageCount);
                    using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, "delete undo");
                }
            }
        });
        Run("board cross-image arrow appears between images and undoes exactly", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                PointF start = Center(BoardImageBounds(board, 0));
                PointF end = Center(BoardImageBounds(board, 1));
                using (Bitmap before = board.ExportImage())
                {
                    board.Tool = DrawingTool.Arrow;
                    board.StrokeColor = Color.Lime;
                    board.StrokeWidth = 14;
                    BoardDrag(board, start, end);
                    using (Bitmap exported = board.ExportImage())
                    {
                        Point middle = ExportPoint(board, new PointF((start.X + end.X) / 2, (start.Y + end.Y) / 2));
                        Color pixel = exported.GetPixel(middle.X, middle.Y);
                        Assert(pixel.G > 200 && pixel.R < 60 && pixel.B < 60,
                            "Arrow did not cross the gap between images.");
                        Equal(2, board.ImageCount);
                    }
                    board.Undo();
                    using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, "cross-image arrow undo");
                }
            }
        });
        Run("board pen and rectangle export at board coordinates and undo", delegate {
            foreach (DrawingTool tool in new DrawingTool[] { DrawingTool.Pen, DrawingTool.Rectangle })
            {
                using (BoardCanvas board = NewBoard())
                {
                    AddBoardPair(board);
                    board.Tool = tool;
                    board.StrokeColor = Color.Lime;
                    board.StrokeWidth = 12;
                    using (Bitmap before = board.ExportImage())
                    {
                        BoardDrag(board, new PointF(-200, 200), new PointF(200, 300));
                        using (Bitmap exported = board.ExportImage())
                            Assert(CountNonWhite(board, exported, new RectangleF(-220, 180, 450, 150)) > 100,
                                tool + " did not paint at original board coordinates.");
                        board.Undo();
                        using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, tool + " undo");
                    }
                }
            }
        });
        Run("board text callback renders annotation without a modal dialog", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                board.Tool = DrawingTool.Text;
                board.StrokeColor = Color.Black;
                board.StrokeWidth = 6;
                int requests = 0;
                board.RequestText = delegate { requests++; return "Board annotation"; };
                using (Bitmap before = board.ExportImage())
                {
                    BoardMouse(board, "OnMouseDown", new PointF(-200, 200));
                    Equal(1, requests);
                    using (Bitmap exported = board.ExportImage())
                        Assert(CountNonWhite(board, exported, new RectangleF(-220, 180, 900, 150)) > 40,
                            "Text callback did not produce exported text.");
                    board.Undo();
                    using (Bitmap restored = board.ExportImage()) FastPixelsEqual(before, restored, "text undo");
                }
            }
        });
        Run("board cancelled drawing and cancelled text do not alter composition", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                using (Bitmap before = board.ExportImage())
                {
                    board.Tool = DrawingTool.Pen;
                    BoardMouse(board, "OnMouseDown", new PointF(-200, 200));
                    BoardMouse(board, "OnMouseMove", new PointF(200, 300));
                    board.Tool = DrawingTool.Select;
                    BoardMouse(board, "OnMouseUp", new PointF(200, 300));
                    board.Tool = DrawingTool.Text;
                    board.RequestText = delegate { return null; };
                    BoardMouse(board, "OnMouseDown", new PointF(-200, 200));
                    using (Bitmap exported = board.ExportImage()) FastPixelsEqual(before, exported, "cancelled board editing");
                    board.Undo();
                    Equal(1, board.ImageCount);
                }
            }
        });
        Run("board undo removes image additions and returns to empty state", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                board.Undo();
                Equal(1, board.ImageCount);
                board.Undo();
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo && !board.HasSelection, "Undoing all additions left stale state.");
                board.DeleteSelected();
                Equal(0, board.ImageCount);
            }
        });
        Run("board drag keeps later translucent images above the moving image", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap red = Solid(200, 150, Color.Red))
            using (Bitmap translucent = Solid(200, 150, Color.FromArgb(128, 0, 0, 255)))
            {
                board.AddImageAt(red, new PointF(-220, 0));
                board.AddImageAt(translucent, new PointF(220, 0));
                RectangleF first = BoardImageBounds(board, 0);
                RectangleF second = BoardImageBounds(board, 1);
                BoardMouse(board, "OnMouseDown", Center(first));
                BoardMouse(board, "OnMouseMove", Center(second));
                using (Bitmap preview = RenderBoard(board))
                using (Bitmap exported = board.ExportImage())
                {
                    PointF middle = Center(second);
                    Point exportPoint = ExportPoint(board, middle);
                    Color expected = exported.GetPixel(exportPoint.X, exportPoint.Y);
                    Point viewportPoint = BoardClientPoint(board, middle);
                    Color actual = preview.GetPixel(viewportPoint.X, viewportPoint.Y);
                    Assert(Math.Abs(expected.R - actual.R) <= 2 &&
                        Math.Abs(expected.G - actual.G) <= 2 &&
                        Math.Abs(expected.B - actual.B) <= 2,
                        "Drag preview changed layer order/alpha: expected " + expected + ", actual " + actual);
                }
                BoardMouse(board, "OnMouseUp", Center(second));
                board.Undo();
                Equal(first, BoardImageBounds(board, 0));
            }
        });
        Run("board commits a long pen to existing cache and matches rebuilt preview", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                using (Bitmap initial = RenderBoard(board)) { }
                FieldInfo cacheField = typeof(BoardCanvas).GetField("_viewportCache",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object cache = cacheField.GetValue(board);
                board.Tool = DrawingTool.Pen;
                board.StrokeColor = Color.Lime;
                board.StrokeWidth = 12;
                PointF last = new PointF(-400, 250);
                BoardMouse(board, "OnMouseDown", last);
                for (int i = 1; i <= 150; i++)
                {
                    last = new PointF(-400 + i * 5, 250 + (float)Math.Sin(i * .1) * 90);
                    BoardMouse(board, "OnMouseMove", last);
                }
                BoardMouse(board, "OnMouseUp", last);
                Assert(Object.ReferenceEquals(cache, cacheField.GetValue(board)),
                    "A committed stroke needlessly replaced the background cache.");
                using (Bitmap committed = RenderBoard(board))
                {
                    typeof(BoardCanvas).GetField("_viewportDirty", BindingFlags.Instance |
                        BindingFlags.NonPublic).SetValue(board, true);
                    using (Bitmap rebuilt = RenderBoard(board))
                        FastPixelsEqual(committed, rebuilt, "appended versus rebuilt board cache");
                }
                using (Bitmap exported = board.ExportImage())
                    Assert(PixelAtWorld(board, exported, new PointF(-400, 250)).G > 190,
                        "Long pen was not available to immediate original-resolution export.");
            }
        });
        Run("board clear removes composition and history and allows fresh images", delegate {
            using (BoardCanvas board = NewBoard())
            {
                AddBoardPair(board);
                board.Tool = DrawingTool.Pen;
                BoardDrag(board, new PointF(-200, 200), new PointF(200, 300));
                board.ClearBoard();
                Equal(0, board.ImageCount);
                Equal(Size.Empty, board.ExportSize);
                Assert(!board.CanUndo && !board.HasSelection, "Clear retained stale history or selection.");
                Assert(!board.HasContent, "Clear retained export bounds.");
                Throws<InvalidOperationException>(delegate { board.ExportImage(); });
                AddBoardPair(board);
                Equal(2, board.ImageCount);
                board.Undo();
                Equal(1, board.ImageCount);
            }
        });
    }

    private static BoardCanvas NewBoard()
    {
        BoardCanvas board = new BoardCanvas();
        board.Size = new Size(1000, 720);
        board.Tool = DrawingTool.Select;
        return board;
    }
    private static void AddBoardPair(BoardCanvas board)
    {
        // Disposing both inputs immediately also verifies board-owned source copies.
        using (Bitmap red = Solid(200, 150, Color.Red))
        using (Bitmap blue = Solid(200, 150, Color.Blue))
        {
            board.AddImageAt(red, new PointF(-220, 0));
            board.AddImageAt(blue, new PointF(220, 0));
        }
    }
    private static RectangleF BoardImageBounds(BoardCanvas board, int index)
    {
        IList images = (IList)typeof(BoardCanvas).GetField("_images",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(board);
        object image = images[index];
        FieldInfo field = image.GetType().GetField("Bounds",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return (RectangleF)field.GetValue(image);
    }
    private static PointF Center(RectangleF rectangle)
    {
        return new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
    }
    private static Color PixelAtCenter(BoardCanvas board, Bitmap bitmap, RectangleF rectangle)
    {
        return PixelAtWorld(board, bitmap, Center(rectangle));
    }
    private static Point ExportPoint(BoardCanvas board, PointF world)
    {
        return Point.Round(new PointF(world.X - board.ExportBounds.Left, world.Y - board.ExportBounds.Top));
    }
    private static Color PixelAtWorld(BoardCanvas board, Bitmap bitmap, PointF world)
    {
        Point point = ExportPoint(board, world);
        return bitmap.GetPixel(point.X, point.Y);
    }
    private static int CountNonWhite(BoardCanvas board, Bitmap bitmap, RectangleF world)
    {
        Rectangle region = Rectangle.Round(new RectangleF(world.X - board.ExportBounds.X,
            world.Y - board.ExportBounds.Y, world.Width, world.Height));
        region.Intersect(new Rectangle(Point.Empty, bitmap.Size));
        int count = 0;
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != Color.White.ToArgb()) count++;
        return count;
    }
    private static void BoardDrag(BoardCanvas board, PointF start, PointF end)
    {
        BoardMouse(board, "OnMouseDown", start);
        BoardMouse(board, "OnMouseMove", end);
        BoardMouse(board, "OnMouseUp", end);
    }
    private static void BoardMouse(BoardCanvas board, string method, PointF boardPoint)
    {
        Point point = BoardClientPoint(board, boardPoint);
        typeof(BoardCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(board, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    }
    private static Point BoardClientPoint(BoardCanvas board, PointF boardPoint)
    {
        return Point.Round(board.BoardToClient(boardPoint));
    }
    private static Bitmap RenderBoard(BoardCanvas board)
    {
        Bitmap result = new Bitmap(board.Width, board.Height, PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using (Graphics graphics = Graphics.FromImage(result))
            typeof(BoardCanvas).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(board, new object[] { new PaintEventArgs(graphics, board.ClientRectangle) });
        return result;
    }
    private static int ChangedRegion(Bitmap before, Bitmap after, Rectangle region)
    {
        region.Intersect(new Rectangle(Point.Empty, before.Size));
        int count = 0;
        for (int y = region.Top; y < region.Bottom; y++)
            for (int x = region.Left; x < region.Right; x++)
                if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb()) count++;
        return count;
    }
    private static void FastPixelsEqual(Bitmap expected, Bitmap actual, string description)
    {
        Equal(expected.Size, actual.Size);
        Rectangle rectangle = new Rectangle(Point.Empty, expected.Size);
        BitmapData a = expected.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData b = actual.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int[] first = new int[expected.Width], second = new int[expected.Width];
            for (int y = 0; y < expected.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), first, 0, first.Length);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), second, 0, second.Length);
                for (int x = 0; x < first.Length; x++)
                    if (first[x] != second[x])
                        throw new Exception(description + " pixel mismatch at " + x + "," + y +
                            " expected=" + first[x].ToString("X8") + " actual=" + second[x].ToString("X8"));
            }
        }
        finally { expected.UnlockBits(a); actual.UnlockBits(b); }
    }
}
