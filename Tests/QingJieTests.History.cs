using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using QingJie;

internal static partial class QingJieTests
{
    private static void RunHistoryTests()
    {
        Run("history snapshots own exact PNG pixels and release input and file handles", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                using (HistoryStore store = new HistoryStore(directory))
                using (Bitmap expected = Pattern(73, 49))
                {
                    expected.SetPixel(2, 3, Color.FromArgb(7, 189, 61, 217));
                    Bitmap input = expected.Clone(new Rectangle(Point.Empty, expected.Size), PixelFormat.Format32bppArgb);
                    Task<string> saving = store.SaveAsync(input, HistoryCategory.Screenshot);
                    input.SetPixel(8, 9, Color.Black);
                    input.Dispose();
                    string path = saving.GetAwaiter().GetResult();
                    Equal(store.ScreenshotDirectory, Path.GetDirectoryName(path));
                    byte[] bytes = File.ReadAllBytes(path);
                    Assert(bytes.Length > 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71,
                        "The history entry is not a PNG.");
                    using (Bitmap loaded = store.LoadImage(path))
                    {
                        string moved = Path.Combine(directory, "renamed.png");
                        File.Move(path, moved);
                        File.Delete(moved);
                        FastPixelsEqual(expected, loaded, "independent history PNG");
                    }
                }
            });
        });

        Run("history categories survive a new store and ignore incomplete temporary PNG files", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                string screenshot, board;
                using (HistoryStore first = new HistoryStore(directory))
                using (Bitmap image = Pattern(40, 28))
                {
                    screenshot = first.SaveAsync(image, HistoryCategory.Screenshot).GetAwaiter().GetResult();
                    board = first.SaveAsync(image, HistoryCategory.Board).GetAwaiter().GetResult();
                }
                DateTime earlier = DateTime.UtcNow.AddMinutes(-2);
                File.SetLastWriteTimeUtc(screenshot, earlier);
                File.SetLastWriteTimeUtc(board, earlier.AddMinutes(1));
                File.WriteAllText(screenshot + ".writing", "partial");
                using (HistoryStore restarted = new HistoryStore(directory))
                {
                    List<HistoryEntry> entries = restarted.List();
                    Equal(2, entries.Count);
                    Equal(board, entries[0].Path);
                    Equal(HistoryCategory.Board, entries[0].Category);
                    Equal(screenshot, entries[1].Path);
                    Equal(1, restarted.List(HistoryCategory.Screenshot).Count);
                    Equal(1, restarted.List(HistoryCategory.Board).Count);
                    Equal("截图", Path.GetFileName(restarted.ScreenshotDirectory));
                    Equal("画布", Path.GetFileName(restarted.BoardDirectory));
                }
            });
        });

        Run("history write failures are explicit and do not poison later queued saves", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                string blockedRoot = Path.Combine(directory, "blocked");
                File.WriteAllText(blockedRoot, "This test file deliberately obstructs the directory.");
                using (HistoryStore store = new HistoryStore(blockedRoot))
                using (Bitmap image = Pattern(30, 20))
                {
                    bool failed = false;
                    try { store.SaveAsync(image, HistoryCategory.Screenshot).GetAwaiter().GetResult(); }
                    catch (IOException) { failed = true; }
                    Assert(failed, "An unwritable destination was reported as successful.");
                    File.Delete(blockedRoot);
                    string path = store.SaveAsync(image, HistoryCategory.Board).GetAwaiter().GetResult();
                    Assert(File.Exists(path), "A failed save poisoned the queue.");
                    Equal(1, store.List().Count);
                    Equal(0, Directory.GetFiles(blockedRoot, "*.writing", SearchOption.AllDirectories).Length);
                }
            });
        });

        Run("history queued saves have unique names and accepted saves finish after store disposal", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                HistoryStore store = new HistoryStore(directory);
                List<Task<string>> pending = new List<Task<string>>();
                using (Bitmap input = Pattern(48, 32))
                    for (int i = 0; i < 24; i++)
                        pending.Add(store.SaveAsync(input, i % 2 == 0 ? HistoryCategory.Screenshot : HistoryCategory.Board));
                store.Dispose();
                store.FlushAsync().GetAwaiter().GetResult();
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Task<string> task in pending)
                {
                    string path = task.GetAwaiter().GetResult();
                    Assert(names.Add(path) && File.Exists(path), "A queued snapshot collided or vanished.");
                }
                using (Bitmap input = Pattern(8, 8))
                    Throws<ObjectDisposedException>(delegate { store.SaveAsync(input, HistoryCategory.Screenshot); });
                Equal(24, store.List().Count);
            });
        });

        Run("history invalid input and damaged PNGs fail without keeping a file locked", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                using (HistoryStore store = new HistoryStore(directory))
                using (Bitmap image = Pattern(8, 8))
                {
                    Throws<ArgumentNullException>(delegate { store.SaveAsync(null, HistoryCategory.Screenshot); });
                    Throws<ArgumentOutOfRangeException>(delegate { store.SaveAsync(image, (HistoryCategory)99); });
                    Throws<ArgumentOutOfRangeException>(delegate { store.List((HistoryCategory)99); });
                    Throws<FileNotFoundException>(delegate { using (store.LoadImage(Path.Combine(directory, "missing.png"))) { } });
                    string damaged = Path.Combine(directory, "damaged.png");
                    File.WriteAllText(damaged, "not a PNG");
                    bool rejected = false;
                    try { using (store.LoadImage(damaged)) { } }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected, "A damaged PNG was silently accepted.");
                    File.Delete(damaged);
                }
            });
        });

        Run("history browser routes one editor selection or multiple board paths in display order", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                using (HistoryStore store = new HistoryStore(directory))
                using (HistoryBrowserForm browser = new HistoryBrowserForm(store))
                {
                    HistoryGrid grid = (HistoryGrid)HistoryField(browser, "grid");
                    List<HistoryEntry> entries = HistoryFixtureEntries(directory, 6);
                    grid.SetEntries(entries);
                    string edited = null;
                    string[] added = null;
                    browser.EditRequested += delegate(string path) { edited = path; };
                    browser.AddToBoardRequested += delegate(string[] paths) { added = paths; };
                    HistoryInvoke(grid, "SelectIndex", 1, false, false);
                    HistoryInvoke(browser, "EditSelection");
                    Equal(entries[1].Path, edited);
                    HistoryInvoke(grid, "SelectIndex", 3, false, true);
                    HistoryInvoke(browser, "AddSelection");
                    Equal(3, added.Length);
                    for (int i = 0; i < added.Length; i++) Equal(entries[i + 1].Path, added[i]);
                    edited = null;
                    HistoryInvoke(browser, "EditSelection");
                    Assert(edited == null, "Multiple items were sent to the single-image editor.");
                    HistoryInvoke(grid, "SelectIndex", 2, true, false);
                    Equal(2, grid.SelectedEntries.Count);
                    HistoryInvoke(grid, "SelectIndex", -1, false, false);
                    Equal(0, grid.SelectedEntries.Count);
                    Assert(!((Button)HistoryField(browser, "editButton")).Enabled &&
                        !((Button)HistoryField(browser, "addButton")).Enabled,
                        "Empty history selection left an import action enabled.");
                }
            });
        });

        Run("history thumbnails load only visible entries and stale or disposed work is safely released", delegate
        {
            InHistoryDirectory(delegate(string directory)
            {
                using (HistoryStore store = new HistoryStore(directory))
                {
                    List<HistoryEntry> entries = HistoryFixtureEntries(directory, 1000);
                    using (Bitmap source = Pattern(1000, 700))
                    {
                        string path = store.SaveAsync(source, HistoryCategory.Screenshot).GetAwaiter().GetResult();
                        entries[0] = store.List()[0];
                    }
                    HistoryGrid grid = new HistoryGrid(store) { Size = new Size(680, 420) };
                    try
                    {
                        IntPtr handle = grid.Handle;
                        grid.SetEntries(entries);
                        Equal(0, ((IDictionary)HistoryField(grid, "thumbnails")).Count);
                        PaintHistoryGrid(grid);
                        PumpHistoryGrid(grid);
                        int decoded = ((IDictionary)HistoryField(grid, "thumbnails")).Count;
                        Assert(decoded > 0 && decoded < 30,
                            "Opening 1,000 entries decoded offscreen history rather than the visible page.");
                        List<HistoryEntry> replacement = HistoryFixtureEntries(directory, 2);
                        replacement[0] = new HistoryEntry(Path.Combine(directory, "replacement.png"),
                            HistoryCategory.Board, DateTime.UtcNow);
                        grid.SetEntries(replacement);
                        PaintHistoryGrid(grid);
                        PumpHistoryGrid(grid);
                        IDictionary thumbnails = (IDictionary)HistoryField(grid, "thumbnails");
                        Assert(!thumbnails.Contains(entries[0].Path), "A stale thumbnail entered the replacement list.");
                        grid.SetEntries(entries);
                        PaintHistoryGrid(grid);
                        grid.Dispose();
                        Stopwatch wait = Stopwatch.StartNew();
                        while ((int)HistoryField(grid, "activeWorkers") != 0 && wait.ElapsedMilliseconds < 5000)
                            Thread.Sleep(1);
                        Equal(0, (int)HistoryField(grid, "activeWorkers"));
                        Equal(0, ((ICollection)HistoryField(grid, "completed")).Count);
                        Equal(0, ((IDictionary)HistoryField(grid, "thumbnails")).Count);
                    }
                    finally { grid.Dispose(); }
                }
            });
        });
    }

    private static void InHistoryDirectory(Action<string> action)
    {
        string temporary = Environment.GetEnvironmentVariable("QINGJIE_TEST_TEMP");
        if (String.IsNullOrWhiteSpace(temporary)) temporary = Path.GetTempPath();
        string root = Path.GetFullPath(Path.Combine(temporary,
            "QingJie-History-Test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string expectedParent = Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("QingJie-History-Test-", StringComparison.Ordinal))
                throw new InvalidOperationException("History test cleanup escaped its temporary directory.");
            Directory.Delete(root, true);
        }
    }

    private static List<HistoryEntry> HistoryFixtureEntries(string directory, int count)
    {
        List<HistoryEntry> entries = new List<HistoryEntry>();
        for (int i = 0; i < count; i++)
            entries.Add(new HistoryEntry(Path.Combine(directory, "fixture-" + i + ".png"),
                i % 2 == 0 ? HistoryCategory.Screenshot : HistoryCategory.Board, DateTime.UtcNow.AddSeconds(-i)));
        return entries;
    }

    private static object HistoryField(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
    }

    private static void HistoryInvoke(object instance, string name, params object[] arguments)
    {
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments);
    }

    private static void PaintHistoryGrid(HistoryGrid grid)
    {
        using (Bitmap image = new Bitmap(grid.Width, grid.Height, PixelFormat.Format32bppPArgb))
        using (Graphics graphics = Graphics.FromImage(image))
            HistoryInvoke(grid, "OnPaint", new PaintEventArgs(graphics, grid.ClientRectangle));
    }

    private static void PumpHistoryGrid(HistoryGrid grid)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            HistoryInvoke(grid, "DrainThumbnails");
            if ((int)HistoryField(grid, "activeWorkers") == 0 &&
                ((ICollection)HistoryField(grid, "waiting")).Count == 0 &&
                ((ICollection)HistoryField(grid, "completed")).Count == 0) return;
            Thread.Sleep(1);
        }
        throw new Exception("History thumbnails did not complete within the bounded timeout.");
    }
}
