using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace QingJie
{
    internal sealed class ScreenshotQueueEntry : IDisposable
    {
        public readonly long Id;
        public Bitmap Image;
        public Bitmap Thumbnail;
        public bool Saved;
        public readonly HashSet<long> BoardImageIds = new HashSet<long>();

        public ScreenshotQueueEntry(long id, Bitmap image, bool saved)
        {
            Id = id;
            Image = image;
            Saved = saved;
        }

        public void DisposeThumbnail()
        {
            if (Thumbnail != null) Thumbnail.Dispose();
            Thumbnail = null;
        }

        public void Dispose()
        {
            DisposeThumbnail();
            if (Image != null) Image.Dispose();
            Image = null;
        }
    }

    /// <summary>
    /// Owns full-resolution snapshots and one cached thumbnail per screenshot.
    /// Painting never resamples an unchanged full-resolution screenshot.
    /// </summary>
    internal sealed class ScreenshotQueueControl : ScrollableControl
    {
        private readonly List<ScreenshotQueueEntry> entries = new List<ScreenshotQueueEntry>();
        private long selectedId;
        private float unit = 1F;
        public event EventHandler SelectionChanged;
        public int Count { get { return entries.Count; } }
        public long SelectedId { get { return selectedId; } }
        public IList<ScreenshotQueueEntry> Entries { get { return entries.AsReadOnly(); } }
        public ScreenshotQueueEntry SelectedEntry { get { return Find(selectedId); } }

        public ScreenshotQueueControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            BackColor = Color.FromArgb(244, 248, 254);
            AccessibleName = "截图队列，选择后可拖到右侧画板";
            TabStop = true;
        }

        public ScreenshotQueueEntry Find(long id)
        {
            foreach (ScreenshotQueueEntry entry in entries)
                if (entry.Id == id) return entry;
            return null;
        }

        public void Upsert(Bitmap image, long id, bool saved)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (id == 0) throw new ArgumentOutOfRangeException("id");
            Bitmap copy = (Bitmap)image.Clone();
            ScreenshotQueueEntry entry = Find(id);
            Bitmap previous = null;
            if (entry == null)
            {
                entry = new ScreenshotQueueEntry(id, copy, saved);
                entries.Add(entry);
            }
            else
            {
                previous = entry.Image;
                entry.Image = copy;
                entry.Saved = saved;
                entry.BoardImageIds.Clear();
                entry.DisposeThumbnail();
            }
            selectedId = id;
            UpdateScrollExtent();
            ScrollSelectionIntoView();
            RaiseSelectionChanged();
            if (previous != null) previous.Dispose();
            // Once the host has established DPI and width, prepare the updated
            // thumbnail before a later switch makes the queue visible.
            if (IsHandleCreated && ClientSize.Width > 32 && ClientSize.Height > 0)
                EnsureThumbnail(entry, ThumbnailBounds(ItemBounds(entries.IndexOf(entry))).Size);
            Invalidate();
        }

        public bool SelectId(long id)
        {
            if (id != 0 && Find(id) == null) return false;
            if (selectedId == id) return true;
            selectedId = id;
            ScrollSelectionIntoView();
            RaiseSelectionChanged();
            Invalidate();
            return true;
        }

        public bool Remove(long id)
        {
            ScreenshotQueueEntry entry = Find(id);
            if (entry == null) return false;
            int index = entries.IndexOf(entry);
            entries.RemoveAt(index);
            if (selectedId == id)
                selectedId = entries.Count == 0 ? 0 : entries[Math.Min(index, entries.Count - 1)].Id;
            UpdateScrollExtent();
            ScrollSelectionIntoView();
            RaiseSelectionChanged();
            entry.Dispose();
            Invalidate();
            return true;
        }

        public long HitTest(Point clientPoint)
        {
            if (clientPoint.X < 0 || clientPoint.X >= ClientSize.Width ||
                clientPoint.Y < 0 || clientPoint.Y >= ClientSize.Height) return 0;
            for (int i = 0; i < entries.Count; i++)
                if (ItemBounds(i).Contains(clientPoint)) return entries[i].Id;
            return 0;
        }

        private int RowStep { get { return (int)Math.Round(152 * unit); } }
        private Rectangle ItemBounds(int index)
        {
            int inset = (int)Math.Round(5 * unit);
            return new Rectangle(inset, inset + index * RowStep + AutoScrollPosition.Y,
                Math.Max(1, ClientSize.Width - inset * 2),
                Math.Max(1, RowStep - (int)Math.Round(8 * unit)));
        }

        private void UpdateScrollExtent()
        {
            AutoScrollMinSize = new Size(0, entries.Count * RowStep + (int)Math.Round(10 * unit));
        }

        private void ScrollSelectionIntoView()
        {
            ScreenshotQueueEntry selected = SelectedEntry;
            if (selected == null) return;
            Rectangle bounds = ItemBounds(entries.IndexOf(selected));
            int scrollY = -AutoScrollPosition.Y;
            if (bounds.Top < 0) scrollY += bounds.Top;
            else if (bounds.Bottom > ClientSize.Height) scrollY += bounds.Bottom - ClientSize.Height;
            AutoScrollPosition = new Point(0, Math.Max(0, scrollY));
        }

        private void RaiseSelectionChanged()
        {
            EventHandler handler = SelectionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (Graphics graphics = CreateGraphics()) unit = graphics.DpiX / 96F;
            UpdateScrollExtent();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollExtent();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled)
            {
                Focus();
                long id = HitTest(e.Location);
                if (id != 0 && id != selectedId)
                {
                    // Do not move a partly visible card under the pointer while
                    // the user starts a drag or a double-click.
                    selectedId = id;
                    RaiseSelectionChanged();
                    Invalidate();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && entries.Count > 0)
            {
                int index = Math.Max(0, entries.IndexOf(SelectedEntry));
                index = Math.Max(0, Math.Min(entries.Count - 1, index + (e.KeyCode == Keys.Up ? -1 : 1)));
                SelectId(entries[index].Id);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        private void EnsureThumbnail(ScreenshotQueueEntry entry, Size size)
        {
            if (entry.Thumbnail != null && entry.Thumbnail.Size == size) return;
            Bitmap thumbnail = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(thumbnail))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float scale = Math.Min((float)size.Width / entry.Image.Width,
                        (float)size.Height / entry.Image.Height);
                    float width = entry.Image.Width * scale, height = entry.Image.Height * scale;
                    using (ImageAttributes attributes = new ImageAttributes())
                    {
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(entry.Image,
                            new Rectangle((int)Math.Round((size.Width - width) / 2),
                                (int)Math.Round((size.Height - height) / 2),
                                Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height))),
                            0, 0, entry.Image.Width, entry.Image.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            catch { thumbnail.Dispose(); throw; }
            entry.DisposeThumbnail();
            entry.Thumbnail = thumbnail;
        }

        private Rectangle ThumbnailBounds(Rectangle bounds)
        {
            int inset = (int)Math.Round(7 * unit), caption = (int)Math.Round(28 * unit);
            return new Rectangle(bounds.X + inset, bounds.Y + inset,
                Math.Max(1, bounds.Width - 2 * inset), Math.Max(1, bounds.Height - caption - 2 * inset));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            if (Math.Abs(unit - graphics.DpiX / 96F) > .001F)
            {
                unit = graphics.DpiX / 96F;
                UpdateScrollExtent();
            }
            using (Brush background = new SolidBrush(BackColor))
                graphics.FillRectangle(background, e.ClipRectangle);
            if (entries.Count == 0)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float tileSize = Math.Min(48 * unit, Math.Max(24 * unit, ClientSize.Width - 28 * unit));
                RectangleF tile = new RectangleF((ClientSize.Width - tileSize) / 2,
                    Math.Max(10 * unit, ClientSize.Height / 2f - 56 * unit), tileSize, tileSize);
                using (GraphicsPath path = UiTheme.RoundedRectangle(tile, 14 * unit))
                using (Brush fill = new SolidBrush(Color.FromArgb(230, 239, 254)))
                    graphics.FillPath(fill, path);
                UiTheme.DrawIcon(graphics, UiIcon.Capture,
                    new RectangleF(tile.Left + tileSize * .27f, tile.Top + tileSize * .27f,
                        tileSize * .46f, tileSize * .46f), Color.FromArgb(112, 147, 209));
                Rectangle text = new Rectangle((int)(8 * unit), (int)(tile.Bottom + 12 * unit),
                    Math.Max(1, ClientSize.Width - (int)(16 * unit)), (int)(52 * unit));
                TextRenderer.DrawText(graphics, "从一张截图开始\n截图会持续保留在这里", Font, text,
                    UiTheme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak);
                return;
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < entries.Count; i++)
            {
                Rectangle bounds = ItemBounds(i);
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                ScreenshotQueueEntry entry = entries[i];
                bool selected = entry.Id == selectedId;
                RectangleF outline = new RectangleF(bounds.X + unit / 2, bounds.Y + unit / 2,
                    Math.Max(1, bounds.Width - unit), Math.Max(1, bounds.Height - unit));
                using (GraphicsPath card = UiTheme.RoundedRectangle(outline, 9 * unit))
                {
                    using (Brush fill = new LinearGradientBrush(bounds,
                        selected ? Color.FromArgb(237, 244, 255) : Color.White,
                        selected ? Color.FromArgb(225, 237, 255) : Color.FromArgb(250, 252, 255), 90f))
                        graphics.FillPath(fill, card);
                    using (Pen border = new Pen(selected ? Color.FromArgb(96, 140, 240) :
                        UiTheme.Border, selected ? 1.6f * unit : unit))
                        graphics.DrawPath(border, card);
                }
                int caption = (int)Math.Round(28 * unit);
                Rectangle imageBounds = ThumbnailBounds(bounds);
                EnsureThumbnail(entry, imageBounds.Size);
                GraphicsState imageState = graphics.Save();
                using (GraphicsPath imageClip = UiTheme.RoundedRectangle(imageBounds, 4 * unit))
                    graphics.SetClip(imageClip, CombineMode.Intersect);
                graphics.DrawImageUnscaled(entry.Thumbnail, imageBounds.Location);
                graphics.Restore(imageState);
                Rectangle textBounds = new Rectangle(imageBounds.Left, imageBounds.Bottom + (int)(5 * unit),
                    imageBounds.Width, caption - (int)(4 * unit));
                string text = "截图 " + (i + 1) + "   " + entry.Image.Width + " × " + entry.Image.Height;
                TextRenderer.DrawText(graphics, text, Font, textBounds,
                    selected ? Color.FromArgb(48, 88, 161) : UiTheme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (ScreenshotQueueEntry entry in entries) entry.Dispose();
                entries.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
