using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public interface IVideoConvertService
    {
        /// <summary>有設定 ffmpeg 且檔案存在（與 FLAC 轉 MP3 共用同一個設定）</summary>
        bool Enabled { get; }
        /// <summary>被管理者標註「無法播放」的動畫資料夾，以及裡面每個 MKV 的編碼與轉換狀態</summary>
        List<ConvertFolder> ListFolders();
        /// <summary>把一個 MKV 排進轉檔佇列；不合格（不是 MKV、不在標註的動畫資料夾、已在佇列）回傳 null 並說明原因</summary>
        ConvertJob Enqueue(string fullPath, string by, out string error);
        /// <summary>佇列與最近完成的工作（新到舊）</summary>
        List<ConvertJob> GetJobs();
        /// <summary>取消排隊中的工作，或中止轉檔中的 ffmpeg；找不到回傳 false</summary>
        bool Cancel(string jobId);
        /// <summary>清掉已結束（完成、失敗、已取消）的紀錄，排隊中與轉換中的不動；回傳清掉幾筆</summary>
        int ClearFinished();
        /// <summary>讀 CUE 檔，回傳拆軌計畫（專輯、曲目、輸出資料夾、已存在幾首）；不合格回傳 null 並說明原因</summary>
        CuePlan PreviewCue(string cuePath, out string error);
        /// <summary>把一個 CUE 檔排進佇列：依曲目切出 MP3，放到 CUE 旁邊的 Disc N 資料夾；不合格回傳 null 並說明原因</summary>
        ConvertJob EnqueueCue(string cuePath, string by, out string error);
        /// <summary>
        /// 登入會員點 CUE 或它的 FLAC：回傳這張專輯的曲目與轉換進度。曲目拆成 MP3 放進暫存（不動分享資料夾），轉好一首就能播一首；
        /// 還沒轉就排進佇列（同一時間只轉一個工作，每位會員最多一張、等待中的總數有上限）。分享資料夾裡已經有拆好的 Disc 資料夾時，回傳那個資料夾的 id 讓前端直接進去。
        /// path 可以是 CUE 檔或同一個資料夾裡的音檔；沒有成對的 CUE 與音檔回傳 null 並說明原因
        /// </summary>
        CueAlbumResult OpenCueAlbum(string path, string by, out string error);
        /// <summary>拆軌暫存裡某一軌的 MP3 路徑（順便更新使用時間）；不存在回傳 null</summary>
        string GetCueCacheFile(string key, int number);
    }

    public class CuePlanTrack
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        /// <summary>起點與長度（秒）；最後一軌長度為 null（到檔案結尾）</summary>
        public double Start { get; set; }
        public double? Length { get; set; }
        /// <summary>輸出檔已經存在（再執行時會略過）</summary>
        public bool Exists { get; set; }
        internal string Audio;
        internal string Performer;
    }

    public class CuePlan
    {
        public string Cue { get; set; }
        /// <summary>專輯名稱（去掉結尾的 (Disc N)，寫進 MP3 標籤用，多片裝才會歸成同一張專輯）</summary>
        public string Album { get; set; }
        /// <summary>CUE 的 TITLE 原文（含 (Disc N)）；畫面上顯示這個，沒有 TITLE 時退回 Album</summary>
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Year { get; set; }
        public string Genre { get; set; }
        public string Audio { get; set; }
        public string Folder { get; set; }
        public int? Disc { get; set; }
        public int Bitrate { get; set; }
        public int ExistingCount { get; set; }
        public List<CuePlanTrack> Tracks { get; set; } = new List<CuePlanTrack>();
        internal string OutputDir;
    }

    public class ConvertFile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public long Bytes { get; set; }
        public string Size { get; set; }
        /// <summary>影片軌的描述，例如 H.265 Main 10 · 10-bit；讀不到為 null</summary>
        public string Video { get; set; }
        /// <summary>音訊軌的描述，多條以「、」分隔</summary>
        public string Audio { get; set; }
        /// <summary>影片已經是 H.264 8-bit（多數瀏覽器能播），不需要轉換</summary>
        public bool Compatible { get; set; }
        /// <summary>旁邊已經有 .bak 備份（之前轉過）</summary>
        public bool HasBackup { get; set; }
        /// <summary>排隊中／轉檔中／最近失敗；沒有為 null</summary>
        public string State { get; set; }
    }

    public class ConvertFolder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        /// <summary>列表頁網址（/page/分享根/…）</summary>
        public string Url { get; set; }
        public List<ConvertFile> Files { get; set; } = new List<ConvertFile>();
    }

    public class ConvertJob
    {
        public const string StatePending = "pending";
        public const string StateRunning = "running";
        public const string StateDone = "done";
        public const string StateFailed = "failed";
        public const string StateCanceled = "canceled";
        public const string KindMkv = "mkv";
        public const string KindCue = "cue";
        /// <summary>會員點 CUE／FLAC 觸發的拆軌，輸出放拆軌暫存而不是分享資料夾</summary>
        public const string KindCueCache = "cuecache";

        public string Id { get; set; }
        public string FileId { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public string State { get; set; }
        /// <summary>0～100；轉檔中才有意義</summary>
        public int Progress { get; set; }
        /// <summary>失敗原因，或完成時的大小變化</summary>
        public string Message { get; set; }
        public string By { get; set; }
        /// <summary>mkv（影片轉檔）或 cue（CUE 拆軌）</summary>
        public string Kind { get; set; } = KindMkv;
        public long QueuedAt { get; set; }
        public long FinishedAt { get; set; }

        internal string FullPath;
        internal string FolderKey;
        internal string CacheDir;
        internal string CacheKey;
        internal volatile bool CancelRequested;
    }

    /// <summary>
    /// 把被標註「無法播放」的 MKV 轉成瀏覽器能播的格式：影片 H.264 8-bit，音訊 AAC（原本就是 AAC 的直接複製），其餘軌道（字幕、字型附件、章節）原樣保留，輸出仍是 MKV。
    /// 流程：先轉成同資料夾的 xxx.mkv.tmp → 驗證（影片已是 H.264 8-bit、音訊軌數沒少、長度相近）→ 原檔改名 xxx.mkv.bak → 暫存檔改名成 xxx.mkv。
    /// 檔名不變，所以評分、網址、書籤都不受影響；新檔的修改時間沿用原檔，不會被當成新上架。任何一步失敗都保留原檔。
    /// 一次只轉一個（單一背景執行緒）；佇列只放在記憶體，重新啟動後要再按一次。
    /// </summary>
    public partial class VideoConvertService : IVideoConvertService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        const int MaxFinishedJobs = 50;
        readonly SystemConfig config = Ioc.GetConfig();
        readonly IRatingService ratingService;
        readonly IUnplayableService unplayableService;
        readonly IFolderIdManager fimgr;
        readonly object gate = new object();
        readonly List<ConvertJob> jobs = new List<ConvertJob>();        // 排隊、轉檔中、最近完成的都在這（舊到新）
        Thread worker;
        Process current;
        long nextId;

        public VideoConvertService(IRatingService ratingService, IUnplayableService unplayableService, IFolderIdManager fimgr)
        {
            this.ratingService = ratingService;
            this.unplayableService = unplayableService;
            this.fimgr = fimgr;
        }

        public bool Enabled
        {
            get
            {
                TranscodeConfig c = config.Transcode;
                return c != null && string.IsNullOrWhiteSpace(c.Ffmpeg) == false && File.Exists(c.Ffmpeg);
            }
        }

        // ---------- 列表 ----------

        public List<ConvertFolder> ListFolders()
        {
            List<ConvertFolder> result = new List<ConvertFolder>();
            RatingConfig rc = config.Rating;
            if (rc == null || rc.Shares == null)
            {
                return result;
            }
            int depth = Math.Max(1, rc.Depth);
            foreach (string share in rc.Shares)
            {
                FolderConfig root = config.Folders.FirstOrDefault(f => string.Equals(f.Title, share, StringComparison.OrdinalIgnoreCase));
                if (root == null || Directory.Exists(root.Path) == false)
                {
                    continue;
                }
                foreach (DirectoryInfo dir in WorkFolders(new DirectoryInfo(root.Path), depth))
                {
                    if (unplayableService.Get(ratingService.Resolve(dir.Name).Key) == null)
                    {
                        continue;
                    }
                    ConvertFolder folder = new ConvertFolder { Id = fimgr.GetIdByFilePath(dir.FullName), Name = dir.Name, Url = PageUrl(dir.FullName) };
                    foreach (FileInfo f in MkvFiles(dir))
                    {
                        folder.Files.Add(Describe(f));
                    }
                    result.Add(folder);
                }
            }
            return result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string PageUrl(string fullPath)
        {
            string rel;
            if (PathUtility.TryGetRelPath(fullPath, out rel) == false)
            {
                return null;
            }
            return "/page/" + string.Join("/", rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        }

        static IEnumerable<DirectoryInfo> WorkFolders(DirectoryInfo dir, int depth)
        {
            if (depth == 0)
            {
                yield return dir;
                yield break;
            }
            List<DirectoryInfo> subs;
            try
            {
                subs = dir.EnumerateDirectories().Where(d => d.Attributes.HasFlag(FileAttributes.Hidden) == false).ToList();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "列出資料夾失敗 {0}", dir.FullName);
                yield break;
            }
            foreach (DirectoryInfo sub in subs)
            {
                foreach (DirectoryInfo work in WorkFolders(sub, depth - 1))
                {
                    yield return work;
                }
            }
        }

        static IEnumerable<FileInfo> MkvFiles(DirectoryInfo dir)
        {
            return dir.EnumerateFiles("*.mkv")
                .Where(f => f.Attributes.HasFlag(FileAttributes.Hidden) == false && f.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        }

        // 與 /api/mediainfo 共用同一份快取（同樣的 key 與型別）
        static MkvMediaInfo Inspect(FileInfo f)
        {
            string cacheKey = string.Format("mkvinfo://{0}|{1}|{2}", f.FullName, f.Length, f.LastWriteTimeUtc.Ticks);
            MkvMediaInfo info = Ioc.GetCache().Get<MkvMediaInfo>(cacheKey);
            if (info == null)
            {
                info = MkvCodecInspector.Inspect(f.FullName);
                Ioc.GetCache().Set(cacheKey, info, TimeSpan.FromHours(6));
            }
            return info;
        }

        /// <summary>影片軌（預設軌優先）是 H.264 8-bit；沒有影片軌視為相容（沒東西可轉）</summary>
        public static bool IsVideoCompatible(MkvMediaInfo info)
        {
            MkvStreamInfo v = info.Video.FirstOrDefault(x => x.IsDefault) ?? info.Video.FirstOrDefault();
            if (v == null)
            {
                return true;
            }
            return v.AlwaysUnsupported == false && v.Mime != null && v.Mime.StartsWith("avc1.", StringComparison.Ordinal)
                && (v.Label ?? string.Empty).Contains("-bit") == false;
        }

        ConvertFile Describe(FileInfo f)
        {
            ConvertFile file = new ConvertFile
            {
                Id = fimgr.GetIdByFilePath(f.FullName),
                Name = f.Name,
                Bytes = f.Length,
                Size = Helper.GetReadableByteSize(f.Length),
                HasBackup = HasBackup(f.FullName)
            };
            try
            {
                MkvMediaInfo info = Inspect(f);
                file.Video = info.Video.Count == 0 ? null : string.Join("、", info.Video.Select(v => v.Label));
                file.Audio = info.Audio.Count == 0 ? null : string.Join("、", info.Audio.Select(a => a.Label));
                file.Compatible = IsVideoCompatible(info);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 MKV 編碼失敗 {0}", f.FullName);
            }
            lock (gate)
            {
                ConvertJob job = jobs.LastOrDefault(j => string.Equals(j.FullPath, f.FullName, StringComparison.OrdinalIgnoreCase));
                if (job != null && (job.State == ConvertJob.StatePending || job.State == ConvertJob.StateRunning || job.State == ConvertJob.StateFailed))
                {
                    file.State = job.State;
                }
            }
            return file;
        }

        static bool HasBackup(string fullPath)
        {
            string dir = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileName(fullPath);
            try
            {
                return Directory.EnumerateFiles(dir, name + "*.bak").Any();
            }
            catch
            {
                return false;
            }
        }

        // ---------- 佇列 ----------

        public ConvertJob Enqueue(string fullPath, string by, out string error)
        {
            error = null;
            if (Enabled == false)
            {
                error = "伺服器沒有設定 ffmpeg";
                return null;
            }
            if (string.IsNullOrEmpty(fullPath) || File.Exists(fullPath) == false)
            {
                error = "找不到這個檔案";
                return null;
            }
            FileInfo fi = new FileInfo(fullPath);
            if (fi.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) == false)
            {
                error = "只能轉換 MKV 檔";
                return null;
            }
            string folderPath = fi.DirectoryName;
            if (ratingService.IsRateable(folderPath) == false || unplayableService.Get(ratingService.Resolve(fi.Directory.Name).Key) == null)
            {
                error = "這個檔案所在的資料夾沒有被標註「無法播放」";
                return null;
            }
            lock (gate)
            {
                if (jobs.Any(j => string.Equals(j.FullPath, fi.FullName, StringComparison.OrdinalIgnoreCase)
                    && (j.State == ConvertJob.StatePending || j.State == ConvertJob.StateRunning)))
                {
                    error = "已經在佇列中";
                    return null;
                }
                ConvertJob job = new ConvertJob
                {
                    Id = (++nextId).ToString(CultureInfo.InvariantCulture),
                    FileId = fimgr.GetIdByFilePath(fi.FullName),
                    Name = fi.Name,
                    Folder = fi.Directory.Name,
                    State = ConvertJob.StatePending,
                    By = by,
                    QueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    FullPath = fi.FullName,
                    FolderKey = ratingService.Resolve(fi.Directory.Name).Key
                };
                jobs.Add(job);
                TrimFinished();
                if (worker == null)
                {
                    worker = new Thread(WorkLoop) { IsBackground = true, Name = "VideoConvertWorker" };
                    worker.Start();
                }
                Monitor.PulseAll(gate);
                return job;
            }
        }

        // 只保留最近 MaxFinishedJobs 筆已結束的工作
        void TrimFinished()
        {
            List<ConvertJob> finished = jobs.Where(j => j.State != ConvertJob.StatePending && j.State != ConvertJob.StateRunning).ToList();
            for (int i = 0; i < finished.Count - MaxFinishedJobs; i++)
            {
                jobs.Remove(finished[i]);
            }
        }

        public List<ConvertJob> GetJobs()
        {
            lock (gate)
            {
                return jobs.AsEnumerable().Reverse().ToList();
            }
        }

        public int ClearFinished()
        {
            lock (gate)
            {
                return jobs.RemoveAll(j => j.State != ConvertJob.StatePending && j.State != ConvertJob.StateRunning);
            }
        }

        public bool Cancel(string jobId)
        {
            lock (gate)
            {
                ConvertJob job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job == null)
                {
                    return false;
                }
                if (job.State == ConvertJob.StatePending)
                {
                    job.State = ConvertJob.StateCanceled;
                    job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    return true;
                }
                if (job.State == ConvertJob.StateRunning)
                {
                    job.CancelRequested = true;
                    try { current?.Kill(true); } catch { }
                    return true;
                }
                return false;
            }
        }

        void WorkLoop()
        {
            while (true)
            {
                ConvertJob job;
                lock (gate)
                {
                    while ((job = jobs.FirstOrDefault(j => j.State == ConvertJob.StatePending)) == null)
                    {
                        Monitor.Wait(gate);
                    }
                    job.State = ConvertJob.StateRunning;
                    job.Progress = 0;
                }
                string error = null;
                try
                {
                    error = (job.Kind == ConvertJob.KindCue || job.Kind == ConvertJob.KindCueCache) ? SplitCue(job) : Convert(job);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "轉檔發生例外 {0}", job.FullPath);
                    error = "發生例外：" + ex.Message;
                }
                lock (gate)
                {
                    job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (job.CancelRequested)
                    {
                        job.State = ConvertJob.StateCanceled;
                        job.Message = null;
                    }
                    else if (error != null)
                    {
                        job.State = ConvertJob.StateFailed;
                        job.Message = error;
                    }
                    else
                    {
                        job.State = ConvertJob.StateDone;
                        job.Progress = 100;
                    }
                }
                if (job.Kind == ConvertJob.KindCueCache)
                {
                    TrimCueCache();
                }
            }
        }

        // ---------- CUE 拆軌 ----------

        const long MaxCueBytes = 1024 * 1024;
        static readonly string[] CueAudioExts = { ".flac", ".wav", ".wv", ".ape", ".tta", ".m4a" };

        public CuePlan PreviewCue(string cuePath, out string error)
        {
            return BuildCuePlan(cuePath, out error);
        }

        CuePlan BuildCuePlan(string cuePath, out string error)
        {
            error = null;
            if (Enabled == false)
            {
                error = "伺服器沒有設定 ffmpeg";
                return null;
            }
            if (string.IsNullOrEmpty(cuePath) || File.Exists(cuePath) == false)
            {
                error = "找不到這個檔案";
                return null;
            }
            FileInfo cue = new FileInfo(cuePath);
            if (cue.Extension.Equals(".cue", StringComparison.OrdinalIgnoreCase) == false)
            {
                error = "只能處理 CUE 檔";
                return null;
            }
            if (cue.Length > MaxCueBytes)
            {
                error = "CUE 檔太大，不像是正常的 CUE";
                return null;
            }
            CueSheet sheet;
            try
            {
                sheet = CueSheet.Parse(File.ReadAllBytes(cue.FullName));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 CUE 失敗 {0}", cue.FullName);
                error = "讀取 CUE 失敗：" + ex.Message;
                return null;
            }
            if (sheet.Tracks.Count == 0)
            {
                error = "CUE 裡找不到可用的曲目（需要 TRACK 與 INDEX 01）";
                return null;
            }
            // 每一軌的音檔：CUE 的 FILE 找不到時，退而求其次找同名的音檔
            Dictionary<string, string> audioByCueName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fileName in sheet.Tracks.Select(t => t.File ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string found = FindAudio(cue, fileName);
                if (found == null)
                {
                    error = "找不到音檔「" + (fileName.Length == 0 ? "（CUE 沒有 FILE）" : fileName) + "」，請確認它和 CUE 在同一個資料夾";
                    return null;
                }
                audioByCueName[fileName] = found;
            }
            string firstAudio = audioByCueName[sheet.Tracks[0].File ?? string.Empty];
            string folderName = sheet.FolderName(cue.Name, Path.GetFileName(firstAudio));
            int width = Math.Max(2, sheet.Tracks.Max(t => t.Number).ToString(CultureInfo.InvariantCulture).Length);
            int? disc = null;
            System.Text.RegularExpressions.Match dm = System.Text.RegularExpressions.Regex.Match(folderName, @"^Disc (\d+)$");
            if (dm.Success)
            {
                disc = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            string album = sheet.AlbumName();
            if (album.Length == 0)
            {
                album = cue.Directory.Name;
            }
            string year = sheet.Date;
            if (string.IsNullOrWhiteSpace(year) == false && year.Length >= 4)
            {
                year = year.Substring(0, 4);
            }
            CuePlan plan = new CuePlan
            {
                Cue = cue.Name,
                Album = album,
                Title = string.IsNullOrWhiteSpace(sheet.Title) ? album : sheet.Title.Trim(),
                Artist = sheet.Performer,
                Year = year,
                Genre = sheet.Genre,
                Audio = Path.GetFileName(firstAudio),
                Folder = folderName,
                Disc = disc,
                Bitrate = Math.Max(32, config.Transcode.Bitrate),
                OutputDir = Path.Combine(cue.DirectoryName, folderName)
            };
            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CueTrack t in sheet.Tracks)
            {
                string fileName = sheet.TrackFileName(t, width);
                if (usedNames.Add(fileName) == false)
                {
                    error = "CUE 裡有重複的曲號 " + t.Number + "，無法決定檔名";
                    return null;
                }
                CuePlanTrack pt = new CuePlanTrack
                {
                    Number = t.Number,
                    Title = string.IsNullOrWhiteSpace(t.Title) ? "Track " + t.Number.ToString(CultureInfo.InvariantCulture) : t.Title,
                    FileName = fileName,
                    Start = t.Start,
                    Length = t.End.HasValue ? t.End.Value - t.Start : (double?)null,
                    Audio = audioByCueName[t.File ?? string.Empty],
                    Performer = string.IsNullOrWhiteSpace(t.Performer) ? sheet.Performer : t.Performer
                };
                pt.Exists = File.Exists(Path.Combine(plan.OutputDir, fileName));
                if (pt.Exists)
                {
                    plan.ExistingCount++;
                }
                plan.Tracks.Add(pt);
            }
            return plan;
        }

        static string FindAudio(FileInfo cue, string cueFileName)
        {
            DirectoryInfo dir = cue.Directory;
            string name = Path.GetFileName((cueFileName ?? string.Empty).Replace('\\', '/').TrimEnd('/'));
            try
            {
                if (name.Length > 0)
                {
                    string exact = Path.Combine(dir.FullName, name);
                    if (File.Exists(exact))
                    {
                        return exact;
                    }
                    FileInfo ci = dir.EnumerateFiles().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (ci != null)
                    {
                        return ci.FullName;
                    }
                }
                string baseName = Path.GetFileNameWithoutExtension(cue.Name);
                FileInfo same = dir.EnumerateFiles().FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f.Name), baseName, StringComparison.OrdinalIgnoreCase)
                    && CueAudioExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
                return same?.FullName;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "尋找 CUE 的音檔失敗 {0}", cue.FullName);
                return null;
            }
        }

        public ConvertJob EnqueueCue(string cuePath, string by, out string error)
        {
            CuePlan plan = BuildCuePlan(cuePath, out error);
            if (plan == null)
            {
                return null;
            }
            if (plan.ExistingCount >= plan.Tracks.Count)
            {
                error = "「" + plan.Folder + "」裡的 " + plan.Tracks.Count + " 首都已經存在，不需要再轉";
                return null;
            }
            FileInfo cue = new FileInfo(cuePath);
            lock (gate)
            {
                if (jobs.Any(j => string.Equals(j.FullPath, cue.FullName, StringComparison.OrdinalIgnoreCase)
                    && (j.State == ConvertJob.StatePending || j.State == ConvertJob.StateRunning)))
                {
                    error = "已經在佇列中";
                    return null;
                }
                ConvertJob job = new ConvertJob
                {
                    Id = (++nextId).ToString(CultureInfo.InvariantCulture),
                    FileId = fimgr.GetIdByFilePath(cue.FullName),
                    Name = cue.Name,
                    Folder = cue.Directory.Name,
                    Kind = ConvertJob.KindCue,
                    State = ConvertJob.StatePending,
                    By = by,
                    QueuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    FullPath = cue.FullName
                };
                jobs.Add(job);
                TrimFinished();
                if (worker == null)
                {
                    worker = new Thread(WorkLoop) { IsBackground = true, Name = "VideoConvertWorker" };
                    worker.Start();
                }
                Monitor.PulseAll(gate);
                return job;
            }
        }

        /// <summary>回傳 null 表示成功（或已取消），否則是失敗原因；已經轉好的曲目會留著，原始 FLAC 與 CUE 不動</summary>
        string SplitCue(ConvertJob job)
        {
            string error;
            CuePlan plan = BuildCuePlan(job.FullPath, out error);
            if (plan == null)
            {
                return error;
            }
            bool toCache = job.Kind == ConvertJob.KindCueCache;
            string outDir = toCache ? job.CacheDir : plan.OutputDir;
            Directory.CreateDirectory(outDir);
            Stopwatch watch = Stopwatch.StartNew();
            int made = 0, skipped = 0, n = plan.Tracks.Count;
            for (int i = 0; i < n; i++)
            {
                if (job.CancelRequested)
                {
                    return null;
                }
                CuePlanTrack t = plan.Tracks[i];
                string output = Path.Combine(outDir, toCache ? CueCacheTrackName(t.Number) : t.FileName);
                job.Progress = Math.Min(99, i * 100 / n);
                if (File.Exists(output))
                {
                    skipped++;
                    continue;
                }
                string tmp = output + ".tmp";
                try
                {
                    string err = RunCueTrack(job, plan, t, n, tmp);
                    if (job.CancelRequested)
                    {
                        return null;
                    }
                    if (err != null)
                    {
                        return "第 " + t.Number + " 軌「" + t.Title + "」" + err;
                    }
                    FileInfo fi = new FileInfo(tmp);
                    if (fi.Exists == false || fi.Length == 0)
                    {
                        return "第 " + t.Number + " 軌「" + t.Title + "」ffmpeg 沒有產生輸出檔";
                    }
                    File.Move(tmp, output);
                    made++;
                }
                finally
                {
                    try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { }
                }
            }
            job.Message = toCache
                ? string.Format("拆出 {0} 首到暫存{1}", made, skipped > 0 ? "（略過 " + skipped + " 首已存在）" : string.Empty)
                : string.Format("拆出 {0} 首到「{1}」{2}", made, plan.Folder, skipped > 0 ? "（略過 " + skipped + " 首已存在）" : string.Empty);
            logger.Info("CUE 拆軌完成 {0}：{1}，耗時 {2:F1} 分鐘", job.FullPath, job.Message, watch.Elapsed.TotalMinutes);
            return null;
        }

        string RunCueTrack(ConvertJob job, CuePlan plan, CuePlanTrack t, int total, string tmp)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = config.Transcode.Ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            List<string> args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" };
            if (t.Start > 0)
            {
                args.Add("-ss");
                args.Add(t.Start.ToString("F6", inv));
            }
            args.Add("-i");
            args.Add(t.Audio);
            if (t.Length.HasValue)
            {
                args.Add("-t");
                args.Add(t.Length.Value.ToString("F6", inv));
            }
            args.AddRange(new[] { "-vn", "-map", "0:a:0", "-map_metadata", "-1", "-c:a", "libmp3lame", "-b:a", plan.Bitrate.ToString(inv) + "k", "-id3v2_version", "3" });
            AddTag(args, "title", t.Title);
            AddTag(args, "artist", t.Performer);
            AddTag(args, "album_artist", plan.Artist);
            AddTag(args, "album", plan.Album);
            AddTag(args, "track", t.Number.ToString(inv) + "/" + total.ToString(inv));
            AddTag(args, "date", plan.Year);
            AddTag(args, "genre", plan.Genre);
            if (plan.Disc.HasValue)
            {
                AddTag(args, "disc", plan.Disc.Value.ToString(inv));
            }
            args.AddRange(new[] { "-f", "mp3", tmp });
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using (Process p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                StringBuilder err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 2000) { err.AppendLine(e.Data); } };
                p.OutputDataReceived += (s, e) => { };
                lock (gate)
                {
                    current = p;
                }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (job.CancelRequested)
                {
                    try { p.Kill(true); } catch { }
                }
                p.WaitForExit();
                lock (gate)
                {
                    current = null;
                }
                if (job.CancelRequested)
                {
                    return null;
                }
                if (p.ExitCode != 0)
                {
                    string detail = err.ToString().Trim();
                    logger.Warn("CUE 拆軌失敗 {0}：ffmpeg 結束碼 {1} {2}", t.Audio, p.ExitCode, detail);
                    return "ffmpeg 失敗（結束碼 " + p.ExitCode + "）" + (detail.Length == 0 ? string.Empty : "：" + FirstLine(detail));
                }
            }
            return null;
        }

        static void AddTag(List<string> args, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            args.Add("-metadata");
            args.Add(key + "=" + value.Trim());
        }

        // ---------- 轉檔 ----------

        /// <summary>回傳 null 表示成功，否則是失敗原因；原檔在失敗時一定保持原樣</summary>
        string Convert(ConvertJob job)
        {
            FileInfo src = new FileInfo(job.FullPath);
            if (src.Exists == false)
            {
                return "檔案已經不存在";
            }
            MkvMediaInfo srcInfo = MkvCodecInspector.Inspect(src.FullName);
            double? srcDuration = MatroskaSubtitleReader.GetDurationSeconds(src.FullName);
            string tmp = src.FullName + ".tmp";
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                string error = RunFfmpeg(job, src.FullName, tmp, srcInfo, srcDuration);
                if (error != null)
                {
                    return error;
                }
                if (job.CancelRequested)
                {
                    return null;
                }
                error = Verify(tmp, srcInfo, srcDuration);
                if (error != null)
                {
                    return error;
                }
                try { File.SetLastWriteTimeUtc(tmp, src.LastWriteTimeUtc); } catch { }        // 沿用原檔的修改時間
                long oldBytes = src.Length;
                string bak = SwapIn(src.FullName, tmp);
                long newBytes = new FileInfo(src.FullName).Length;
                job.Message = string.Format("{0} → {1}，原檔備份為 {2}", Helper.GetReadableByteSize(oldBytes), Helper.GetReadableByteSize(newBytes), Path.GetFileName(bak));
                logger.Info("MKV 轉檔完成 {0}：{1}，耗時 {2:F1} 分鐘", src.FullName, job.Message, watch.Elapsed.TotalMinutes);
                ClearMarkIfAllCompatible(src.Directory, job.FolderKey);
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { }        // 成功時 tmp 已經改名，這裡只清失敗留下的
            }
        }

        string RunFfmpeg(ConvertJob job, string src, string tmp, MkvMediaInfo srcInfo, double? srcDuration)
        {
            TranscodeConfig tc = config.Transcode;
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = tc.Ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true
            };
            List<string> args = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-progress", "pipe:1", "-i", src,
                "-map", "0",
                "-c:v", "libx264", "-preset", string.IsNullOrWhiteSpace(tc.VideoPreset) ? "medium" : tc.VideoPreset,
                "-crf", Math.Max(0, Math.Min(51, tc.VideoCrf)).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", Math.Max(64, tc.AudioBitrate).ToString(CultureInfo.InvariantCulture) + "k"
            };
            for (int i = 0; i < srcInfo.Audio.Count; i++)
            {
                if (srcInfo.Audio[i].Label == "AAC")
                {
                    args.Add("-c:a:" + i.ToString(CultureInfo.InvariantCulture));
                    args.Add("copy");        // 原本就是 AAC：直接複製，不重編
                }
            }
            if (tc.VideoThreads > 0)
            {
                args.Insert(args.IndexOf("-c:v"), "-threads");
                args.Insert(args.IndexOf("-c:v"), Math.Min(tc.VideoThreads, 64).ToString(CultureInfo.InvariantCulture));
            }
            args.AddRange(new[] { "-c:s", "copy", "-c:t", "copy", "-max_muxing_queue_size", "4096", "-f", "matroska", tmp });
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using (Process p = Process.Start(psi))
            {
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                StringBuilder err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && err.Length < 2000) { err.AppendLine(e.Data); } };
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null || srcDuration.HasValue == false || srcDuration.Value <= 0)
                    {
                        return;
                    }
                    // out_time_us（新版）或 out_time_ms（舊版，單位同樣是微秒）
                    string v = e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) ? e.Data.Substring(12)
                        : e.Data.StartsWith("out_time_ms=", StringComparison.Ordinal) ? e.Data.Substring(12) : null;
                    long us;
                    if (v != null && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out us) && us >= 0)
                    {
                        job.Progress = (int)Math.Max(0, Math.Min(99, us / 1000000.0 / srcDuration.Value * 100));
                    }
                };
                lock (gate)
                {
                    current = p;
                }
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (job.CancelRequested)
                {
                    try { p.Kill(true); } catch { }
                }
                p.WaitForExit();
                lock (gate)
                {
                    current = null;
                }
                if (job.CancelRequested)
                {
                    return null;
                }
                if (p.ExitCode != 0)
                {
                    string detail = err.ToString().Trim();
                    logger.Warn("MKV 轉檔失敗 {0}：ffmpeg 結束碼 {1} {2}", src, p.ExitCode, detail);
                    return "ffmpeg 失敗（結束碼 " + p.ExitCode + "）" + (detail.Length == 0 ? string.Empty : "：" + FirstLine(detail));
                }
            }
            return null;
        }

        static string FirstLine(string text)
        {
            int i = text.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? text : text.Substring(0, i);
        }

        // 轉出來的檔案要真的達到目的才換掉原檔
        static string Verify(string tmp, MkvMediaInfo srcInfo, double? srcDuration)
        {
            FileInfo fi = new FileInfo(tmp);
            if (fi.Exists == false || fi.Length == 0)
            {
                return "ffmpeg 沒有產生輸出檔";
            }
            MkvMediaInfo info = MkvCodecInspector.Inspect(tmp);
            if (info.Video.Count == 0 || IsVideoCompatible(info) == false)
            {
                return "轉出的檔案影片軌不是 H.264 8-bit，已放棄";
            }
            if (info.Audio.Count < srcInfo.Audio.Count)
            {
                return "轉出的檔案音訊軌變少了（原本 " + srcInfo.Audio.Count + " 條，現在 " + info.Audio.Count + " 條），已放棄";
            }
            double? newDuration = MatroskaSubtitleReader.GetDurationSeconds(tmp);
            if (srcDuration.HasValue && newDuration.HasValue && Math.Abs(srcDuration.Value - newDuration.Value) > 3)
            {
                return string.Format("轉出的檔案長度不對（原本 {0:F0} 秒，現在 {1:F0} 秒），已放棄", srcDuration.Value, newDuration.Value);
            }
            return null;
        }

        /// <summary>原檔改名成 .bak，再把暫存檔改成原檔名；回傳備份檔的完整路徑。第二步失敗會把原檔改回來</summary>
        static string SwapIn(string original, string tmp)
        {
            string bak = original + ".bak";
            for (int n = 1; File.Exists(bak); n++)
            {
                bak = original + "." + n + ".bak";        // 之前已經有備份：不覆蓋
            }
            MoveWithRetry(original, bak);        // 有人正在串流這個檔案時會被鎖住，所以重試
            try
            {
                File.Move(tmp, original);
            }
            catch
            {
                try { File.Move(bak, original); } catch (Exception ex) { logger.Error(ex, "換檔失敗且無法還原原檔，備份在 {0}", bak); }
                throw;
            }
            return bak;
        }

        static void MoveWithRetry(string from, string to)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(from, to);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(3000);
                }
            }
        }

        // 資料夾裡所有 MKV 的影片都相容了，就自動取消「無法播放」標註
        void ClearMarkIfAllCompatible(DirectoryInfo dir, string key)
        {
            try
            {
                foreach (FileInfo f in MkvFiles(dir))
                {
                    if (IsVideoCompatible(MkvCodecInspector.Inspect(f.FullName)) == false)
                    {
                        return;
                    }
                }
                unplayableService.Clear(key);
                logger.Info("{0} 的 MKV 都已相容，自動取消「無法播放」標註", dir.Name);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "檢查是否取消標註失敗 {0}", dir.FullName);
            }
        }
    }
}
