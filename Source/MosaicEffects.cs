using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QingJie
{
    public enum MosaicStyle { Pixelate, Blur, Dots, Stripes, Solid }

    /// <summary>
    /// Creates independent, opaque obfuscation images at the original resolution.
    /// Transparent input is composited onto white before any averaging. Pattern
    /// backgrounds use the averaged image too; no original pixels show through gaps.
    /// </summary>
    public static class MosaicEffects
    {
        public static Bitmap Apply(Bitmap source, int blockSize, MosaicStyle style, Color color)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (blockSize <= 0) throw new ArgumentOutOfRangeException("blockSize");
            if (!Enum.IsDefined(typeof(MosaicStyle), style))
                throw new ArgumentOutOfRangeException("style");

            Bitmap result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            try
            {
                if (source.HorizontalResolution > 0 && source.VerticalResolution > 0)
                    result.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                if (style == MosaicStyle.Solid)
                {
                    using (Graphics graphics = Graphics.FromImage(result))
                        graphics.Clear(Color.FromArgb(255, color.R, color.G, color.B));
                    return result;
                }

                blockSize = Math.Min(blockSize, Math.Max(source.Width, source.Height));
                int columns = (source.Width - 1) / blockSize + 1;
                int rows = (source.Height - 1) / blockSize + 1;
                int[] cells = AverageCells(source, blockSize, columns, rows);
                if (style == MosaicStyle.Blur)
                    RenderBlur(result, cells, columns, rows);
                else
                    RenderPattern(result, cells, columns, blockSize, style);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static int[] AverageCells(Bitmap source, int blockSize, int columns, int rows)
        {
            Bitmap converted = null;
            Bitmap readable = source;
            try
            {
                if (source.PixelFormat != PixelFormat.Format32bppArgb &&
                    source.PixelFormat != PixelFormat.Format32bppRgb)
                {
                    converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImageUnscaled(source, 0, 0);
                    }
                    readable = converted;
                }
                int count = checked(columns * rows);
                long[] reds = new long[count];
                long[] greens = new long[count];
                long[] blues = new long[count];
                int[] row = new int[source.Width];
                bool hasAlpha = readable.PixelFormat == PixelFormat.Format32bppArgb;
                BitmapData data = readable.LockBits(new Rectangle(Point.Empty, readable.Size),
                    ImageLockMode.ReadOnly, readable.PixelFormat);
                try
                {
                    for (int y = 0; y < source.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        int firstCell = (y / blockSize) * columns;
                        for (int column = 0; column < columns; column++)
                        {
                            int left = column * blockSize;
                            int right = Math.Min(source.Width, left + blockSize);
                            long red = 0, green = 0, blue = 0;
                            for (int x = left; x < right; x++)
                            {
                                int pixel = row[x];
                                int alpha = hasAlpha ? (int)((uint)pixel >> 24) : 255;
                                if (alpha == 255)
                                {
                                    red += (pixel >> 16) & 255;
                                    green += (pixel >> 8) & 255;
                                    blue += pixel & 255;
                                }
                                else
                                {
                                    int white = 255 * (255 - alpha);
                                    red += (((pixel >> 16) & 255) * alpha + white) / 255;
                                    green += (((pixel >> 8) & 255) * alpha + white) / 255;
                                    blue += ((pixel & 255) * alpha + white) / 255;
                                }
                            }
                            int index = firstCell + column;
                            reds[index] += red;
                            greens[index] += green;
                            blues[index] += blue;
                        }
                    }
                }
                finally { readable.UnlockBits(data); }
                int[] averages = new int[count];
                for (int y = 0; y < rows; y++)
                {
                    int height = Math.Min(blockSize, source.Height - y * blockSize);
                    for (int x = 0; x < columns; x++)
                    {
                        int width = Math.Min(blockSize, source.Width - x * blockSize);
                        int samples = width * height;
                        int index = y * columns + x;
                        averages[index] = Pack((int)(reds[index] / samples),
                            (int)(greens[index] / samples), (int)(blues[index] / samples));
                    }
                }
                return averages;
            }
            finally { if (converted != null) converted.Dispose(); }
        }

        private static void RenderPattern(Bitmap result, int[] cells, int columns,
            int blockSize, MosaicStyle style)
        {
            int[] row = new int[result.Width];
            int period = Math.Max(6, blockSize / 2);
            float radius = blockSize * 0.36f;
            float radiusSquared = radius * radius;
            BitmapData data = result.LockBits(new Rectangle(Point.Empty, result.Size),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < result.Height; y++)
                {
                    int cellRow = y / blockSize;
                    float dy = y + 0.5f - (cellRow * blockSize + blockSize / 2f);
                    for (int column = 0; column < columns; column++)
                    {
                        int average = cells[cellRow * columns + column];
                        int left = column * blockSize;
                        int right = Math.Min(result.Width, left + blockSize);
                        if (style == MosaicStyle.Pixelate)
                        {
                            for (int x = left; x < right; x++) row[x] = average;
                        }
                        else if (style == MosaicStyle.Dots)
                        {
                            int background = Tint(average, 255, 255, 255, 30);
                            int dot = Tint(average, 20, 33, 53, 16);
                            float centerX = left + blockSize / 2f;
                            for (int x = left; x < right; x++)
                            {
                                float dx = x + 0.5f - centerX;
                                row[x] = dx * dx + dy * dy <= radiusSquared ? dot : background;
                            }
                        }
                        else
                        {
                            int light = Tint(average, 255, 255, 255, 22);
                            int dark = Tint(average, 20, 33, 53, 28);
                            for (int x = left; x < right; x++)
                                row[x] = ((x + y) % period) < period / 2 ? dark : light;
                        }
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { result.UnlockBits(data); }
        }

        private static void RenderBlur(Bitmap result, int[] cells, int columns, int rows)
        {
            // Work on the averaged thumbnail, never on the original text/details.
            // Two inexpensive 3x3 passes soften the block boundaries before upscale.
            for (int pass = 0; pass < 2; pass++)
            {
                int[] smoothed = new int[cells.Length];
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < columns; x++)
                    {
                        int red = 0, green = 0, blue = 0, samples = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = Math.Max(0, Math.Min(rows - 1, y + dy));
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = Math.Max(0, Math.Min(columns - 1, x + dx));
                                int pixel = cells[yy * columns + xx];
                                red += (pixel >> 16) & 255;
                                green += (pixel >> 8) & 255;
                                blue += pixel & 255;
                                samples++;
                            }
                        }
                        smoothed[y * columns + x] = Pack(red / samples, green / samples,
                            blue / samples);
                    }
                }
                cells = smoothed;
            }
            using (Bitmap thumbnail = new Bitmap(columns, rows, PixelFormat.Format32bppArgb))
            {
                BitmapData data = thumbnail.LockBits(new Rectangle(Point.Empty, thumbnail.Size),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < rows; y++)
                        Marshal.Copy(cells, y * columns, IntPtr.Add(data.Scan0, y * data.Stride),
                            columns);
                }
                finally { thumbnail.UnlockBits(data); }
                using (Graphics graphics = Graphics.FromImage(result))
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    // TileFlipXY keeps the outside edge fully opaque during filtering.
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(thumbnail, new Rectangle(Point.Empty, result.Size),
                        0, 0, columns, rows, GraphicsUnit.Pixel, attributes);
                }
            }
        }

        private static int Tint(int pixel, int red, int green, int blue, int percent)
        {
            int remaining = 100 - percent;
            return Pack((((pixel >> 16) & 255) * remaining + red * percent) / 100,
                (((pixel >> 8) & 255) * remaining + green * percent) / 100,
                ((pixel & 255) * remaining + blue * percent) / 100);
        }

        private static int Pack(int red, int green, int blue)
        {
            return unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;
        }
    }
}
