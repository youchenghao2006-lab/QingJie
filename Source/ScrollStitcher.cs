using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QingJie
{
    public enum ScrollFrameResult { Appended, Unchanged, NoMatch, LimitReached }

    /// <summary>
    /// Conservative downward-only stitching of equally sized frozen viewports.
    /// Calls must be serialized. Inputs remain caller-owned; accepted pixels and
    /// exported images are independent. A rejected frame never changes the anchor.
    /// </summary>
    public sealed class ScrollStitcher : IDisposable
    {
        private const int DefaultMaximumHeight = 32767;
        private const long DefaultMaximumPixels = 80000000;
        private readonly int width, viewportHeight, maximumHeight;
        private readonly long maximumPixels;
        private readonly List<Bitmap> parts = new List<Bitmap>();
        private Bitmap previous, footer;
        private Samples previousSamples;
        private int height, topMargin = -1, bottomMargin = -1;
        private bool disposed;

        public ScrollStitcher(Bitmap first)
            : this(first, DefaultMaximumHeight, DefaultMaximumPixels) { }

        // Small limits let regression tests cover allocation guards without
        // allocating an eighty-million-pixel image.
        internal ScrollStitcher(Bitmap first, int maximumHeight, long maximumPixels)
        {
            if (first == null) throw new ArgumentNullException("first");
            if (maximumHeight <= 0 || maximumHeight > DefaultMaximumHeight)
                throw new ArgumentOutOfRangeException("maximumHeight");
            if (maximumPixels <= 0 || maximumPixels > DefaultMaximumPixels)
                throw new ArgumentOutOfRangeException("maximumPixels");
            width = first.Width;
            viewportHeight = height = first.Height;
            this.maximumHeight = maximumHeight;
            this.maximumPixels = maximumPixels;
            if (width > 32767 || height > maximumHeight || (long)width * height > maximumPixels)
                throw new ArgumentException("截图超过长图尺寸上限，请缩小截图区域。", "first");
            try
            {
                previous = Copy(first, new Rectangle(Point.Empty, first.Size));
                previousSamples = Samples.Read(previous);
                parts.Add(Copy(previous, new Rectangle(Point.Empty, previous.Size)));
            }
            catch { Dispose(); throw; }
        }

        public int Height { get { CheckAlive(); return height; } }

        /// <summary>
        /// Independent, aspect-preserving preview. Renders retained strips directly
        /// into a bounded bitmap; never allocates the full-height export first.
        /// </summary>
        public Bitmap CreatePreview(int maxWidth, int maxHeight)
        {
            CheckAlive();
            if (maxWidth <= 0) throw new ArgumentOutOfRangeException("maxWidth");
            if (maxHeight <= 0) throw new ArgumentOutOfRangeException("maxHeight");
            double scale = Math.Min(1.0, Math.Min(Math.Min(maxWidth, 2048) / (double)width,
                Math.Min(maxHeight, 2048) / (double)height));
            int previewWidth = Math.Max(1, (int)Math.Floor(width * scale));
            int previewHeight = Math.Max(1, (int)Math.Floor(height * scale));
            Bitmap result = new Bitmap(previewWidth, previewHeight, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                using (ImageAttributes attributes = new ImageAttributes())
                {
                    graphics.Clear(Color.White);
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    int sourceY = 0;
                    foreach (Bitmap part in parts)
                        DrawPreviewPart(graphics, part, ref sourceY, previewWidth, previewHeight, attributes);
                    if (footer != null)
                        DrawPreviewPart(graphics, footer, ref sourceY, previewWidth, previewHeight, attributes);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private void DrawPreviewPart(Graphics graphics, Bitmap part, ref int sourceY,
            int previewWidth, int previewHeight, ImageAttributes attributes)
        {
            int top = (int)Math.Round(sourceY * (double)previewHeight / height);
            sourceY += part.Height;
            int bottom = (int)Math.Round(sourceY * (double)previewHeight / height);
            if (bottom > top)
                graphics.DrawImage(part, new Rectangle(0, top, previewWidth, bottom - top),
                    0, 0, part.Width, part.Height, GraphicsUnit.Pixel, attributes);
        }

        public ScrollFrameResult AddFrame(Bitmap viewport)
        {
            CheckAlive();
            if (viewport == null) throw new ArgumentNullException("viewport");
            if (viewport.Width != width || viewport.Height != viewportHeight)
                return ScrollFrameResult.NoMatch;
            Samples currentSamples = Samples.Read(viewport);
            if (previousSamples.SameAs(currentSamples) && SamePixels(previous, viewport))
                return ScrollFrameResult.Unchanged;

            int top = topMargin >= 0 ? topMargin : StaticMargin(previousSamples, currentSamples, true);
            int bottom = bottomMargin >= 0 ? bottomMargin : StaticMargin(previousSamples, currentSamples, false);
            int body = viewportHeight - top - bottom;
            if (currentSamples.HasVerticalDetail(top, body) &&
                ComparePixels(previous, viewport, top, bottom, 0, true, true))
                return ScrollFrameResult.Unchanged;
            int minimumOverlap = Math.Max(16, Math.Min(128, body / 5));
            if (body <= minimumOverlap + 1) return ScrollFrameResult.NoMatch;

            List<Candidate> candidates = new List<Candidate>();
            for (int shift = 1; shift <= body - minimumOverlap; shift++)
            {
                double error = SampleError(previousSamples, currentSamples, top, bottom, shift);
                if (error <= 3.0) candidates.Add(new Candidate(shift, error));
            }
            if (candidates.Count == 0) return ScrollFrameResult.NoMatch;
            candidates.Sort(delegate(Candidate left, Candidate right) { return left.Error.CompareTo(right.Error); });

            // Repeated lines/tiles can produce several equally plausible offsets.
            // Never choose the shortest or longest one just to make progress.
            double candidateThreshold = Math.Min(3.0, candidates[0].Error + .75);
            int plausible = 0;
            while (plausible < candidates.Count && candidates[plausible].Error <= candidateThreshold) plausible++;
            if (plausible > 12) return ScrollFrameResult.NoMatch;
            int acceptedShift = 0;
            for (int i = 0; i < plausible; i++)
            {
                int shift = candidates[i].Shift;
                if (!currentSamples.HasVerticalDetail(top, viewportHeight - top - bottom - shift)) continue;
                if (!VerifyOverlap(previous, viewport, top, bottom, shift)) continue;
                if (acceptedShift != 0) return ScrollFrameResult.NoMatch;
                acceptedShift = shift;
            }
            if (acceptedShift == 0) return ScrollFrameResult.NoMatch;
            long nextHeight = (long)height + acceptedShift;
            if (nextHeight > maximumHeight || nextHeight * width > maximumPixels)
                return ScrollFrameResult.LimitReached;

            Bitmap nextPrevious = null, appended = null, nextFooter = null, trimmedFirst = null;
            try
            {
                nextPrevious = Copy(viewport, new Rectangle(Point.Empty, viewport.Size));
                appended = Copy(viewport, new Rectangle(0, viewportHeight - bottom - acceptedShift,
                    width, acceptedShift));
                if (bottom > 0)
                {
                    nextFooter = Copy(viewport, new Rectangle(0, viewportHeight - bottom, width, bottom));
                    if (bottomMargin < 0)
                        trimmedFirst = Copy(parts[0], new Rectangle(0, 0, width, viewportHeight - bottom));
                }
                // All allocations have succeeded; commit the frame atomically.
                if (trimmedFirst != null)
                {
                    Bitmap oldFirst = parts[0]; parts[0] = trimmedFirst; trimmedFirst = null;
                    oldFirst.Dispose();
                }
                parts.Add(appended); appended = null;
                previous.Dispose(); previous = nextPrevious; nextPrevious = null;
                if (footer != null) footer.Dispose();
                footer = nextFooter; nextFooter = null;
                previousSamples = currentSamples;
                topMargin = top; bottomMargin = bottom;
                height = (int)nextHeight;
                return ScrollFrameResult.Appended;
            }
            finally
            {
                if (nextPrevious != null) nextPrevious.Dispose();
                if (appended != null) appended.Dispose();
                if (nextFooter != null) nextFooter.Dispose();
                if (trimmedFirst != null) trimmedFirst.Dispose();
            }
        }

        /// <summary>Native-resolution pixels, with any recognized fixed footer once at the end.</summary>
        public Bitmap ExportImage()
        {
            CheckAlive();
            Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData destination = result.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[checked(width * 4)];
                    int y = 0;
                    foreach (Bitmap part in parts) CopyRows(part, destination, ref y, row);
                    if (footer != null) CopyRows(footer, destination, ref y, row);
                }
                finally { result.UnlockBits(destination); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void CopyRows(Bitmap source, BitmapData destination, ref int destinationY, byte[] row)
        {
            BitmapData data = source.LockBits(new Rectangle(Point.Empty, source.Size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < source.Height; y++, destinationY++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, IntPtr.Add(destination.Scan0, destinationY * destination.Stride), row.Length);
                }
            }
            finally { source.UnlockBits(data); }
        }

        private static Bitmap Copy(Bitmap image, Rectangle bounds)
        { return image.Clone(bounds, PixelFormat.Format32bppArgb); }

        private static int StaticMargin(Samples previous, Samples current, bool top)
        {
            int length = 0, maximum = previous.Height / 3;
            while (length < maximum)
            {
                int row = top ? length : previous.Height - length - 1;
                if (!previous.SameRow(current, row, row)) break;
                length++;
            }
            int start = top ? 0 : previous.Height - length;
            // Uniform blank edges do not establish a fixed header/footer.
            return length >= 2 && previous.HasDetail(start, length) ? length : 0;
        }

        private static double SampleError(Samples previous, Samples current, int top, int bottom, int shift)
        {
            int overlap = current.Height - top - bottom - shift;
            int first = current.DetailRows.BinarySearch(top);
            if (first < 0) first = ~first;
            int end = current.DetailRows.BinarySearch(top + overlap);
            if (end < 0) end = ~end;
            int rows = Math.Min(32, end - first);
            if (rows == 0) return 4.0;
            long sum = 0;
            int count = 0;
            for (int i = 0; i < rows; i++)
            {
                int y = current.DetailRows[first + (rows == 1 ? 0 : i * (end - first - 1) / (rows - 1))];
                int a = (y + shift) * previous.RowBytes, b = y * current.RowBytes;
                bool flatEdge = !previous.HorizontalDetail[y + shift] && !current.HorizontalDetail[y];
                for (int x = 0; x < current.RowBytes; x += 3)
                {
                    bool ink = flatEdge;
                    for (int c = 0; c < 3 && !ink; c++)
                        ink = Math.Abs(previous.Pixels[a + x + c] - previous.Pixels[a + c]) > 12 ||
                            Math.Abs(current.Pixels[b + x + c] - current.Pixels[b + c]) > 12 ||
                            Math.Abs(previous.Pixels[a + c] - current.Pixels[b + c]) > 12;
                    if (!ink) continue;
                    for (int c = 0; c < 3; c++)
                        sum += Math.Abs(previous.Pixels[a + x + c] - current.Pixels[b + x + c]);
                    count += 3;
                }
                if (sum > 3L * rows * current.RowBytes) return 4.0;
            }
            return count == 0 ? 4.0 : (double)sum / count;
        }

        private static bool SamePixels(Bitmap previous, Bitmap current)
        { return ComparePixels(previous, current, 0, 0, 0, true); }

        private static bool VerifyOverlap(Bitmap previous, Bitmap current, int top, int bottom, int shift)
        { return ComparePixels(previous, current, top, bottom, shift, false); }

        private static bool ComparePixels(Bitmap previous, Bitmap current, int top, int bottom, int shift,
            bool exact, bool matchInterior = false)
        {
            Rectangle bounds = new Rectangle(Point.Empty, previous.Size);
            BitmapData oldData = previous.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                BitmapData newData = current.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int bytes = previous.Width * 4;
                    byte[] oldRow = new byte[bytes], newRow = new byte[bytes];
                    int edge = exact && !matchInterior ? 0 : MatchingEdge(previous.Width);
                    long count = (long)(previous.Width - edge * 2) * (previous.Height - top - bottom - shift);
                    long bad = 0, total = 0;
                    long inkCount = 0, inkBad = 0, inkTotal = 0;
                    for (int y = top; y < previous.Height - bottom - shift; y++)
                    {
                        Marshal.Copy(IntPtr.Add(oldData.Scan0, (y + shift) * oldData.Stride), oldRow, 0, bytes);
                        Marshal.Copy(IntPtr.Add(newData.Scan0, y * newData.Stride), newRow, 0, bytes);
                        int background = edge * 4;
                        for (int x = edge * 4; x < bytes - edge * 4; x += 4)
                        {
                            int blue = Math.Abs(oldRow[x] - newRow[x]);
                            int green = Math.Abs(oldRow[x + 1] - newRow[x + 1]);
                            int red = Math.Abs(oldRow[x + 2] - newRow[x + 2]);
                            int alpha = Math.Abs(oldRow[x + 3] - newRow[x + 3]);
                            if (exact)
                            {
                                if (blue + green + red + alpha != 0) return false;
                            }
                            else
                            {
                                total += blue + green + red + alpha;
                                if (blue > 12 || green > 12 || red > 12 || alpha > 12) bad++;
                                bool ink = false;
                                for (int c = 0; c < 3 && !ink; c++)
                                    ink = Math.Abs(oldRow[x + c] - oldRow[background + c]) > 12 ||
                                        Math.Abs(newRow[x + c] - newRow[background + c]) > 12 ||
                                        Math.Abs(oldRow[background + c] - newRow[background + c]) > 12;
                                if (ink)
                                {
                                    inkCount++; inkTotal += blue + green + red + alpha;
                                    if (blue > 12 || green > 12 || red > 12 || alpha > 12) inkBad++;
                                }
                            }
                        }
                        // Small local animations may differ, but cannot dominate
                        // the seam or justify matching unrelated/shifted pages.
                        if (!exact && (bad > count * .015 || total > count * 3 * 1.5)) return false;
                    }
                    // White margins must not make two different text lines look
                    // identical merely because their ink occupies few pixels.
                    return exact || inkCount == 0 || (inkBad <= inkCount * .01 && inkTotal <= inkCount * 3);
                }
                finally { current.UnlockBits(newData); }
            }
            finally { previous.UnlockBits(oldData); }
        }

        private static int MatchingEdge(int imageWidth)
        { return Math.Min(24, imageWidth / 32); }

        private sealed class Candidate
        {
            internal readonly int Shift;
            internal readonly double Error;
            internal Candidate(int shift, double error) { Shift = shift; Error = error; }
        }

        private sealed class Samples
        {
            internal readonly int Height, RowBytes;
            internal readonly byte[] Pixels;
            internal readonly List<int> DetailRows = new List<int>();
            internal readonly bool[] HorizontalDetail;
            private Samples(int height, int rowBytes)
            {
                Height = height; RowBytes = rowBytes; Pixels = new byte[checked(height * rowBytes)];
                HorizontalDetail = new bool[height];
            }
            internal static Samples Read(Bitmap image)
            {
                int edge = MatchingEdge(image.Width);
                int contentWidth = image.Width - edge * 2;
                int columns = Math.Min(96, contentWidth);
                Samples result = new Samples(image.Height, columns * 3);
                BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[checked(image.Width * 4)];
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < columns; x++)
                        {
                            int source = (edge + (columns == 1 ? 0 : x * (contentWidth - 1) / (columns - 1))) * 4;
                            int target = y * result.RowBytes + x * 3;
                            result.Pixels[target] = row[source];
                            result.Pixels[target + 1] = row[source + 1];
                            result.Pixels[target + 2] = row[source + 2];
                        }
                        int begin = y * result.RowBytes;
                        bool vertical = false;
                        for (int x = 0; x < result.RowBytes; x++)
                        {
                            if (Math.Abs(result.Pixels[begin + x] - result.Pixels[begin + x % 3]) > 12)
                                result.HorizontalDetail[y] = true;
                            if (y > 0 && Math.Abs(result.Pixels[begin + x] - result.Pixels[begin - result.RowBytes + x]) > 12)
                                vertical = true;
                        }
                        if (result.HorizontalDetail[y] || vertical) result.DetailRows.Add(y);
                    }
                }
                finally { image.UnlockBits(data); }
                return result;
            }
            internal bool SameAs(Samples other)
            {
                for (int i = 0; i < Pixels.Length; i++) if (Pixels[i] != other.Pixels[i]) return false;
                return true;
            }
            internal bool SameRow(Samples other, int leftRow, int rightRow)
            {
                int left = leftRow * RowBytes, right = rightRow * RowBytes;
                int changed = 0;
                for (int x = 0; x < RowBytes; x += 3)
                {
                    if (Math.Abs(Pixels[left + x] - other.Pixels[right + x]) > 6 ||
                        Math.Abs(Pixels[left + x + 1] - other.Pixels[right + x + 1]) > 6 ||
                        Math.Abs(Pixels[left + x + 2] - other.Pixels[right + x + 2]) > 6) changed++;
                }
                // A blinking caret or a clock digit must not turn a fixed input
                // bar into scrollable content. Substantial changes still stop it.
                return changed <= Math.Max(1, RowBytes / 3 / 32);
            }
            internal bool HasDetail(int start, int length)
            {
                if (length == 0) return false;
                int origin = start * RowBytes;
                for (int y = start; y < start + length; y++)
                    for (int x = 0; x < RowBytes; x++)
                        if (Math.Abs(Pixels[y * RowBytes + x] - Pixels[origin + x % 3]) > 16) return true;
                return false;
            }
            internal bool HasVerticalDetail(int start, int length)
            {
                if (length < 2) return false;
                int changed = 0;
                for (int y = start + 1; y < start + length; y++)
                {
                    bool different = false;
                    for (int x = 0; x < RowBytes; x++)
                        if (Math.Abs(Pixels[y * RowBytes + x] - Pixels[(y - 1) * RowBytes + x]) > 8)
                        { different = true; break; }
                    if (different) changed++;
                }
                return changed >= Math.Min(4, length / 4);
            }
        }

        private void CheckAlive()
        { if (disposed) throw new ObjectDisposedException("ScrollStitcher"); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (previous != null) previous.Dispose();
            if (footer != null) footer.Dispose();
            foreach (Bitmap part in parts) part.Dispose();
            parts.Clear(); previous = footer = null; previousSamples = null;
        }
    }
}
