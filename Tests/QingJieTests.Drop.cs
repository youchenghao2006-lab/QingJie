using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunBoardDropTests()
    {
        Run("infinite board insertion preserves negative and distant world positions", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                board.AddImageAt(image, new PointF(900, 800));
                Equal(new RectangleF(800, 725, 200, 150), BoardImageBounds(board, 0));
                board.AddImageAt(image, new PointF(-200, -100));
                Equal(new RectangleF(-300, -175, 200, 150), BoardImageBounds(board, 1));
                board.AddImageAt(image, new PointF(10000, 10000));
                Equal(new RectangleF(9900, 9925, 200, 150), BoardImageBounds(board, 2));
                Equal(new RectangleF(-300, -175, 10400, 10250), board.ContentBounds);
            }
        });
        Run("board insertion rejects null and nonfinite coordinates without modifying board", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                Throws<ArgumentNullException>(delegate { board.AddImageAt(null, new PointF(10, 10)); });
                Throws<ArgumentOutOfRangeException>(delegate { board.AddImageAt(image, new PointF(Single.NaN, 10)); });
                Throws<ArgumentOutOfRangeException>(delegate { board.AddImageAt(image, new PointF(10, Single.PositiveInfinity)); });
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo, "Rejected coordinates added a history entry.");
            }
        });
        Run("bitmap drag enter and over accept copy and preview the latest valid location", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                DataObject data = new DataObject(DataFormats.Bitmap, image);
                DragEventArgs enter = SendBoardDrag(board, "OnDragEnter", data, new PointF(400, 400), DragDropEffects.Copy);
                Equal(DragDropEffects.Copy, enter.Effect);
                Assert(board.DropPreviewVisible, "Valid bitmap did not show a drop outline.");
                DragEventArgs over = SendBoardDrag(board, "OnDragOver", data, new PointF(-300, -200), DragDropEffects.Copy);
                Equal(DragDropEffects.Copy, over.Effect);
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo, "Hovering created an image or history entry.");
            }
        });
        Run("bitmap drop uses board coordinates owns its pixels raises once and can undo", delegate {
            using (BoardCanvas board = NewBoard())
            {
                int dropped = 0;
                board.ImageDropped += delegate { dropped++; };
                using (Bitmap image = Solid(200, 150, Color.Red))
                {
                    DataObject data = new DataObject(DataFormats.Bitmap, image);
                    SendBoardDrag(board, "OnDragEnter", data, new PointF(400, 400), DragDropEffects.Copy);
                    SendBoardDrag(board, "OnDragOver", data, new PointF(-300, -200), DragDropEffects.Copy);
                    DragEventArgs drop = SendBoardDrag(board, "OnDragDrop", data, new PointF(-300, -200), DragDropEffects.Copy);
                    Equal(DragDropEffects.Copy, drop.Effect);
                    image.SetPixel(100, 75, Color.Black);
                }
                Equal(1, dropped);
                Equal(1, board.ImageCount);
                Assert(!board.DropPreviewVisible, "Drop outline remained after completing drop.");
                RectangleF placed = BoardImageBounds(board, 0);
                PointF center = Center(placed);
                Assert(Math.Abs(center.X + 300) < 4 && Math.Abs(center.Y + 200) < 4,
                    "Dropped image did not follow the final pointer position.");
                using (Bitmap exported = board.ExportImage())
                    Equal(Color.Red.ToArgb(), PixelAtCenter(board, exported, placed).ToArgb());
                board.Undo();
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo, "Undoing one dropped image retained a stale history entry.");
                Equal(1, dropped);
            }
        });
        Run("bitmap drag outside the viewport cancels preview and never inserts an image", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                DataObject data = new DataObject(DataFormats.Bitmap, image);
                SendBoardDrag(board, "OnDragEnter", data, new PointF(400, 400), DragDropEffects.Copy);
                DragEventArgs outside = SendBoardDragClient(board, "OnDragOver", data,
                    new Point(-1, 1), DragDropEffects.Copy);
                Equal(DragDropEffects.None, outside.Effect);
                Assert(!board.DropPreviewVisible, "Invalid location kept the drop outline.");
                DragEventArgs drop = SendBoardDragClient(board, "OnDragDrop", data,
                    new Point(-1, 1), DragDropEffects.Copy);
                Equal(DragDropEffects.None, drop.Effect);
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo, "Rejected drop altered history.");
            }
        });
        Run("drag rejects text invalid bitmap payloads and move-only transfers", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                DataObject[] rejected = {
                    new DataObject(DataFormats.Text, "not an image"),
                    new DataObject(DataFormats.Bitmap, "invalid bitmap")
                };
                foreach (DataObject data in rejected)
                {
                    Equal(DragDropEffects.None, SendBoardDrag(board, "OnDragEnter", data,
                        new PointF(400, 400), DragDropEffects.Copy).Effect);
                    SendBoardDrag(board, "OnDragDrop", data, new PointF(400, 400), DragDropEffects.Copy);
                }
                DataObject bitmap = new DataObject(DataFormats.Bitmap, image);
                Equal(DragDropEffects.None, SendBoardDrag(board, "OnDragEnter", bitmap,
                    new PointF(400, 400), DragDropEffects.Move).Effect);
                SendBoardDrag(board, "OnDragDrop", bitmap, new PointF(400, 400), DragDropEffects.Move);
                Equal(0, board.ImageCount);
                Assert(!board.DropPreviewVisible && !board.CanUndo, "Rejected payload changed editing state.");
            }
        });
        Run("disabled board or disabled image drops cannot accept a dragged bitmap", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                DataObject data = new DataObject(DataFormats.Bitmap, image);
                board.ImageDropEnabled = false;
                Equal(DragDropEffects.None, SendBoardDrag(board, "OnDragEnter", data,
                    new PointF(400, 400), DragDropEffects.Copy).Effect);
                SendBoardDrag(board, "OnDragDrop", data, new PointF(400, 400), DragDropEffects.Copy);
                board.ImageDropEnabled = true;
                board.Enabled = false;
                Equal(DragDropEffects.None, SendBoardDrag(board, "OnDragEnter", data,
                    new PointF(400, 400), DragDropEffects.Copy).Effect);
                SendBoardDrag(board, "OnDragDrop", data, new PointF(400, 400), DragDropEffects.Copy);
                Equal(0, board.ImageCount);
                Assert(!board.CanUndo, "Disabled drop created history.");
            }
        });
        Run("leaving a valid bitmap drop target clears outline without changing composition", delegate {
            using (BoardCanvas board = NewBoard())
            using (Bitmap image = Solid(200, 150, Color.Red))
            {
                DataObject data = new DataObject(DataFormats.Bitmap, image);
                SendBoardDrag(board, "OnDragEnter", data, new PointF(400, 400), DragDropEffects.Copy);
                Assert(board.DropPreviewVisible, "No outline to cancel.");
                typeof(BoardCanvas).GetMethod("OnDragLeave", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(board, new object[] { EventArgs.Empty });
                Assert(!board.DropPreviewVisible && !board.CanUndo, "Leaving retained drop state.");
                Equal(0, board.ImageCount);
            }
        });
        Run("file drops inside board route once to host without claiming current screenshot insertion", delegate {
            using (BoardCanvas board = NewBoard())
            {
                int requests = 0, imagesDropped = 0;
                string[] received = null;
                board.RequestFileDrop = delegate(string[] paths) { requests++; received = paths; };
                board.ImageDropped += delegate { imagesDropped++; };
                string[] pathsToRoute = { @"C:\synthetic-test\first.png", @"C:\synthetic-test\second.png" };
                DataObject data = new DataObject(DataFormats.FileDrop, pathsToRoute);
                DragEventArgs outside = SendBoardDragClient(board, "OnDragDrop", data,
                    new Point(-1, 1), DragDropEffects.Copy);
                Equal(DragDropEffects.None, outside.Effect);
                Equal(0, requests);
                Equal(DragDropEffects.Copy, SendBoardDrag(board, "OnDragEnter", data,
                    new PointF(600, 500), DragDropEffects.Copy).Effect);
                Equal(DragDropEffects.Copy, SendBoardDrag(board, "OnDragDrop", data,
                    new PointF(600, 500), DragDropEffects.Copy).Effect);
                Equal(1, requests);
                Equal(2, received.Length);
                Equal(pathsToRoute[0], received[0]);
                Equal(pathsToRoute[1], received[1]);
                Equal(0, imagesDropped);
                Equal(0, board.ImageCount);
                Assert(!board.DropPreviewVisible && !board.CanUndo,
                    "Routing files retained bitmap-drop state or altered history.");
            }
        });
    }

    private static DragEventArgs SendBoardDrag(BoardCanvas board, string method, IDataObject data,
        PointF boardPoint, DragDropEffects allowed)
    {
        return SendBoardDragClient(board, method, data, BoardClientPoint(board, boardPoint), allowed);
    }

    private static DragEventArgs SendBoardDragClient(BoardCanvas board, string method, IDataObject data,
        Point clientPoint, DragDropEffects allowed)
    {
        // PointToScreen creates only this synthetic control's handle. No desktop pixels
        // are read, and no mouse/keyboard messages are sent to any actual window.
        Point screen = board.PointToScreen(clientPoint);
        DragEventArgs args = new DragEventArgs(data, 0, screen.X, screen.Y, allowed, DragDropEffects.None);
        typeof(BoardCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(board, new object[] { args });
        return args;
    }
}
