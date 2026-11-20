using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;

namespace QingJie
{
    // A worker owns this immutable-by-convention snapshot from submission until
    // disposal. The UI supplies retained image references and immutable annotations;
    // no rendering code here reads a Control or a live camera.
    internal sealed class BoardViewSnapshot : IDisposable
    {
        public Size ViewSize;
        public float DpiScale;
        public PointF Origin;
        public float Scale;
        public Color Background;
        public readonly List<BoardSnapshotImage> Images = new List<BoardSnapshotImage>();
        public readonly List<BoardSnapshotAnnotation> Annotations = new List<BoardSnapshotAnnotation>();
        private int disposed;

        public Bitmap Render(CancellationToken cancellation)
        {
            if (Interlocked.CompareExchange(ref disposed, 0, 0) != 0)
                throw new ObjectDisposedException("BoardViewSnapshot");
            cancellation.ThrowIfCancellationRequested();
            if (ViewSize.Width < 1 || ViewSize.Height < 1 || !PositiveFinite(Scale) ||
                !PositiveFinite(DpiScale))
                throw new InvalidOperationException("Invalid board snapshot dimensions.");
            Bitmap result = new Bitmap(ViewSize.Width, ViewSize.Height, PixelFormat.Format32bppPArgb);
            try
            {
                result.SetResolution(96 * DpiScale, 96 * DpiScale);
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    Rectangle client = new Rectangle(Point.Empty, ViewSize);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    using (Brush fill = new SolidBrush(Background))
                        graphics.FillRectangle(fill, client);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    DrawGrid(graphics, cancellation);
                    RectangleF visible = RectangleF.FromLTRB(-Origin.X / Scale, -Origin.Y / Scale,
                        (ViewSize.Width - Origin.X) / Scale, (ViewSize.Height - Origin.Y) / Scale);
                    ConfigureQuality(graphics);
                    foreach (BoardSnapshotImage image in Images)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        RectangleF region = RectangleF.Intersect(visible, image.Bounds);
                        if (region.Width <= 0 || region.Height <= 0) continue;
                        DrawImage(graphics, image, region, cancellation);
                    }
                    graphics.SetClip(client, CombineMode.Intersect);
                    graphics.TranslateTransform(Origin.X, Origin.Y);
                    graphics.ScaleTransform(Scale, Scale);
                    foreach (BoardSnapshotAnnotation annotation in Annotations)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (annotation.Draw != null && annotation.Bounds.IntersectsWith(visible))
                            annotation.Draw(graphics);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void DrawImage(Graphics graphics, BoardSnapshotImage image,
            RectangleF visible, CancellationToken cancellation)
        {
            object gate = image.SyncRoot ?? image.Pixels;
            bool entered = false;
            try
            {
                // Export can briefly own this source. A cancelled worker must
                // leave the wait without delaying the latest requested view.
                while (!entered)
                {
                    cancellation.ThrowIfCancellationRequested();
                    entered = Monitor.TryEnter(gate, 10);
                }
                cancellation.ThrowIfCancellationRequested();
                Bitmap pixels = image.Pixels;
                RectangleF source = new RectangleF(
                    (visible.Left - image.Bounds.Left) * pixels.Width / image.Bounds.Width,
                    (visible.Top - image.Bounds.Top) * pixels.Height / image.Bounds.Height,
                    visible.Width * pixels.Width / image.Bounds.Width,
                    visible.Height * pixels.Height / image.Bounds.Height);
                PointF[] destination = {
                    ToClient(new PointF(visible.Left, visible.Top)),
                    ToClient(new PointF(visible.Right, visible.Top)),
                    ToClient(new PointF(visible.Left, visible.Bottom))
                };
                CompositingMode previous = graphics.CompositingMode;
                try
                {
                    if (image.IsOpaque) graphics.CompositingMode = CompositingMode.SourceCopy;
                    using (ImageAttributes attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(pixels, destination, source, GraphicsUnit.Pixel, attributes);
                    }
                }
                finally { graphics.CompositingMode = previous; }
            }
            finally { if (entered) Monitor.Exit(gate); }
        }

        private PointF ToClient(PointF point)
        {
            return new PointF(Origin.X + point.X * Scale, Origin.Y + point.Y * Scale);
        }

        private void DrawGrid(Graphics graphics, CancellationToken cancellation)
        {
            double worldStep = 64;
            while (worldStep * Scale < 22 * DpiScale) worldStep *= 2;
            while (worldStep * Scale > 48 * DpiScale) worldStep /= 2;
            double step = worldStep * Scale;
            double firstX = Math.Floor(-Origin.X / step) * step + Origin.X;
            double firstY = Math.Floor(-Origin.Y / step) * step + Origin.Y;
            int dot = Math.Max(1, (int)Math.Round(DpiScale));
            using (Brush brush = new SolidBrush(Color.FromArgb(211, 220, 234)))
                for (double y = firstY; y <= ViewSize.Height; y += step)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (double x = firstX; x <= ViewSize.Width; x += step)
                        graphics.FillRectangle(brush, (float)Math.Floor(x + .5),
                            (float)Math.Floor(y + .5), dot, dot);
                }
        }

        private static bool PositiveFinite(float value)
        {
            return value > 0 && !Single.IsInfinity(value) && !Single.IsNaN(value);
        }

        private static void ConfigureQuality(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (BoardSnapshotImage image in Images) image.Dispose();
            Images.Clear();
            Annotations.Clear();
        }
    }

    internal sealed class BoardSnapshotImage : IDisposable
    {
        public Bitmap Pixels;
        public RectangleF Bounds;
        public bool IsOpaque;
        public object SyncRoot;
        public Action Release;

        public void Dispose()
        {
            Action release = Interlocked.Exchange(ref Release, null);
            if (release != null) release();
        }
    }

    internal sealed class BoardSnapshotAnnotation
    {
        public RectangleF Bounds;
        public Action<Graphics> Draw;
    }
}
