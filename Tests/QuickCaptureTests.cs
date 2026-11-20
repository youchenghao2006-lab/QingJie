using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
using QingJie;

internal static class QuickCaptureTests
{
    private static int passed;
    [STAThread]
    private static int Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            Run("undo redo reset and new-edit invalidation", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (AnnotationCanvas canvas = Canvas(source))
                {
                    Stroke(canvas);
                    using (Bitmap changed = canvas.ExportImage())
                    {
                        Require(canvas.CanUndo && !canvas.CanRedo, "initial history");
                        canvas.Undo();
                        using (Bitmap original = canvas.ExportImage()) EqualPixels(source, original);
                        Require(!canvas.CanUndo && canvas.CanRedo, "undo history");
                        canvas.Redo();
                        using (Bitmap redone = canvas.ExportImage()) EqualPixels(changed, redone);
                        canvas.Undo();
                        canvas.Tool = DrawingTool.Rectangle;
                        Mouse(canvas, "OnMouseDown", new Point(15, 15));
                        Mouse(canvas, "OnMouseMove", new Point(50, 40));
                        Mouse(canvas, "OnMouseUp", new Point(50, 40));
                        Require(!canvas.CanRedo, "new edit retained redo");
                        canvas.ResetAnnotations();
                        using (Bitmap reset = canvas.ExportImage()) EqualPixels(source, reset);
                        Require(!canvas.CanUndo && !canvas.CanRedo, "reset history");
                    }
                    Stroke(canvas);
                    canvas.Undo();
                    canvas.SetImage(source);
                    Require(!canvas.CanUndo && !canvas.CanRedo, "replace history");
                }
            });
            Run("compact mode has no padding and preserves export", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (AnnotationCanvas canvas = Canvas(source))
                {
                    RectangleF bounds = (RectangleF)typeof(AnnotationCanvas).GetField("_imageBounds",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas);
                    Require(bounds == new RectangleF(0, 0, 120, 80), "compact bounds " + bounds);
                    using (Bitmap copy = canvas.ExportImage()) EqualPixels(source, copy);
                    canvas.CompactMode = false;
                    bounds = (RectangleF)typeof(AnnotationCanvas).GetField("_imageBounds",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(canvas);
                    Require(bounds.Width < 120 && bounds.Height < 80, "normal padding removed");
                }
            });
            Run("compact preview maps physical pixels across image and screen DPI", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                {
                    source.SetResolution(72, 72);
                    using (AnnotationCanvas canvas = Canvas(source))
                    foreach (int dpi in new int[] { 96, 192, 300 })
                    using (Bitmap rendered = new Bitmap(source.Width, source.Height))
                    {
                        rendered.SetResolution(dpi, dpi);
                        using (Graphics g = Graphics.FromImage(rendered))
                            canvas.PaintViewportRegion(g, new Rectangle(Point.Empty, source.Size));
                        EqualPixels(source, rendered);
                    }
                }
            });
            Run("quick accepts independent revisions without showing windows", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(-300, 200, 120, 80)))
                {
                    IntPtr formHandle = quick.Handle;
                    IntPtr canvasHandle = quick.Canvas.Handle;
                    int count = 0;
                    quick.ImageAccepted += delegate(Bitmap accepted)
                    {
                        using (accepted) { count++; accepted.SetPixel(1, 1, Color.Magenta); }
                    };
                    source.SetPixel(1, 1, Color.Yellow);
                    quick.AcceptCurrentImage();
                    quick.AcceptCurrentImage();
                    Require(count == 1 && !quick.Visible, "accept revision/show");
                    using (Bitmap export = quick.Canvas.ExportImage())
                        Require(export.GetPixel(1, 1).ToArgb() == Color.FromArgb(255, 7, 11, 26).ToArgb(),
                            "source ownership");
                    Stroke(quick.Canvas);
                    quick.AcceptCurrentImage();
                    Require(count == 2, "edited revision not accepted");
                }
            });
            Run("pin event owns its clone and retains the requested location", delegate
            {
                using (Bitmap source = Pattern(60, 40))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(-230, 90, 60, 40)))
                {
                    Bitmap accepted = null, pinned = null;
                    Point location = Point.Empty;
                    quick.ImageAccepted += delegate(Bitmap value) { accepted = value; };
                    quick.PinRequested += delegate(Bitmap value, Point point) { pinned = value; location = point; };
                    quick.PinCurrentImage();
                    try
                    {
                        Require(accepted != null && pinned != null &&
                            !Object.ReferenceEquals(accepted, pinned), "transfers share bitmap");
                        Require(location == new Point(-230, 90), "pin location");
                        accepted.SetPixel(1, 1, Color.Magenta);
                        Require(pinned.GetPixel(1, 1) != Color.Magenta, "pin transfer aliases accepted");
                        Require(!quick.Visible, "pin showed quick form");
                    }
                    finally { if (accepted != null) accepted.Dispose(); if (pinned != null) pinned.Dispose(); }
                }
            });
            Run("pinned transforms and export retain independent source pixels", delegate
            {
                using (Bitmap source = Pattern(3, 2))
                using (PinnedImageForm pinned = new PinnedImageForm(source, new Point(-80, 40)))
                {
                    pinned.RotateClockwise();
                    Require(pinned.ImageSize == new Size(2, 3), "rotation size");
                    using (Bitmap turned = pinned.ExportImage())
                        Require(turned.GetPixel(1, 0) == source.GetPixel(0, 0), "clockwise pixels");
                    pinned.RotateCounterClockwise();
                    using (Bitmap restored = pinned.ExportImage()) EqualPixels(source, restored);
                    pinned.FlipHorizontal();
                    using (Bitmap flipped = pinned.ExportImage())
                        Require(flipped.GetPixel(0, 0) == source.GetPixel(2, 0), "horizontal pixels");
                    pinned.FlipHorizontal();
                    pinned.FlipVertical();
                    using (Bitmap flipped = pinned.ExportImage())
                        Require(flipped.GetPixel(0, 0) == source.GetPixel(0, 1), "vertical pixels");
                    pinned.FlipVertical();
                    using (Bitmap first = pinned.ExportImage())
                    {
                        first.SetPixel(0, 0, Color.Magenta);
                        using (Bitmap second = pinned.ExportImage()) EqualPixels(source, second);
                    }
                    Require(!pinned.Visible, "transform showed form");
                }
            });
            Run("pinned wheel-style zoom preserves screen anchor", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (PinnedImageForm pinned = new PinnedImageForm(source, new Point(-400, 200)))
                {
                    Rectangle before = pinned.ImageScreenBounds;
                    Require(before.Location == new Point(-400, 200), "initial border alignment");
                    Point anchor = new Point(before.Left + 37, before.Top + 23);
                    pinned.SetZoom(200, anchor);
                    Rectangle after = pinned.ImageScreenBounds;
                    Require(Math.Abs(anchor.X - (after.Left + 74)) <= 1 &&
                        Math.Abs(anchor.Y - (after.Top + 46)) <= 1, "zoom anchor");
                    using (Bitmap unchanged = pinned.ExportImage()) EqualPixels(source, unchanged);
                    pinned.SetOpacityPercent(60);
                    Require(Math.Abs(pinned.Opacity - .6) < .001, "opacity");
                    Require(pinned.OwnedForms.Length == 0, "pin created a floating toolbox");
                    bool rejected = false;
                    try { pinned.SetZoom(Single.NaN, anchor); } catch (ArgumentOutOfRangeException) { rejected = true; }
                    Require(rejected, "non-finite zoom");
                }
            });
            Run("floating toolbar placement handles negative monitors", delegate
            {
                Rectangle work = new Rectangle(-1920, -200, 1920, 1040);
                Size toolbar = new Size(514, 88);
                Point point = CaptureWindowPlacement.Beside(new Rectangle(-130, 740, 100, 80), toolbar, work);
                Require(work.Contains(new Rectangle(point, toolbar)), "toolbar outside work area");
                Require(point.Y < 740, "bottom overflow did not place above");
            });
            Run("adjusted capture re-crops owned desktop pixels with negative origins", delegate
            {
                Rectangle desktopBounds = new Rectangle(-200, -100, 320, 200);
                Rectangle selected = new Rectangle(-150, -60, 100, 80);
                using (Bitmap desktop = Pattern(320, 200))
                using (Bitmap original = desktop.Clone(new Rectangle(0, 0, 320, 200), PixelFormat.Format32bppArgb))
                using (Bitmap crop = desktop.Clone(new Rectangle(50, 40, 100, 80), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop, selected, desktop, desktopBounds))
                {
                    desktop.SetPixel(20, 20, Color.Magenta);
                    quick.SetSelectionBounds(new Rectangle(-180, -80, 170, 120));
                    Require(quick.SelectionBounds == new Rectangle(-180, -80, 170, 120), "adjusted bounds");
                    using (Bitmap expected = original.Clone(new Rectangle(20, 20, 170, 120), PixelFormat.Format32bppArgb))
                    using (Bitmap result = quick.Canvas.ExportImage()) EqualPixels(expected, result);
                    quick.SetSelectionBounds(new Rectangle(-1000, -1000, 10000, 10000));
                    Require(quick.SelectionBounds == desktopBounds, "desktop clamp");
                    quick.SetSelectionBounds(new Rectangle(-190, -90, 1, 1));
                    Require(quick.Canvas.ImageSize == new Size(2, 2), "minimum capture size");
                }
            });
            Run("capture adjustment preserves annotation world positions and redo", delegate
            {
                Rectangle desktopBounds = new Rectangle(-200, -100, 320, 200);
                Rectangle selected = new Rectangle(-150, -60, 100, 80);
                using (Bitmap desktop = Pattern(320, 200))
                using (Bitmap crop = desktop.Clone(new Rectangle(50, 40, 100, 80), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop, selected, desktop, desktopBounds))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    handle = ((Control)Field(quick, "_selectionFrame")).Handle;
                    Stroke(quick.Canvas);
                    Require(quick.Canvas.CanUndo, "fixture stroke did not commit");
                    using (Bitmap annotated = quick.Canvas.ExportImage())
                    {
                        quick.Canvas.Undo();
                        quick.SetSelectionBounds(new Rectangle(-180, -80, 170, 120));
                        Require(quick.Canvas.CanRedo, "adjustment discarded redo");
                        quick.Canvas.Redo();
                        using (Bitmap expanded = quick.Canvas.ExportImage())
                        using (Bitmap oldArea = expanded.Clone(new Rectangle(30, 20, 100, 80),
                            PixelFormat.Format32bppArgb)) EqualPixels(annotated, oldArea);
                        quick.SetSelectionBounds(selected);
                        using (Bitmap originalArea = quick.Canvas.ExportImage()) EqualPixels(annotated, originalArea);
                        quick.Canvas.Undo();
                        using (Bitmap originalArea = quick.Canvas.ExportImage()) EqualPixels(crop, originalArea);
                    }
                }
            });
            Run("selection drag reuses bitmaps until release and Escape restores bounds", delegate
            {
                Rectangle desktopBounds = new Rectangle(-300, -100, 700, 400);
                Rectangle selected = new Rectangle(-200, -20, 180, 140);
                using (Bitmap desktop = Pattern(700, 400))
                using (Bitmap crop = desktop.Clone(new Rectangle(100, 80, 180, 140), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop, selected, desktop, desktopBounds))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    Control frame = (Control)Field(quick, "_selectionFrame");
                    handle = frame.Handle;
                    int changes = 0;
                    quick.Canvas.ContentChanged += delegate { changes++; };
                    object source = Field(quick.Canvas, "_source");
                    object desktopCopy = Field(quick, "_desktopSnapshot");
                    Type edges = typeof(QuickCaptureForm).GetNestedType("SelectionEdges", BindingFlags.NonPublic);
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12), new Point(-20, 120));
                    object preview = Field(quick, "_resizeComposite");
                    int nativeResizes = 0;
                    quick.SizeChanged += delegate { nativeResizes++; };
                    long started = Stopwatch.GetTimestamp();
                    for (int i = 1; i <= 500; i++)
                        Invoke(quick, "UpdateSelectionResize", new Point(-20 + i % 70, 120 + i % 50));
                    double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    Require(changes == 0 && quick.Canvas.ImageSize == crop.Size, "drag rebuilt annotation source");
                    Require(nativeResizes == 0 && quick.Bounds == selected, "mouse burst resized native window before frame");
                    Require(Object.ReferenceEquals(source, Field(quick.Canvas, "_source")) &&
                        Object.ReferenceEquals(desktopCopy, Field(quick, "_desktopSnapshot")) &&
                        Object.ReferenceEquals(preview, Field(quick, "_resizeComposite")), "drag allocated image buffers");
                    Invoke(quick, "FinishSelectionResize", false);
                    Require(changes == 1 && nativeResizes == 1 &&
                        quick.Canvas.ImageSize == quick.SelectionBounds.Size, "release did not flush and crop once");
                    Rectangle committed = quick.SelectionBounds;
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 3), committed.Location);
                    Invoke(quick, "UpdateSelectionResize", new Point(committed.Left - 40, committed.Top - 30));
                    Invoke(quick, "FinishSelectionResize", true);
                    Require(quick.Bounds == committed && quick.SelectionBounds == committed && changes == 1,
                        "cancel altered committed pixels");
                    Size acceptedSize = Size.Empty;
                    quick.ImageAccepted += delegate(Bitmap image)
                    { using (image) acceptedSize = image.Size; };
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12),
                        new Point(committed.Right, committed.Bottom));
                    Invoke(quick, "UpdateSelectionResize",
                        new Point(committed.Right + 12, committed.Bottom + 8));
                    quick.AcceptCurrentImage();
                    Require(acceptedSize == quick.Bounds.Size &&
                        acceptedSize == quick.SelectionBounds.Size && changes == 2,
                        "confirmation exported the old size during adjustment");
                    Require(!quick.Visible, "drag showed a window");
                    Console.WriteLine("selection-drag 500 handlers ms=" + elapsed.ToString("F3") +
                        " source_copies_during_move=0");
                }
            });
            Run("borrowed desktop preserves identity and caller ownership", delegate
            {
                using (Bitmap desktop = Pattern(320, 200))
                using (Bitmap crop = desktop.Clone(new Rectangle(50, 40, 100, 80), PixelFormat.Format32bppArgb))
                {
                    Rectangle world = new Rectangle(-200, -100, 320, 200);
                    using (QuickCaptureForm quick = new QuickCaptureForm(crop,
                        new Rectangle(-150, -60, 100, 80), desktop, world, true))
                    {
                        Require(Object.ReferenceEquals(desktop, Field(quick, "_desktopSnapshot")), "borrow cloned desktop");
                        quick.SetSelectionBounds(new Rectangle(-190, -90, 140, 100));
                        using (Bitmap expected = desktop.Clone(new Rectangle(10, 10, 140, 100), PixelFormat.Format32bppArgb))
                        using (Bitmap actual = quick.Canvas.ExportImage()) EqualPixels(expected, actual);
                    }
                    Require(desktop.GetPixel(1, 1).A == 255, "borrower disposed caller bitmap");
                }
            });
            Run("single-row icon toolbar fits high DPI and keeps overflow actions accessible", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(20, 30, 120, 80)))
                {
                    CaptureToolbarForm toolbar = (CaptureToolbarForm)Field(quick, "_toolbar");
                    List<CaptureToolbarButton> buttons = (List<CaptureToolbarButton>)Field(toolbar, "_buttons");
                    ToolTip tips = (ToolTip)Field(toolbar, "_tips");
                    Require(buttons.Count == 17, "missing toolbar actions");
                    foreach (CaptureToolbarButton button in buttons)
                        Require(!String.IsNullOrEmpty(tips.GetToolTip(button)), "missing tooltip " + button.Text);
                    foreach (int dpi in new int[] { 96, 120, 144, 192 })
                        foreach (int width in new int[] { 1920, 1024, 640, 240 })
                        {
                            Rectangle work = new Rectangle(-width, -60, width, 700);
                            toolbar.FitWithin(work, dpi / 96f);
                            int shown = (int)Field(toolbar, "_shownCount");
                            CaptureToolbarButton more = (CaptureToolbarButton)Field(toolbar, "_overflowButton");
                            int y = shown > 0 ? buttons[0].Top : more.Top;
                            Require(toolbar.Width <= work.Width && toolbar.Height <= 90,
                                "toolbar does not fit one row");
                            int right = 0;
                            for (int i = 0; i < shown; i++)
                            {
                                Require(buttons[i].Top == y && buttons[i].Left >= right &&
                                    toolbar.ClientRectangle.Contains(buttons[i].Bounds), "overlapping/clipped icon");
                                right = buttons[i].Right;
                            }
                            if (shown < buttons.Count)
                            {
                                Require(more.Top == y && more.Left >= right &&
                                    toolbar.ClientRectangle.Contains(more.Bounds), "invalid more placement");
                                Invoke(toolbar, "PopulateOverflowMenu");
                                ContextMenuStrip menu = (ContextMenuStrip)Field(toolbar, "_overflowMenu");
                                Require(menu.Items.Count == buttons.Count - shown, "lost overflow command");
                            }
                        }
                    Require(!quick.Visible && !toolbar.Visible, "layout displayed real UI");
                }
                using (CaptureToolbarForm toolbar = new CaptureToolbarForm("synthetic overflow"))
                {
                    int invoked = 0;
                    for (int i = 0; i < 16; i++) toolbar.AddButton("复制", delegate { invoked++; });
                    toolbar.FitWithin(new Rectangle(0, 0, 240, 300), 2);
                    Invoke(toolbar, "PopulateOverflowMenu");
                    ContextMenuStrip menu = (ContextMenuStrip)Field(toolbar, "_overflowMenu");
                    menu.Items[0].PerformClick();
                    Require(invoked == 1, "hidden overflow action did not invoke");
                }
            });
            Run("closing tolerates owner disposal and late layout or timer callbacks", delegate
            {
                using (Bitmap source = Pattern(160, 100))
                {
                    QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(20, 20, 160, 100));
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    handle = ((Control)Field(quick, "_selectionFrame")).Handle;
                    CaptureToolbarForm toolbar = (CaptureToolbarForm)Field(quick, "_toolbar");
                    toolbar.FitWithin(new Rectangle(0, 0, 800, 600));
                    quick.FormClosed += delegate { quick.Dispose(); };
                    Type edges = typeof(QuickCaptureForm).GetNestedType("SelectionEdges", BindingFlags.NonPublic);
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12), new Point(180, 120));
                    Invoke(quick, "UpdateSelectionResize", new Point(200, 140));
                    quick.Close();
                    quick.Dispose();
                    Invoke(quick, "OnLocationChanged", EventArgs.Empty);
                    Invoke(quick, "ApplyPendingResizeBounds");
                    toolbar.FitWithin(new Rectangle(0, 0, 800, 600));
                    using (PinnedImageForm pinned = new PinnedImageForm(source, new Point(20, 20)))
                    {
                        handle = pinned.Handle;
                        pinned.SetZoom(120, new Point(50, 50));
                        pinned.FormClosed += delegate { pinned.Dispose(); };
                        pinned.Close();
                        pinned.Dispose();
                        Invoke(pinned, "OnLocationChanged", EventArgs.Empty);
                    }
                }
            });
            Run("mouseup commits its exact endpoint and native bounds can span monitors", delegate
            {
                using (Bitmap desktop = Pattern(400, 300))
                using (Bitmap crop = desktop.Clone(new Rectangle(30, 40, 100, 80), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop,
                    new Rectangle(-70, -10, 100, 80), desktop, new Rectangle(-100, -50, 400, 300)))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    Control frame = (Control)Field(quick, "_selectionFrame");
                    handle = frame.Handle;
                    Type edges = typeof(QuickCaptureForm).GetNestedType("SelectionEdges", BindingFlags.NonPublic);
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12), new Point(30, 70));
                    Invoke(frame, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 125, 95, 0));
                    Require(quick.SelectionBounds == new Rectangle(-70, -10, 125, 95), "mouseup without move lost endpoint");
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12), new Point(55, 85));
                    Invoke(quick, "UpdateSelectionResize", new Point(60, 90));
                    Invoke(frame, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 140, 110, 0));
                    Require(quick.SelectionBounds == new Rectangle(-70, -10, 140, 110), "mouseup retained stale pending endpoint");
                }
                using (Bitmap wide = new Bitmap(3200, 1800))
                {
                    Rectangle bounds = new Rectangle(-1400, -100, 3200, 1800);
                    using (QuickCaptureForm quick = new QuickCaptureForm(wide, bounds, wide, bounds, true))
                    {
                        IntPtr handle = quick.Handle;
                        Require(quick.Bounds == bounds && quick.ClientSize == bounds.Size,
                            "initial screenshot was clipped to one monitor");
                        quick.SetSelectionBounds(new Rectangle(-1400, -100, 3000, 1600));
                        Require(quick.Bounds == quick.SelectionBounds &&
                            quick.ClientSize == new Size(3000, 1600), "adjusted large selection was clipped");
                    }
                    using (PinnedImageForm pinned = new PinnedImageForm(wide, new Point(-1400, -100)))
                    {
                        IntPtr handle = pinned.Handle;
                        Require(pinned.ImageScreenBounds == bounds, "initial large pin was clipped");
                        pinned.SetZoom(120, new Point(-1400, -100));
                        Require(pinned.ImageScreenBounds.Size == new Size(3840, 2160), "large pin zoom was clipped");
                    }
                }
            });
            Run("selection interior drag is discoverable and preview allocation is lazy", delegate
            {
                Rectangle desktopBounds = new Rectangle(-300, -100, 700, 400);
                Rectangle initial = new Rectangle(-200, -20, 180, 140);
                using (Bitmap desktop = Pattern(700, 400))
                using (Bitmap crop = desktop.Clone(new Rectangle(100, 80, 180, 140), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop, initial, desktop, desktopBounds))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    Control frame = (Control)Field(quick, "_selectionFrame");
                    handle = frame.Handle;
                    Invoke(quick.Canvas, "OnMouseEnter", EventArgs.Empty);
                    Require(quick.Canvas.Cursor == Cursors.SizeAll, "adjust mode lacks move cursor");
                    object source = Field(quick.Canvas, "_source");
                    Mouse(quick.Canvas, "OnMouseDown", new Point(50, 50));
                    Require((bool)Field(quick, "_resizing") && Field(quick, "_resizeComposite") == null &&
                        !(bool)Field(quick, "_resizePreviewStarted"), "click allocated preview");
                    Invoke(frame, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 50, 50, 0));
                    Require(quick.SelectionBounds == initial && Object.ReferenceEquals(source,
                        Field(quick.Canvas, "_source")), "click rebuilt source");
                    Mouse(quick.Canvas, "OnMouseDown", new Point(50, 50));
                    Invoke(frame, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 80, 70, 0));
                    Require(Field(quick, "_resizeComposite") == null, "unannotated drag copied screenshot");
                    Invoke(quick, "ApplyPendingResizeBounds");
                    Invoke(frame, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 50, 50, 0));
                    Require(quick.SelectionBounds == new Rectangle(-170, 0, 180, 140), "interior drag endpoint");
                    using (Bitmap expected = desktop.Clone(new Rectangle(130, 100, 180, 140), PixelFormat.Format32bppArgb))
                    using (Bitmap actual = quick.Canvas.ExportImage()) EqualPixels(expected, actual);
                    Stroke(quick.Canvas);
                    quick.Canvas.Tool = DrawingTool.Select;
                    Invoke(quick, "UpdateActions");
                    Mouse(quick.Canvas, "OnMouseDown", new Point(50, 50));
                    Require(Field(quick, "_resizeComposite") == null, "annotated click copied image");
                    Invoke(frame, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 55, 55, 0));
                    Require(Field(quick, "_resizeComposite") != null, "annotated drag lost preview");
                    Invoke(quick, "FinishSelectionResize", true);
                    Require(quick.Canvas.CanUndo, "cancel discarded annotation history");
                    Invoke(frame, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, frame.Width - 2, frame.Height - 2, 0));
                    Require(frame.Cursor == Cursors.SizeNWSE && Convert.ToInt32(Field(frame, "_hoverEdges")) == 12,
                        "corner hover feedback");
                    Invoke(frame, "OnMouseLeave", EventArgs.Empty);
                    Require(Convert.ToInt32(Field(frame, "_hoverEdges")) == 0, "stale hover feedback");
                }
            });
            Run("toolbar groups remain single row and pinned input stays under the pointer", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(20, 20, 120, 80)))
                {
                    CaptureToolbarForm toolbar = (CaptureToolbarForm)Field(quick, "_toolbar");
                    toolbar.FitWithin(new Rectangle(0, 0, 1920, 1080), 1);
                    List<CaptureToolbarButton> buttons = (List<CaptureToolbarButton>)Field(toolbar, "_buttons");
                    Require(buttons[7].StartsGroup && buttons[9].StartsGroup && buttons[12].StartsGroup &&
                        buttons[16].StartsGroup, "toolbar action groups missing");
                    Require(buttons[7].Left - buttons[6].Right > buttons[2].Left - buttons[1].Right,
                        "group has no visual spacing");
                }
                using (Bitmap source = Pattern(120, 80))
                using (PinnedImageForm pinned = new PinnedImageForm(source, new Point(-200, 80)))
                {
                    IntPtr handle = pinned.Handle;
                    Point original = pinned.Location;
                    Invoke(pinned, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, -5, 20, 120));
                    Require(pinned.ZoomPercent == 100 && pinned.Location == original, "off-window wheel zoomed pin");
                    Invoke(pinned, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 30, 30, 0));
                    Invoke(pinned, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 50, 45, 0));
                    Require(pinned.Location == new Point(original.X + 20, original.Y + 15), "pin lost mouseup endpoint");
                    Point committed = pinned.Location;
                    Invoke(pinned, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 30, 30, 0));
                    Invoke(pinned, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 1, 50, 45, 0));
                    Invoke(pinned, "HandleShortcut", Keys.Escape);
                    Require(!pinned.IsDisposed && pinned.Location == committed &&
                        !(bool)Field(pinned, "_dragging"), "Escape did not cancel pin drag");
                }
            });
            Run("pinned images remain clean after shown double-click and right-click callbacks", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (PinnedImageForm pinned = new PinnedImageForm(source, new Point(-180, 90)))
                {
                    IntPtr handle = pinned.Handle;
                    int forms = Application.OpenForms.Count;
                    Invoke(pinned, "OnShown", EventArgs.Empty);
                    for (int i = 0; i < 3; i++)
                        Invoke(pinned, "OnMouseDoubleClick", new MouseEventArgs(MouseButtons.Left, 2, 30, 30, 0));
                    Invoke(pinned, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 30, 30, 0));
                    Invoke(pinned, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, 30, 30, 0));
                    Require(pinned.OwnedForms.Length == 0 && Application.OpenForms.Count == forms,
                        "pin interaction created a floating toolbox");
                    ContextMenuStrip menu = pinned.ContextMenuStrip;
                    Require(menu != null && !menu.Visible, "pin lost its contextual operations");
                    foreach (ToolStripItem item in menu.Items)
                        Require(!item.Text.Contains("工具条") && !item.Text.Contains("工具箱"),
                            "pin menu can restore the removed toolbox");
                    string[] commands = { "复制图像", "保存图像", "原始尺寸", "缩放", "向左旋转",
                        "向右旋转", "水平镜像", "垂直镜像", "透明度", "关闭贴图" };
                    foreach (string command in commands)
                        Require(FindMenuCommand(menu, command) != null, "missing pin command " + command);
                    pinned.SetZoom(160, new Point(-140, 120));
                    FindMenuCommand(menu, "原始尺寸").PerformClick();
                    Require(Math.Abs(pinned.ZoomPercent - 100) < .001 && pinned.OwnedForms.Length == 0,
                        "original-size menu did not keep the clean pin");
                    using (Bitmap actual = pinned.ExportImage()) EqualPixels(source, actual);
                    Require(!pinned.Visible, "synthetic pin test displayed a window");
                }
            });
            Run("scroll handoff keeps a quick capture without a subscriber", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(-280, -120, 120, 80)))
                {
                    CaptureToolbarButton scroll = (CaptureToolbarButton)Field(quick, "_scrollCapture");
                    Require(scroll.Enabled, "fresh screenshot cannot start scrolling");
                    scroll.ActivateAction();
                    Require(!quick.IsDisposed && quick.SelectionBounds == new Rectangle(-280, -120, 120, 80),
                        "missing subscriber closed or changed the screenshot");
                    using (Bitmap actual = quick.Canvas.ExportImage()) EqualPixels(source, actual);
                }
            });
            Run("scroll handoff commits pending negative-coordinate selection before closing", delegate
            {
                Rectangle desktopBounds = new Rectangle(-400, -200, 500, 350);
                Rectangle initial = new Rectangle(-300, -150, 160, 100);
                Rectangle expected = new Rectangle(-300, -150, 200, 130);
                using (Bitmap desktop = Pattern(500, 350))
                using (Bitmap crop = desktop.Clone(new Rectangle(100, 50, 160, 100), PixelFormat.Format32bppArgb))
                using (QuickCaptureForm quick = new QuickCaptureForm(crop, initial, desktop, desktopBounds))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    handle = ((Control)Field(quick, "_selectionFrame")).Handle;
                    CaptureToolbarForm toolbar = (CaptureToolbarForm)Field(quick, "_toolbar");
                    CaptureToolbarButton scroll = (CaptureToolbarButton)Field(quick, "_scrollCapture");
                    int count = 0;
                    quick.ScrollRequested += delegate(Rectangle selection)
                    {
                        count++;
                        Require(quick.IsDisposed && toolbar.IsDisposed, "handoff preceded quick-session closure");
                        Require(selection == expected, "handoff lost the latest physical selection: " + selection);
                    };
                    Type edges = typeof(QuickCaptureForm).GetNestedType("SelectionEdges", BindingFlags.NonPublic);
                    Invoke(quick, "BeginSelectionResize", Enum.ToObject(edges, 12), new Point(-140, -50));
                    Invoke(quick, "UpdateSelectionResize", new Point(-100, -20));
                    Require(quick.SelectionBounds == initial && (bool)Field(quick, "_resizeBoundsPending"),
                        "fixture did not leave a pending adjustment");
                    scroll.ActivateAction();
                    scroll.ActivateAction();
                    Require(count == 1, "scroll handoff was lost or delivered twice");
                    Require(desktop.GetPixel(1, 1).A == 255, "handoff disposed the caller's original image");
                }
            });
            Run("scroll handoff blocks both annotations and redo until explicitly cleared", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(-280, 120, 120, 80)))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    CaptureToolbarButton scroll = (CaptureToolbarButton)Field(quick, "_scrollCapture");
                    int count = 0;
                    quick.ScrollRequested += delegate(Rectangle bounds) { count++; };
                    Stroke(quick.Canvas);
                    using (Bitmap annotated = quick.Canvas.ExportImage())
                    {
                        Require(!scroll.Enabled && scroll.Text.Contains("清除") &&
                            scroll.AccessibleDescription.Contains("还原"), "annotation guard lacks disable explanation");
                        scroll.ActivateAction();
                        Invoke(quick, "RequestScrollCapture");
                        Require(count == 0 && !quick.IsDisposed && quick.Canvas.CanUndo,
                            "scroll handoff discarded annotations");
                        using (Bitmap actual = quick.Canvas.ExportImage()) EqualPixels(annotated, actual);
                        quick.Canvas.Undo();
                        Require(!scroll.Enabled && !quick.Canvas.CanUndo && quick.Canvas.CanRedo,
                            "redo-only state enabled scrolling");
                        Invoke(quick, "RequestScrollCapture");
                        Require(count == 0 && !quick.IsDisposed && quick.Canvas.CanRedo,
                            "scroll handoff discarded redo history");
                        quick.Canvas.Redo();
                        using (Bitmap actual = quick.Canvas.ExportImage()) EqualPixels(annotated, actual);
                    }
                    quick.Canvas.ResetAnnotations();
                    Require(scroll.Enabled && !quick.Canvas.CanUndo && !quick.Canvas.CanRedo,
                        "explicit clear did not enable scrolling");
                    scroll.ActivateAction();
                    Require(count == 1 && quick.IsDisposed, "clean handoff did not close and dispatch");
                }
            });
            Run("scroll command stays available with its safety state in narrow-toolbar overflow", delegate
            {
                using (Bitmap source = Pattern(120, 80))
                using (QuickCaptureForm quick = new QuickCaptureForm(source, new Rectangle(-50, -30, 120, 80)))
                {
                    IntPtr handle = quick.Handle;
                    handle = quick.Canvas.Handle;
                    CaptureToolbarForm toolbar = (CaptureToolbarForm)Field(quick, "_toolbar");
                    toolbar.FitWithin(new Rectangle(0, 0, 80, 300), 2);
                    Invoke(toolbar, "PopulateOverflowMenu");
                    ContextMenuStrip menu = (ContextMenuStrip)Field(toolbar, "_overflowMenu");
                    ToolStripItem item = FindScrollMenuItem(menu);
                    Require(item != null && item.Enabled && item.Text.Contains("向下滚动"),
                        "narrow toolbar lost scroll command or description");
                    Stroke(quick.Canvas);
                    Invoke(toolbar, "PopulateOverflowMenu");
                    item = FindScrollMenuItem(menu);
                    Require(item != null && !item.Enabled && item.Text.Contains("清除"),
                        "overflow bypassed the annotation guard");
                    quick.Canvas.ResetAnnotations();
                    Invoke(toolbar, "PopulateOverflowMenu");
                    item = FindScrollMenuItem(menu);
                    int count = 0;
                    quick.ScrollRequested += delegate(Rectangle bounds)
                    { Require(quick.IsDisposed, "overflow handoff preceded closure"); count++; };
                    item.PerformClick();
                    Require(count == 1, "overflow command did not dispatch");
                }
            });
            Console.WriteLine("RESULT: " + passed + " quick capture groups passed");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    private static void Run(string name, Action action)
    { action(); passed++; Console.WriteLine("PASS " + name); }
    private static ToolStripItem FindScrollMenuItem(ContextMenuStrip menu)
    {
        foreach (ToolStripItem item in menu.Items)
            if (item.Text.StartsWith("滚动长截图")) return item;
        return null;
    }
    private static ToolStripItem FindMenuCommand(ContextMenuStrip menu, string command)
    {
        foreach (ToolStripItem item in menu.Items)
            if (item.Text.StartsWith(command)) return item;
        return null;
    }
    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static AnnotationCanvas Canvas(Bitmap source)
    {
        AnnotationCanvas canvas = new AnnotationCanvas { CompactMode = true, Size = source.Size };
        canvas.SetImage(source);
        return canvas;
    }
    private static void Stroke(AnnotationCanvas canvas)
    {
        canvas.Tool = DrawingTool.Pen;
        canvas.StrokeColor = Color.Red;
        canvas.StrokeWidth = 6;
        Mouse(canvas, "OnMouseDown", new Point(10, 10));
        Mouse(canvas, "OnMouseMove", new Point(30, 35));
        Mouse(canvas, "OnMouseMove", new Point(60, 15));
        Mouse(canvas, "OnMouseUp", new Point(70, 20));
    }
    private static void Mouse(AnnotationCanvas canvas, string name, Point point)
    {
        typeof(AnnotationCanvas).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) });
    }
    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    private static object Invoke(object target, string name, params object[] args)
    {
        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    }
    private static Bitmap Pattern(int width, int height)
    {
        Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, x * 7 % 256, y * 11 % 256, (x + y) * 13 % 256));
        return bitmap;
    }
    private static void EqualPixels(Bitmap first, Bitmap second)
    {
        Require(first.Size == second.Size, "image size");
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                Require(first.GetPixel(x, y) == second.GetPixel(x, y), "pixel at " + x + "," + y);
    }
}
