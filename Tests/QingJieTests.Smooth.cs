using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunSmoothPenTests()
    {
        Run("both smooth pens preserve round dots short lines and mouse-up-only endpoints", delegate {
            PointF[][] cases = {
                new PointF[] { new PointF(100, 120) },
                new PointF[] { new PointF(100, 120), new PointF(104, 120) },
                new PointF[] { new PointF(80, 180), new PointF(250, 80) }
            };
            for (int boardMode = 0; boardMode < 2; boardMode++)
            foreach (PointF[] points in cases)
            using (Control control = NewSmoothCanvas(boardMode == 1))
            {
                SmoothMouse(control, "OnMouseDown", points[0]);
                SmoothMouse(control, "OnMouseUp", points[points.Length - 1]);
                AssertSmoothExport(control, points, "dot/short/mouse-up endpoint");
                UndoSmooth(control);
                AssertSmoothEmpty(control);
            }
        });
        Run("both smooth pens round a sharp corner with independent curve export at negative world coordinates", delegate {
            PointF[] points = { new PointF(80, 180), new PointF(180, 180), new PointF(180, 80) };
            for (int boardMode = 0; boardMode < 2; boardMode++)
            using (Control control = NewSmoothCanvas(boardMode == 1))
            {
                SmoothMouse(control, "OnMouseDown", points[0]);
                SmoothMouse(control, "OnMouseMove", points[1]);
                SmoothMouse(control, "OnMouseUp", points[2]);
                AssertSmoothExport(control, points, "rounded corner independent geometry");
                using (Bitmap exported = ExportSmooth(control))
                {
                    Equal(Color.White.ToArgb(), SmoothPixel(control, exported, points[1]).ToArgb());
                    Color curved = SmoothPixel(control, exported, new PointF(168, 168));
                    Assert(curved.R > 200 && curved.G < 80 && curved.B < 80,
                        "The pen did not follow the rounded interior of the original corner.");
                }
                UndoSmooth(control);
                AssertSmoothEmpty(control);
            }
        });
        Run("smooth pen dirty redraw clears provisional tails through reversals commit undo and cancel", delegate {
            PointF[] points = { new PointF(80, 220), new PointF(300, 220), new PointF(300, 80),
                new PointF(180, 80), new PointF(180, 260), new PointF(360, 260) };
            for (int boardMode = 0; boardMode < 2; boardMode++)
            using (Control control = NewSmoothCanvas(boardMode == 1))
            using (SmoothDirtySurface driver = new SmoothDirtySurface(control))
            {
                driver.Full();
                SmoothMouse(control, "OnMouseDown", points[0]);
                driver.FlushAndCompare("initial round dot");
                for (int index = 1; index < points.Length - 1; index++)
                {
                    SmoothMouse(control, "OnMouseMove", points[index]);
                    driver.FlushAndCompare("replaced tail " + index);
                }
                SmoothMouse(control, "OnMouseUp", points[points.Length - 1]);
                driver.FlushAndCompare("mouseup appended endpoint");
                AssertSmoothExport(control, points, "reversed smooth stroke");
                UndoSmooth(control);
                driver.FlushAndCompare("smooth undo");
                AssertSmoothEmpty(control);
                SmoothMouse(control, "OnMouseDown", points[0]);
                SmoothMouse(control, "OnMouseMove", points[1]);
                driver.FlushAndCompare("cancel candidate");
                if (control is AnnotationCanvas) ((AnnotationCanvas)control).Tool = DrawingTool.Select;
                else ((BoardCanvas)control).Tool = DrawingTool.Select;
                driver.FlushAndCompare("cancelled tail");
                AssertSmoothEmpty(control);
            }
        });
    }

    private static Control NewSmoothCanvas(bool boardMode)
    {
        if (boardMode)
        {
            BoardCanvas board = NewBoard();
            board.Tool = DrawingTool.Pen;
            board.StrokeColor = Color.Red;
            board.StrokeWidth = 12;
            IntPtr handle = board.Handle;
            return board;
        }
        AnnotationCanvas canvas = new AnnotationCanvas();
        canvas.Size = new Size(704, 544);
        using (Bitmap image = Solid(640, 480, Color.White)) canvas.SetImage(image);
        canvas.Tool = DrawingTool.Pen;
        canvas.StrokeColor = Color.Red;
        canvas.StrokeWidth = 12;
        IntPtr editorHandle = canvas.Handle;
        return canvas;
    }
    private static PointF SmoothWorld(PointF point) { return new PointF(point.X - 320, point.Y - 240); }
    private static void SmoothMouse(Control control, string method, PointF point)
    {
        if (control is AnnotationCanvas) Mouse((AnnotationCanvas)control, method, point);
        else BoardMouse((BoardCanvas)control, method, SmoothWorld(point));
    }
    private static Bitmap ExportSmooth(Control control)
    {
        return control is AnnotationCanvas ? ((AnnotationCanvas)control).ExportImage() : ((BoardCanvas)control).ExportImage();
    }
    private static Color SmoothPixel(Control control, Bitmap bitmap, PointF point)
    {
        return control is AnnotationCanvas ? bitmap.GetPixel((int)point.X, (int)point.Y) :
            PixelAtWorld((BoardCanvas)control, bitmap, SmoothWorld(point));
    }
    private static void UndoSmooth(Control control)
    {
        if (control is AnnotationCanvas) ((AnnotationCanvas)control).Undo();
        else ((BoardCanvas)control).Undo();
    }
    private static void AssertSmoothEmpty(Control control)
    {
        if (control is BoardCanvas)
            Assert(!((BoardCanvas)control).HasContent && !((BoardCanvas)control).CanUndo, "Undo/cancel retained a board stroke.");
        else
            using (Bitmap exported = ((AnnotationCanvas)control).ExportImage())
            using (Bitmap white = Solid(640, 480, Color.White))
                FastPixelsEqual(white, exported, "editor smooth undo/cancel");
    }
    private static void AssertSmoothExport(Control control, PointF[] sourcePoints, string description)
    {
        PointF[] points = new PointF[sourcePoints.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = control is BoardCanvas ? SmoothWorld(sourcePoints[i]) : sourcePoints[i];
        using (Bitmap exported = ExportSmooth(control))
        using (Bitmap expected = Solid(exported.Width, exported.Height, Color.White))
        using (Graphics graphics = Graphics.FromImage(expected))
        using (Pen pen = new Pen(Color.Red, 12))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            pen.StartCap = pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (control is BoardCanvas)
            {
                RectangleF crop = ((BoardCanvas)control).ExportBounds;
                graphics.TranslateTransform(-crop.X, -crop.Y);
            }
            DrawIndependentSmoothCurve(graphics, pen, points);
            FastPixelsEqual(expected, exported, description + " " + control.GetType().Name);
        }
    }
    private static void DrawIndependentSmoothCurve(Graphics graphics, Pen pen, IList<PointF> points)
    {
        if (points.Count == 1)
        {
            using (Brush fill = new SolidBrush(pen.Color))
                graphics.FillEllipse(fill, points[0].X - pen.Width / 2, points[0].Y - pen.Width / 2, pen.Width, pen.Width);
            return;
        }
        // Independent quadratic-to-cubic reference, never calls SmoothStroke.
        using (GraphicsPath path = new GraphicsPath())
        {
            PointF start = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                PointF control = points[i - 1];
                PointF end = new PointF(control.X + (points[i].X - control.X) * .5f,
                    control.Y + (points[i].Y - control.Y) * .5f);
                path.AddBezier(start,
                    new PointF(start.X + (control.X - start.X) * (2f / 3f), start.Y + (control.Y - start.Y) * (2f / 3f)),
                    new PointF(end.X + (control.X - end.X) * (2f / 3f), end.Y + (control.Y - end.Y) * (2f / 3f)), end);
                start = end;
            }
            path.AddLine(start, points[points.Count - 1]);
            graphics.DrawPath(pen, path);
        }
    }
    private sealed class SmoothDirtySurface : IDisposable
    {
        private readonly Control control;
        private readonly Bitmap surface;
        private readonly List<Rectangle> invalid = new List<Rectangle>();
        public SmoothDirtySurface(Control target)
        {
            control = target;
            surface = new Bitmap(target.Width, target.Height, PixelFormat.Format32bppPArgb);
            surface.SetResolution(96, 96);
            target.Invalidated += delegate(object sender, InvalidateEventArgs args) { invalid.Add(args.InvalidRect); };
        }
        public void Full()
        {
            using (Region region = new Region(control.ClientRectangle)) Paint(surface, region, control.ClientRectangle);
            invalid.Clear();
        }
        public void FlushAndCompare(string description)
        {
            using (Region region = new Region())
            using (Graphics measure = Graphics.FromImage(surface))
            {
                region.MakeEmpty();
                foreach (Rectangle rectangle in invalid) region.Union(rectangle);
                Rectangle bounds = Rectangle.Ceiling(region.GetBounds(measure));
                bounds.Intersect(control.ClientRectangle);
                if (bounds.Width > 0 && bounds.Height > 0) Paint(surface, region, bounds);
            }
            invalid.Clear();
            using (Bitmap whole = new Bitmap(surface.Width, surface.Height, PixelFormat.Format32bppPArgb))
            using (Region full = new Region(control.ClientRectangle))
            {
                whole.SetResolution(96, 96);
                Paint(whole, full, control.ClientRectangle);
                FastPixelsEqual(whole, surface, description + " " + control.GetType().Name);
            }
        }
        private void Paint(Bitmap bitmap, Region region, Rectangle bounds)
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SetClip(region, CombineMode.Replace);
                control.GetType().GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(control, new object[] { new PaintEventArgs(graphics, bounds) });
            }
        }
        public void Dispose() { surface.Dispose(); }
    }
}
