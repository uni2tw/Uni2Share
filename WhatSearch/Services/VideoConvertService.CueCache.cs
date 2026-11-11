using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public class CueAlbumTrack
    {
        public int Number { get; set; }
        public string Title { get; set; }
        /// <summary>只在跟專輯演出者不同時才有值</summary>
        public string Artist { get; set; }
        public bool Ready { get; set; }
        /// <summary>播放網址；還沒轉好時為 null</summary>
        public string Url { get; set; }
    }

    public class CueAlbumResult
    {
        public const string StateReady = "ready";
        public const string StateQueued = "queued";
        public const string StateRunning = "running";
        public const string StateFailed = "failed";

        public string Key { get; set; }
        public string Cue { get; set; }
        public string Album { get; set; }
        public string Artist { get; set; }
        public string Year { get; set; }
        /// <summary>ready（全部轉好）／queued（排隊中）／running（轉換中）／failed</summary>
        public string State { get; set; }
        /// <summary>排隊中時，前面還有幾個工作（含正在轉的）</summary>
        public int Ahead { get; set; }
        public int ReadyCount { get; set; }
        public int Total { get; set; }
        /// <summary>失敗原因</summary>
        public string Message { get; set; }
        /// <summary>分享資料夾裡已經有拆好的資料夾時，那個資料夾的 id（前端直接進去，不用暫存）</summary>
        public string RedirectFolderId { get; set; }
        public List<CueAlbumTrack> Tracks { get; set; } = new List<CueAlbumTrack>();
    }

    /// <summary>
    /// 一般會員點 CUE／FLAC 時的即時拆軌：輸出放在拆軌暫存（預設 data\cuecache\＜key＞\001.mp3…），不寫進分享資料夾。
    /// key 由「CUE 與音檔的路徑、大小、修改時間、位元率」雜湊而來，檔案變了就重拆。工作和管理者的轉檔共用同一條佇列，所以同一時間只會有一個在轉。
    /// 暫存超過容量上限，或超過保留天數沒被開過就整張專輯刪掉（用資料夾的修改時間當「最近使用」，開啟與播放都會更新）。
    /// </summary>
    public partial class VideoConvertService
    {
        const int MaxCueCachePending = 5;
        static readonly Regex CueKeyRegex = new Regex("^[0-9a-f]{40}$", RegexOptions.Compiled);
        int cueTrimStarted;
        string cueCacheRoot;

        string CueCacheRoot()
        {
            if (cueCacheRoot == null)
            {
                string folder = config.Transcode.CueCacheFolder;
                string root = string.IsNullOrWhiteSpace(folder) ? Helper.GetRelativePath("data", "cuecache") : folder;
                Directory.CreateDirectory(root);
                cueCacheRoot = root;
            }
            return cueCacheRoot;
        }

        static string CueCacheTrackName(int number)
        {
            return number.ToString("D3", CultureInfo.InvariantCulture) + ".mp3";
        }

        static bool IsActive(ConvertJob j)
        {
            return j.State == ConvertJob.StatePending || j.State == ConvertJob.StateRunning;
        }

        static void TouchCueDir(string dir)
        {
            try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch { }
        }

        string CueCacheKey(string cuePath, CuePlan plan)
        {
            FileInfo cue = new FileInfo(cuePath);
            StringBuilder sb = new StringBuilder();
            sb.Append(cue.FullName.ToLowerInvariant()).Append('|').Append(cue.Length).Append('|').Append(cue.LastWriteTimeUtc.Ticks);
            foreach (string audio in plan.Tracks.Select(t => t.Audio).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FileInfo f = new FileInfo(audio);
                sb.Append('|').Append(f.FullName.ToLowerInvariant()).Append(':').Append(f.Length).Append(':').Append(f.LastWriteTimeUtc.Ticks);
            }
            sb.Append("|b").Append(plan.Bitrate);
            using (SHA1 sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>點的是 CUE 就用它；點的是音檔就找同資料夾裡「同名」的 CUE，沒有再找 FILE 指到這個音檔的 CUE。找不到回傳 null</summary>
        string ResolveCuePath(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || File.Exists(path) == false)
            {
                error = "找不到這個檔案";
                return null;
            }
            if (string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            if (CueAudioExts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) == false)
            {
                error = "這不是 CUE 或可拆軌的音檔";
                return null;
            }
            List<FileInfo> cues;
            try
            {
                cues = new DirectoryInfo(Path.GetDirectoryName(path)).EnumerateFiles()
                    .Where(f => string.Equals(f.Extension, ".cue", StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "尋找音檔的 CUE 失敗 {0}", path);
                error = "找不到對應的 CUE";
                return null;
            }
            string baseName = Path.GetFileNameWithoutExtension(path);
            FileInfo same = cues.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), baseName, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                return same.FullName;
            }
            foreach (FileInfo f in cues)
            {
                string ignore;
                CuePlan plan = BuildCuePlan(f.FullName, out ignore);
                if (plan != null && plan.Tracks.Any(t => string.Equals(t.Audio, path, StringComparison.OrdinalIgnoreCase)))
                {
                    return f.FullName;
                }
            }
            error = "找不到對應的 CUE";
            return null;
        }

        public CueAlbumResult OpenCueAlbum(string path, string by, out string error)
        {
            string cuePath = ResolveCuePath(path, out error);
            if (cuePath == null)
            {
                return null;
            }
            CuePlan plan = BuildCuePlan(cuePath, out error);
            if (plan == null)
            {
                return null;
            }
            CueAlbumResult result = new CueAlbumResult
            {
                Cue = Path.GetFileName(cuePath),
                Album = plan.Album,
                Artist = plan.Artist,
                Year = plan.Year,
                Total = plan.Tracks.Count
            };
            // 管理者已經把它拆在分享資料夾了：直接進那個資料夾，不用再轉
            if (plan.ExistingCount >= plan.Tracks.Count)
            {
                result.State = CueAlbumResult.StateReady;
                result.RedirectFolderId = fimgr.GetIdByFilePath(plan.OutputDir);
                result.ReadyCount = result.Total;
                return result;
            }
            if (Interlocked.Exchange(ref cueTrimStarted, 1) == 0)
            {
                Task.Run(() => TrimCueCache());        // 啟動後第一次用到時，順便清掉過期的暫存
            }
            string key = CueCacheKey(cuePath, plan);
            string dir = Path.Combine(CueCacheRoot(), key);
            result.Key = key;
            string albumArtist = plan.Artist ?? string.Empty;
            foreach (CuePlanTrack t in plan.Tracks)
            {
                bool ready = File.Exists(Path.Combine(dir, CueCacheTrackName(t.Number)));
                result.Tracks.Add(new CueAlbumTrack
                {
                    Number = t.Number,
                    Title = t.Title,
                    Artist = (string.IsNullOrWhiteSpace(t.Performer) || string.Equals(t.Performer, albumArtist, StringComparison.Ordinal)) ? null : t.Performer,
                    Ready = ready,
                    Url = ready ? "/api/cue/file/" + key + "/" + t.Number.ToString(CultureInfo.InvariantCulture) : null
                });
            }
            result.ReadyCount = result.Tracks.Count(t => t.Ready);
            if (result.ReadyCount >= result.Total)
            {
                result.State = CueAlbumResult.StateReady;
                TouchCueDir(dir);
                return result;
            }
            lock (gate)
            {
                ConvertJob job = jobs.LastOrDefault(j => j.Kind == ConvertJob.KindCueCache && j.CacheKey == key && j.State != ConvertJob.StateCanceled);
                if (job != null && job.State == ConvertJob.StateFailed && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - job.FinishedAt) < 10 * 60 * 1000)
                {
                    result.State = CueAlbumResult.StateFailed;
                    result.Message = job.Message;
                    return result;
                }
                if (job == null || IsActive(job) == false)
                {
                    if (jobs.Any(j => j.Kind == ConvertJob.KindCueCache && IsActive(j) && string.Equals(j.By, by, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = "你已經有一張專輯正在轉換或排隊中，等它完成再開下一張";
                        return null;
                    }
                    if (jobs.Count(j => j.Kind == ConvertJob.KindCueCache && j.State == ConvertJob.StatePending) >= MaxCueCachePending)
                    {
                        error = "目前排隊轉換的專輯比較多，請稍後再試";
                        return null;
                    }
                    job = new ConvertJob
                    {
                        Id = (++nextId).ToString(CultureInfo.InvariantCulture),
                        FileId = fimgr.GetIdByFilePath(cuePath),
                        Name = Path.GetFileName(cuePath),
                        Folder = Path.GetFileName(Path.GetDirectoryName(cuePath)),
                        Kind = ConvertJob.KindCueCache,
                        State = ConvertJob.StatePending,
                        By = by,
                        QueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        FullPath = cuePath,
                        CacheDir = dir,
                        CacheKey = key
                    };
                    jobs.Add(job);
                    TrimFinished();
                    if (worker == null)
                    {
                        worker = new Thread(WorkLoop) { IsBackground = true, Name = "VideoConvertWorker" };
                        worker.Start();
                    }
                    Monitor.PulseAll(gate);
                }
                result.State = job.State == ConvertJob.StateRunning ? CueAlbumResult.StateRunning : CueAlbumResult.StateQueued;
                int index = jobs.IndexOf(job);
                result.Ahead = jobs.Take(Math.Max(0, index)).Count(IsActive);
                TouchCueDir(dir);
                return result;
            }
        }

        public string GetCueCacheFile(string key, int number)
        {
            if (string.IsNullOrEmpty(key) || CueKeyRegex.IsMatch(key) == false || number < 0 || number > 999)
            {
                return null;
            }
            string dir = Path.Combine(CueCacheRoot(), key);
            string file = Path.Combine(dir, CueCacheTrackName(number));
            if (File.Exists(file) == false)
            {
                return null;
            }
            TouchCueDir(dir);
            return file;
        }

        /// <summary>刪掉超過保留天數沒開過的專輯，再依容量上限從最久沒用的刪起；排隊中、轉換中的不刪</summary>
        void TrimCueCache()
        {
            try
            {
                HashSet<string> busy;
                lock (gate)
                {
                    busy = new HashSet<string>(jobs.Where(j => j.Kind == ConvertJob.KindCueCache && IsActive(j)).Select(j => j.CacheKey));
                }
                var dirs = new DirectoryInfo(CueCacheRoot()).GetDirectories()
                    .Select(d => new { Dir = d, Bytes = SafeDirBytes(d), Used = d.LastWriteTimeUtc, Busy = busy.Contains(d.Name) })
                    .OrderBy(x => x.Used).ToList();
                long total = dirs.Sum(x => x.Bytes);
                long limit = (long)(Math.Max(0.1, config.Transcode.CueCacheGB) * 1024 * 1024 * 1024);
                DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, config.Transcode.CueCacheDays));
                int removed = 0;
                foreach (var x in dirs)
                {
                    if (x.Busy || (x.Used >= cutoff && total <= limit))
                    {
                        continue;
                    }
                    try
                    {
                        x.Dir.Delete(true);
                        total -= x.Bytes;
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "刪除拆軌暫存失敗 {0}", x.Dir.FullName);
                    }
                }
                if (removed > 0)
                {
                    logger.Info("拆軌暫存已清理 {0} 張專輯（上限 {1} GB、保留 {2} 天）", removed, config.Transcode.CueCacheGB, config.Transcode.CueCacheDays);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "清理拆軌暫存失敗");
            }
        }

        static long SafeDirBytes(DirectoryInfo d)
        {
            try { return d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }
    }
}
