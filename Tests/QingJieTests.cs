using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static int passed;
    private static int failed;

    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Run("selection keeps normal bounds", delegate {
            Equal(new Rectangle(4, 6, 23, 31),
                ScreenCapture.NormalizeSelection(new Point(4, 6), new Point(27, 37), new Size(80, 60)));
        });
        Run("selection normalizes reverse drag", delegate {
            Equal(new Rectangle(4, 6, 23, 31),
                ScreenCapture.NormalizeSelection(new Point(27, 37), new Point(4, 6), new Size(80, 60)));
        });
        Run("selection clips to all image edges", delegate {
            Equal(new Rectangle(0, 0, 80, 60),
                ScreenCapture.NormalizeSelection(new Point(120, -12), new Point(-30, 90), new Size(80, 60)));
        });
        Run("selection supports last pixel at right and bottom", delegate {
            Equal(new Rectangle(79, 59, 1, 1),
                ScreenCapture.NormalizeSelection(new Point(80, 60), new Point(79, 59), new Size(80, 60)));
        });
        Run("selection cannot create out-of-image area", delegate {
            Rectangle selection = ScreenCapture.NormalizeSelection(new Point(-30, 5), new Point(-10, 15), new Size(80, 60));
            Assert(selection.Width == 0 || selection.Height == 0, "Fully outside selection must be empty.");
        });
        Run("selection handles extreme drag coordinates", delegate {
            Equal(new Rectangle(0, 0, 80, 60),
                ScreenCapture.NormalizeSelection(new Point(int.MaxValue, int.MinValue), new Point(int.MinValue, int.MaxValue), new Size(80, 60)));
        });
        Run("crop preserves every selected original pixel", delegate {
            using (Bitmap source = Pattern(127, 83))
            using (Bitmap cropped = ScreenCapture.Crop(source, new Rectangle(21, 13, 62, 45)))
            {
                Equal(new Size(62, 45), cropped.Size);
                PixelsEqual(source, cropped, new Point(21, 13));
            }
        });
        Run("full image crop has no resampling or border loss", delegate {
            using (Bitmap source = Pattern(127, 83))
            using (Bitmap cropped = ScreenCapture.Crop(source, new Rectangle(Point.Empty, source.Size)))
                PixelsEqual(source, cropped, Point.Empty);
        });
        Run("crop owns its independent pixel data", delegate {
            Bitmap source = Pattern(25, 18);
            int expected = source.GetPixel(5, 4).ToArgb();
            using (Bitmap cropped = ScreenCapture.Crop(source, new Rectangle(5, 4, 9, 7)))
            {
                source.SetPixel(5, 4, Color.Magenta);
                source.Dispose();
                Assert(cropped.GetPixel(0, 0).ToArgb() == expected, "Crop changed with or depended on source bitmap.");
            }
        });
        Run("crop rejects empty, outside, and overflow rectangles", delegate {
            using (Bitmap source = Pattern(25, 18))
            {
                Throws<ArgumentOutOfRangeException>(delegate { using (ScreenCapture.Crop(source, Rectangle.Empty)) {} });
                Throws<ArgumentOutOfRangeException>(delegate { using (ScreenCapture.Crop(source, new Rectangle(24, 17, 2, 2))) {} });
                Throws<ArgumentOutOfRangeException>(delegate { using (ScreenCapture.Crop(source, new Rectangle(-1, 0, 1, 1))) {} });
                Throws<ArgumentOutOfRangeException>(delegate { using (ScreenCapture.Crop(source, new Rectangle(int.MaxValue, 0, int.MaxValue, 1))) {} });
            }
        });
        RunAnnotationTests();
        RunMosaicFeatureTests();
        RunBoardFeatureTests();
        RunBoardDropTests();
        RunWorkspaceFeatureTests();
        RunBoardViewTests();
        RunScreenshotQueueTests();
        RunInfiniteBoardTests();
        RunBoardAsyncTests();
        RunSmoothPenTests();
        RunHistoryTests();
        RunLifecycleTests();
        Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RunAnnotationTests()
    {
        Run("empty capture tile fires once on center left click", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                canvas.Size = new Size(600, 400);
                int requests = 0;
                canvas.CaptureRequested += delegate { requests++; };
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(300, 128));
                RawMouse(canvas, "OnMouseUp", MouseButtons.Left, new Point(300, 128));
                Equal(1, requests);
                Assert(!canvas.HasImage && !canvas.CanUndo, "Capture request must not itself alter image state.");
            }
        });
        Run("empty capture tile ignores outside and right clicks", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                canvas.Size = new Size(600, 400);
                int requests = 0;
                canvas.CaptureRequested += delegate { requests++; };
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(1, 1));
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(300, 300));
                RawMouse(canvas, "OnMouseDown", MouseButtons.Right, new Point(300, 128));
                Equal(0, requests);
            }
        });
        Run("empty capture tile cannot trigger while disabled", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                canvas.Size = new Size(600, 400);
                canvas.Enabled = false;
                int requests = 0;
                canvas.CaptureRequested += delegate { requests++; };
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(300, 128));
                Equal(0, requests);
            }
        });
        Run("capture tile no longer triggers after an image loads", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(160, 100))
            {
                canvas.Size = new Size(600, 400);
                canvas.SetImage(source);
                int requests = 0;
                canvas.CaptureRequested += delegate { requests++; };
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(300, 128));
                RawMouse(canvas, "OnMouseDown", MouseButtons.Left, new Point(300, 200));
                Equal(0, requests);
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("viewport mapping scales and clamps to original image", delegate {
            RectangleF bounds = new RectangleF(30, 40, 200, 100);
            Equal(new PointF(200, 100), AnnotationCanvas.MapClientToImage(new PointF(130, 90), bounds, new Size(400, 200), true));
            Equal(new PointF(0, 200), AnnotationCanvas.MapClientToImage(new PointF(10, 180), bounds, new Size(400, 200), true));
            Equal(new PointF(-40, 280), AnnotationCanvas.MapClientToImage(new PointF(10, 180), bounds, new Size(400, 200), false));
        });
        Run("viewport fits down with preserved aspect and never enlarges", delegate {
            Equal(new RectangleF(32, 32, 200, 100),
                AnnotationCanvas.CalculateImageBounds(new Size(400, 200), new Size(264, 164), 32));
            Equal(new RectangleF(100, 75, 100, 50),
                AnnotationCanvas.CalculateImageBounds(new Size(100, 50), new Size(300, 200), 32));
        });
        Run("canvas exports untouched pixels at original resolution", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(127, 83))
            {
                canvas.Size = new Size(43, 31);
                canvas.SetImage(source);
                Assert(canvas.HasImage, "HasImage must become true.");
                Equal(source.Size, canvas.ImageSize);
                using (Bitmap exported = canvas.ExportImage())
                {
                    Equal(source.Size, exported.Size);
                    PixelsEqual(source, exported, Point.Empty);
                }
            }
        });
        Run("semi-transparent source exports exact RGBA pixels", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(127, 83))
            {
                for (int y = 0; y < source.Height; y++)
                    for (int x = 0; x < source.Width; x++)
                    {
                        Color color = source.GetPixel(x, y);
                        source.SetPixel(x, y, Color.FromArgb((x * 7 + y * 13) % 256, color.R, color.G, color.B));
                    }
                canvas.SetImage(source);
                using (Bitmap exported = canvas.ExportImage())
                    PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("canvas image replacement clears undo history", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap first = Pattern(60, 40))
            using (Bitmap second = Pattern(31, 19))
            {
                canvas.SetImage(first);
                canvas.Size = new Size(200, 180);
                canvas.Tool = DrawingTool.Pen;
                Drag(canvas, new PointF(8, 8), new PointF(40, 25));
                Assert(canvas.CanUndo, "Initial annotation was not committed.");
                canvas.SetImage(second);
                Assert(!canvas.CanUndo, "Replacement must not retain annotations from prior image.");
                using (Bitmap exported = canvas.ExportImage())
                    PixelsEqual(second, exported, Point.Empty);
            }
        });
        Run("canvas owns source and exported images independently", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            {
                Bitmap source = Pattern(80, 50);
                int expected = source.GetPixel(0, 0).ToArgb();
                canvas.SetImage(source);
                source.SetPixel(0, 0, Color.Magenta);
                source.Dispose();
                using (Bitmap exported = canvas.ExportImage())
                {
                    Assert(exported.GetPixel(0, 0).ToArgb() == expected, "Canvas depends on source pixels.");
                    exported.SetPixel(0, 0, Color.Magenta);
                }
                using (Bitmap exported = canvas.ExportImage())
                    Assert(exported.GetPixel(0, 0).ToArgb() == expected, "Export mutates canvas data.");
            }
        });
        Run("rectangle exports at original coordinates after preview fit", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Solid(400, 200, Color.White))
            {
                canvas.Size = new Size(264, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Rectangle;
                canvas.StrokeColor = Color.Red;
                canvas.StrokeWidth = 4;
                Drag(canvas, new PointF(40, 40), new PointF(200, 120));
                Assert(canvas.CanUndo, "Drawn rectangle missing from undo.");
                using (Bitmap exported = canvas.ExportImage())
                {
                    Equal(new Size(400, 200), exported.Size);
                    Equal(Color.Red.ToArgb(), exported.GetPixel(100, 40).ToArgb());
                    Equal(Color.White.ToArgb(), exported.GetPixel(100, 80).ToArgb());
                    Equal(Color.White.ToArgb(), exported.GetPixel(10, 10).ToArgb());
                }
                canvas.Undo();
                Assert(!canvas.CanUndo, "Undo did not remove rectangle.");
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("arrow, pen, and multiple undo restore previous composites", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Solid(160, 100, Color.White))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.StrokeColor = Color.Blue;
                canvas.StrokeWidth = 4;
                canvas.Tool = DrawingTool.Arrow;
                Drag(canvas, new PointF(15, 20), new PointF(130, 20));
                using (Bitmap arrow = canvas.ExportImage())
                {
                    Assert(ChangedPixels(source, arrow) > 50, "Arrow did not render.");
                    canvas.Tool = DrawingTool.Pen;
                    Drag(canvas, new PointF(20, 60), new PointF(120, 60));
                    using (Bitmap pen = canvas.ExportImage())
                        Assert(ChangedPixels(arrow, pen) > 50, "Pen did not render.");
                    canvas.Undo();
                    using (Bitmap undone = canvas.ExportImage()) PixelsEqual(arrow, undone, Point.Empty);
                    Assert(canvas.CanUndo, "First annotation should remain.");
                }
                canvas.Undo();
                Assert(!canvas.CanUndo, "Final undo history should be empty.");
                using (Bitmap undone = canvas.ExportImage()) PixelsEqual(source, undone, Point.Empty);
            }
        });
        Run("pen click draws a dot and clear restores source", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Solid(160, 100, Color.White))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Pen;
                canvas.StrokeWidth = 6;
                Drag(canvas, new PointF(40, 40), new PointF(40, 40));
                using (Bitmap exported = canvas.ExportImage())
                    Assert(ChangedPixels(source, exported) > 0, "A pen click should leave a dot.");
                canvas.ClearAnnotations();
                Assert(!canvas.CanUndo, "Clear should remove history.");
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("mosaic changes only selection, is opaque, and undoes exactly", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(160, 100))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Mosaic;
                Drag(canvas, new PointF(25, 20), new PointF(95, 70));
                using (Bitmap exported = canvas.ExportImage())
                {
                    int changed = 0;
                    for (int y = 0; y < source.Height; y++)
                        for (int x = 0; x < source.Width; x++)
                        {
                            bool inside = x >= 25 && x < 95 && y >= 20 && y < 70;
                            int before = source.GetPixel(x, y).ToArgb();
                            Color after = exported.GetPixel(x, y);
                            if (!inside) Assert(before == after.ToArgb(), "Mosaic leaked outside selection.");
                            else
                            {
                                Assert(after.A == 255, "Mosaic must obscure with opaque pixels.");
                                if (before != after.ToArgb()) changed++;
                            }
                        }
                    Assert(changed > 3000, "Mosaic did not sufficiently modify region.");
                    Equal(exported.GetPixel(40, 40).ToArgb(), exported.GetPixel(45, 45).ToArgb());
                }
                canvas.Undo();
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("switching tool cancels unfinished gesture", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(160, 100))
            {
                canvas.Size = new Size(224, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Rectangle;
                Mouse(canvas, "OnMouseDown", new PointF(20, 20));
                Mouse(canvas, "OnMouseMove", new PointF(100, 70));
                canvas.Tool = DrawingTool.Select;
                Mouse(canvas, "OnMouseUp", new PointF(100, 70));
                Assert(!canvas.CanUndo, "Cancelled gesture entered history.");
                using (Bitmap exported = canvas.ExportImage()) PixelsEqual(source, exported, Point.Empty);
            }
        });
        Run("null image resets editor to empty state", delegate {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(80, 50))
            {
                canvas.SetImage(source);
                canvas.SetImage(null);
                Assert(!canvas.HasImage && !canvas.CanUndo, "Empty editor retained image state.");
                Equal(Size.Empty, canvas.ImageSize);
                Assert(canvas.ExportImage() == null, "Empty editor should have no export.");
            }
        });
        Run("PNG file export roundtrip preserves exact original-resolution pixels", delegate {
            EncodedFileRoundtrip(ImageFormat.Png, ".png", true);
        });
        Run("JPEG file export roundtrip preserves original dimensions", delegate {
            EncodedFileRoundtrip(ImageFormat.Jpeg, ".jpg", false);
        });
    }

    private static void EncodedFileRoundtrip(ImageFormat format, string extension, bool exact)
    {
        string file = Path.Combine(Path.GetTempPath(), "QingJie-export-test-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            using (AnnotationCanvas canvas = new AnnotationCanvas())
            using (Bitmap source = Pattern(400, 200))
            {
                canvas.Size = new Size(264, 164);
                canvas.SetImage(source);
                canvas.Tool = DrawingTool.Rectangle;
                canvas.StrokeColor = Color.Red;
                Drag(canvas, new PointF(40, 40), new PointF(200, 120));
                using (Bitmap exported = canvas.ExportImage())
                {
                    exported.Save(file, format);
                    Assert(new FileInfo(file).Length > 0, "Export did not write encoded bytes.");
                    using (Bitmap decoded = new Bitmap(file))
                    {
                        Equal(source.Size, decoded.Size);
                        Assert(decoded.RawFormat.Guid == format.Guid, "Unexpected encoded image format.");
                        if (exact) PixelsEqual(exported, decoded, Point.Empty);
                    }
                }
            }
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private static void Drag(AnnotationCanvas canvas, PointF start, PointF end)
    {
        Mouse(canvas, "OnMouseDown", start);
        Mouse(canvas, "OnMouseMove", end);
        Mouse(canvas, "OnMouseUp", end);
    }

    private static void Mouse(AnnotationCanvas canvas, string method, PointF imagePoint)
    {
        RectangleF bounds = AnnotationCanvas.CalculateImageBounds(canvas.ImageSize, canvas.ClientSize, 32);
        int x = (int)Math.Round(bounds.X + imagePoint.X * bounds.Width / canvas.ImageSize.Width);
        int y = (int)Math.Round(bounds.Y + imagePoint.Y * bounds.Height / canvas.ImageSize.Height);
        RawMouse(canvas, method, MouseButtons.Left, new Point(x, y));
    }

    private static void RawMouse(AnnotationCanvas canvas, string method, MouseButtons button, Point point)
    {
        typeof(AnnotationCanvas).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(canvas, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
    }

    private static Bitmap Solid(int width, int height, Color color)
    {
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap)) g.Clear(color);
        return bitmap;
    }

    private static int ChangedPixels(Bitmap first, Bitmap second)
    {
        int result = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) result++;
        return result;
    }

    private static Bitmap Pattern(int width, int height)
    {
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, (x * 37 + y * 13) % 256, (x * 17 + y * 59) % 256, (x * 67 + y * 29) % 256));
        return bitmap;
    }

    private static void PixelsEqual(Bitmap expected, Bitmap actual, Point offset)
    {
        for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
                Assert(expected.GetPixel(x + offset.X, y + offset.Y).ToArgb() == actual.GetPixel(x, y).ToArgb(),
                    "Pixel mismatch at " + x + "," + y + ".");
    }

    private static void Equal<T>(T expected, T actual)
    {
        Assert(object.Equals(expected, actual), "Expected " + expected + "; actual " + actual + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + error.ToString());
        }
    }
}
