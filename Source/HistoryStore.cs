using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QingJie
{
    public enum HistoryCategory { Screenshot, Board }

    public sealed class HistoryEntry
    {
        public string Path { get; private set; }
        public string Name { get { return System.IO.Path.GetFileNameWithoutExtension(Path); } }
        public HistoryCategory Category { get; private set; }
        public DateTime CreatedUtc { get; private set; }

        internal HistoryEntry(string path, HistoryCategory category, DateTime createdUtc)
        {
            Path = path;
            Category = category;
            CreatedUtc = createdUtc;
        }
    }

    /// <summary>
    /// Owns each queued snapshot. Dispose rejects new saves but lets accepted saves
    /// finish; FlushAsync lets the host await those saves before exiting.
    /// </summary>
    public sealed class HistoryStore : IDisposable
    {
        private readonly object gate = new object();
        private Task tail = Task.FromResult(0);
        private bool disposed;

        public string RootDirectory { get; private set; }
        public string ScreenshotDirectory { get { return System.IO.Path.Combine(RootDirectory, "截图"); } }
        public string BoardDirectory { get { return System.IO.Path.Combine(RootDirectory, "画布"); } }

        public HistoryStore(string rootDirectory)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("历史记录目录不能为空。", "rootDirectory");
            RootDirectory = System.IO.Path.GetFullPath(rootDirectory);
        }

        public Task<string> SaveAsync(Bitmap image, HistoryCategory category)
        {
            if (image == null) throw new ArgumentNullException("image");
            string directory = GetDirectory(category);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("HistoryStore");
                // Clone before returning: the caller may immediately edit or
                // dispose its bitmap while PNG compression happens in the queue.
                Bitmap snapshot = image.Clone(
                    new Rectangle(Point.Empty, image.Size), PixelFormat.Format32bppArgb);
                DateTime capturedUtc = DateTime.UtcNow;
                Task<string> result;
                try
                {
                    result = tail.ContinueWith(delegate(Task previous)
                    {
                        // Observe earlier failures without blocking later saves.
                        if (previous.IsFaulted) { AggregateException ignored = previous.Exception; }
                        using (snapshot)
                            return SaveSnapshot(snapshot, directory, capturedUtc);
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
                catch { snapshot.Dispose(); throw; }
                tail = result;
                return result;
            }
        }

        public Task FlushAsync()
        {
            lock (gate) return tail;
        }

        public List<HistoryEntry> List(HistoryCategory? category = null)
        {
            List<HistoryEntry> result = new List<HistoryEntry>();
            if (category.HasValue) ReadEntries(GetDirectory(category.Value), category.Value, result);
            else
            {
                ReadEntries(ScreenshotDirectory, HistoryCategory.Screenshot, result);
                ReadEntries(BoardDirectory, HistoryCategory.Board, result);
            }
            result.Sort(delegate(HistoryEntry left, HistoryEntry right)
            {
                int order = right.CreatedUtc.CompareTo(left.CreatedUtc);
                return order == 0 ? StringComparer.OrdinalIgnoreCase.Compare(right.Path, left.Path) : order;
            });
            return result;
        }

        public Bitmap LoadImage(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("图片路径不能为空。", "path");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (Bitmap decoded = new Bitmap(stream))
            {
                if ((long)decoded.Width * decoded.Height > 80000000L)
                    throw new InvalidOperationException("这张图片超过 8000 万像素，无法载入历史预览。");
                return decoded.Clone(new Rectangle(Point.Empty, decoded.Size), PixelFormat.Format32bppArgb);
            }
        }

        private string GetDirectory(HistoryCategory category)
        {
            switch (category)
            {
                case HistoryCategory.Screenshot: return ScreenshotDirectory;
                case HistoryCategory.Board: return BoardDirectory;
                default: throw new ArgumentOutOfRangeException("category");
            }
        }

        private static void ReadEntries(string directory, HistoryCategory category, List<HistoryEntry> result)
        {
            if (File.Exists(directory)) throw new IOException("历史目录被同名文件占用：" + directory);
            if (!Directory.Exists(directory)) return;
            // Metadata only: opening this list never decodes every PNG.
            foreach (string path in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists) continue;
                result.Add(new HistoryEntry(file.FullName, category, file.LastWriteTimeUtc));
            }
        }

        private static string SaveSnapshot(Bitmap snapshot, string directory, DateTime capturedUtc)
        {
            Directory.CreateDirectory(directory);
            string name = capturedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff") +
                "-" + Guid.NewGuid().ToString("N") + ".png";
            string destination = System.IO.Path.Combine(directory, name);
            string temporary = destination + ".writing";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    snapshot.Save(stream, ImageFormat.Png);
                    stream.Flush(true);
                }
                File.SetLastWriteTimeUtc(temporary, capturedUtc);
                // Same-directory move publishes only a complete PNG. A failed
                // save cannot leave a partial file in the visible history list.
                File.Move(temporary, destination);
                return destination;
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
                throw;
            }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
        }
    }
}
