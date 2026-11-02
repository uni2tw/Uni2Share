using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public interface ITranscodeService
    {
        /// <summary>有設定 ffmpeg 且檔案存在</summary>
        bool Enabled { get; }
        /// <summary>這個檔案是不是要轉檔的來源（目前只有 .flac）</summary>
        bool IsSource(string fullPath);
        /// <summary>把一個資料夾裡的 FLAC 依傳入順序排進背景轉檔（已轉好或已在佇列的略過）</summary>
        void EnqueueFolder(IEnumerable<string> fullPaths);
        /// <summary>ready / pending / failed；沒啟用或不是 FLAC 為 null。pending 會順便保證已排進佇列</summary>
        string GetState(string fullPath);
        /// <summary>轉好的 MP3 快取檔路徑；還沒轉好為 null（並更新它的使用時間，讓快取清理知道最近播過）</summary>
        string GetReadyFile(string fullPath);
        /// <summary>把這個檔案移到佇列最前面（使用者點了還沒轉好的那首）</summary>
        void Prioritize(string fullPath);
        /// <summary>轉檔快取目前佔用的位元組（沒啟用為 0；最多 30 秒更新一次，不會每次都掃資料夾）</summary>
        long CacheBytes { get; }
        /// <summary>轉檔快取的上限位元組（config 的 maxCacheGB）</summary>
        long CacheLimitBytes { get; }
    }

    /// <summary>
    /// FLAC → MP3（預設 192 kbps）：呼叫伺服器上的 ffmpeg，轉好的檔案放在快取資料夾，之後用一般的 Range 串流播放。
    /// 快取檔名由「路徑＋大小＋修改時間」雜湊而來，原檔變了就會重轉；先寫成 .tmp 再改名，所以不會播到一半的檔案。
    /// 狀態只放在記憶體：重新啟動後，已經存在的快取檔仍是 ready，失敗的會重試。
    /// </summary>
    public class TranscodeService : ITranscodeService
    {
        public const string StateReady = "ready";
        public const string StatePending = "pending";
        public const string StateFailed = "failed";

        static ILogger logger = LogManager.GetCurrentClassLogger();
        const int ProcessTimeoutMinutes = 15;
        readonly SystemConfig config = Ioc.GetConfig();
        readonly object gate = new object();
        readonly LinkedList<string> queue = new LinkedList<string>();
        readonly HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 佇列中或轉檔中（key = 完整路徑）
        readonly Dictionary<string, DateTime> failed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly List<Thread> workers = new List<Thread>();
        bool started;
        string cacheFolder;

        public bool Enabled
        {
            get
            {
                TranscodeConfig c = config.Transcode;
                return c != null && string.IsNullOrWhiteSpace(c.Ffmpeg) == false && File.Exists(c.Ffmpeg);
            }
        }

        public bool IsSource(string fullPath)
        {
            return string.Equals(Path.GetExtension(fullPath), ".flac", StringComparison.OrdinalIgnoreCase);
        }

        public long CacheLimitBytes
        {
            get { return (long)(Math.Max(0.1, config.Transcode.MaxCacheGB) * 1024 * 1024 * 1024); }
        }

        readonly object cacheSizeLock = new object();
        long cacheBytes;
        DateTime cacheBytesAt = DateTime.MinValue;

        public long CacheBytes
        {
            get
            {
                if (Enabled == false)
                {
                    return 0;
                }
                lock (cacheSizeLock)
                {
                    if ((DateTime.Now - cacheBytesAt).TotalSeconds > 30)
                    {
                        try
                        {
                            cacheBytes = new DirectoryInfo(CacheFolder()).GetFiles("*.mp3").Sum(f => f.Length);
                        }
                        catch
                        {
                            cacheBytes = 0;
                        }
                        cacheBytesAt = DateTime.Now;
                    }
                    return cacheBytes;
                }
            }
        }

        string CacheFolder()
        {
            if (cacheFolder == null)
            {
                string folder = config.Transcode.CacheFolder;
                cacheFolder = string.IsNullOrWhiteSpace(folder) ? Helper.GetRelativePath("data", "transcode") : folder;
                Directory.CreateDirectory(cacheFolder);
            }
            return cacheFolder;
        }

        string CachePathOf(FileInfo fi)
        {
            string key = fi.FullName.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + config.Transcode.Bitrate;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(CacheFolder(), BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant() + ".mp3");
            }
        }

        void EnsureStarted()
        {
            if (started)
            {
                return;
            }
            started = true;
            try
            {
                foreach (string tmp in Directory.GetFiles(CacheFolder(), "*.tmp"))
                {
                    try { File.Delete(tmp); } catch { }        // 上次中途結束留下的半成品
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "清理轉檔暫存檔失敗");
            }
            int n = Math.Max(1, Math.Min(config.Transcode.Concurrency, 8));
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(WorkLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "TranscodeWorker" + i };
                workers.Add(t);
                t.Start();
            }
            logger.Info("FLAC 轉 MP3 已啟用：ffmpeg={0}，{1} kbps，快取 {2}（上限 {3} GB），同時轉 {4} 個", config.Transcode.Ffmpeg, config.Transcode.Bitrate, CacheFolder(), config.Transcode.MaxCacheGB, n);
        }

        public void EnqueueFolder(IEnumerable<string> fullPaths)
        {
            if (Enabled == false)
            {
                return;
            }
            lock (gate)
            {
                EnsureStarted();
                bool added = false;
                foreach (string path in fullPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    added |= EnqueueLocked(path, false);
                }
                if (added)
                {
                    Monitor.PulseAll(gate);
                }
            }
        }

        // 已轉好、已在佇列、剛失敗過（10 分鐘內）的都略過；回傳是否真的新增
        bool EnqueueLocked(string path, bool front)
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Exists == false)
            {
                return false;
            }
            if (File.Exists(CachePathOf(fi)))
            {
                return false;
            }
            DateTime when;
            if (failed.TryGetValue(path, out when))
            {
                if ((DateTime.Now - when).TotalMinutes < 10)
                {
                    return false;
                }
                failed.Remove(path);
            }
            if (queued.Contains(path))
            {
                if (front && queue.Remove(path))
                {
                    queue.AddFirst(path);
                }
                return false;
            }
            queued.Add(path);
            if (front)
            {
                queue.AddFirst(path);
            }
            else
            {
                queue.AddLast(path);
            }
            return true;
        }

        public string GetState(string fullPath)
        {
            if (Enabled == false || IsSource(fullPath) == false)
            {
                return null;
            }
            FileInfo fi = new FileInfo(fullPath);
            if (fi.Exists == false)
            {
                return null;
            }
            if (File.Exists(CachePathOf(fi)))
            {
                return StateReady;
            }
            lock (gate)
            {
                if (queued.Contains(fullPath))
                {
                    return StatePending;
                }
                DateTime when;
                if (failed.TryGetValue(fullPath, out when) && (DateTime.Now - when).TotalMinutes < 10)
                {
                    return StateFailed;
                }
                EnsureStarted();
                if (EnqueueLocked(fullPath, false))
                {
                    Monitor.PulseAll(gate);
                }
                return StatePending;
            }
        }

        public string GetReadyFile(string fullPath)
        {
            if (Enabled == false || IsSource(fullPath) == false)
            {
                return null;
            }
            FileInfo fi = new FileInfo(fullPath);
            if (fi.Exists == false)
            {
                return null;
            }
            string cache = CachePathOf(fi);
            if (File.Exists(cache) == false)
            {
                return null;
            }
            try { File.SetLastWriteTimeUtc(cache, DateTime.UtcNow); } catch { }        // 當作「最近使用」
            return cache;
        }

        public void Prioritize(string fullPath)
        {
            if (Enabled == false || IsSource(fullPath) == false)
            {
                return;
            }
            lock (gate)
            {
                EnsureStarted();
                if (EnqueueLocked(fullPath, true))
                {
                    Monitor.PulseAll(gate);
                }
            }
        }

        void WorkLoop()
        {
            while (true)
            {
                string path;
                lock (gate)
                {
                    while (queue.Count == 0)
                    {
                        Monitor.Wait(gate);
                    }
                    path = queue.First.Value;
                    queue.RemoveFirst();
                }
                bool ok = false;
                try
                {
                    ok = Convert(path);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "FLAC 轉檔失敗 {0}", path);
                }
                lock (gate)
                {
                    queued.Remove(path);
                    if (ok == false)
                    {
                        failed[path] = DateTime.Now;
                    }
                }
                if (ok)
                {
                    TrimCache();
                }
            }
        }

        bool Convert(string path)
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Exists == false)
            {
                return false;
            }
            string final = CachePathOf(fi);
            if (File.Exists(final))
            {
                return true;
            }
            string tmp = final + ".tmp";
            var watch = Stopwatch.StartNew();
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = config.Transcode.Ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            foreach (string a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-vn", "-map_metadata", "0",
                "-c:a", "libmp3lame", "-b:a", Math.Max(32, config.Transcode.Bitrate) + "k", "-id3v2_version", "3", "-f", "mp3", tmp })
            {
                psi.ArgumentList.Add(a);
            }
            using (Process p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                // 兩個輸出都要有人讀，不然 ffmpeg 寫滿緩衝區會卡住
                StringBuilder err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 2000) { err.AppendLine(e.Data); } };
                p.OutputDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (p.WaitForExit(ProcessTimeoutMinutes * 60 * 1000) == false)
                {
                    try { p.Kill(); } catch { }
                    try { File.Delete(tmp); } catch { }
                    logger.Warn("FLAC 轉檔逾時（{0} 分鐘）已中止 {1}", ProcessTimeoutMinutes, path);
                    return false;
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || File.Exists(tmp) == false || new FileInfo(tmp).Length == 0)
                {
                    try { File.Delete(tmp); } catch { }
                    logger.Warn("FLAC 轉檔失敗 {0}：ffmpeg 結束碼 {1} {2}", path, p.ExitCode, err.ToString().Trim());
                    return false;
                }
            }
            File.Move(tmp, final);
            logger.Info("FLAC 轉檔完成 {0}：{1} -> {2}，耗時 {3}ms", path,
                Helper.GetReadableByteSize(fi.Length), Helper.GetReadableByteSize(new FileInfo(final).Length), watch.ElapsedMilliseconds);
            return true;
        }

        // 超過上限就刪最久沒用的（用修改時間當「最近使用」，播放時會更新）
        void TrimCache()
        {
            try
            {
                long limit = CacheLimitBytes;
                List<FileInfo> files = new DirectoryInfo(CacheFolder()).GetFiles("*.mp3").OrderBy(f => f.LastWriteTimeUtc).ToList();
                long total = files.Sum(f => f.Length);
                int removed = 0;
                foreach (FileInfo f in files)
                {
                    if (total <= limit || files.Count - removed <= 1)
                    {
                        break;
                    }
                    try
                    {
                        f.Delete();
                        total -= f.Length;
                        removed++;
                    }
                    catch { }
                }
                if (removed > 0)
                {
                    logger.Info("轉檔快取超過上限，已刪除最久沒用的 {0} 個檔案", removed);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "清理轉檔快取失敗");
            }
        }
    }
}
