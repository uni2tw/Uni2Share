using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public interface INewsService
    {
        /// <summary>設定檔有指定要通知的分享根才啟用</summary>
        bool Enabled { get; }
        /// <summary>啟動背景處理（檔案監看、單一資料夾小掃描、定期完整掃描）；沒啟用就什麼都不做</summary>
        void Start();
        /// <summary>最近的新上架事件（新到舊）；已經不存在的資料夾不列出</summary>
        List<NewsEvent> GetEvents(int limit);
        /// <summary>這個會員「上次看新上架」的時間（毫秒）；第一次問到時以現在為準，舊的事件不算未讀</summary>
        long GetLastRead(string member);
        int GetUnread(string member);
        void MarkRead(string member);
        /// <summary>移除某個會員「上次看新上架」的記錄（刪除會員時用）</summary>
        void RemoveMember(string member);
        /// <summary>某個資料夾裡有哪些項目要加「新」標記（badgeDays 內）：子資料夾（有通知的作品／專輯）與檔案（新增的集數）</summary>
        NewsBadges GetBadges(string folderRel);
    }

    public class NewsBadge
    {
        public string Kind { get; set; }
        public int Count { get; set; }
        public long Time { get; set; }
    }

    public class NewsBadges
    {
        /// <summary>子資料夾名稱 → 最新一筆通知</summary>
        public Dictionary<string, NewsBadge> Folders { get; } = new Dictionary<string, NewsBadge>(StringComparer.OrdinalIgnoreCase);
        /// <summary>這個資料夾裡新增的檔名</summary>
        public HashSet<string> Files { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public class NewsEvent
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("share")]
        public string Share { get; set; }
        /// <summary>series（新作品）／episodes（既有作品新增集數）／album（新專輯）</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }
        /// <summary>動畫資料夾或專輯資料夾的相對路徑，例如 /Anime/2026-4/作品</summary>
        [JsonPropertyName("path")]
        public string Path { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("count")]
        public int Count { get; set; }
        /// <summary>UTC 毫秒</summary>
        [JsonPropertyName("time")]
        public long Time { get; set; }
        /// <summary>這筆通知新增的檔名（動畫集數）；資料夾裡的「新」標記靠它。專輯為 null；1.16.0 之前產生的事件也沒有</summary>
        [JsonPropertyName("files")]
        public List<string> Files { get; set; }
    }

    public class NewsFileState
    {
        public long Size { get; set; }
        public long LastChange { get; set; }
        public bool Announced { get; set; }
    }

    public class NewsUnitState
    {
        public bool Announced { get; set; }
        /// <summary>上次列出這個作品資料夾時，資料夾本身的修改時間（UTC ticks）；沒變就不用再列出裡面的檔案</summary>
        public long Mtime { get; set; }
        public Dictionary<string, NewsFileState> Files { get; set; } = new Dictionary<string, NewsFileState>();
    }

    public class NewsAlbumState
    {
        public long Size { get; set; }
        public long LastChange { get; set; }
        public bool Announced { get; set; }
        /// <summary>專輯資料夾本身的修改時間（UTC ticks）；已通知過的專輯，沒變就整個略過，不再列出內容</summary>
        public long Mtime { get; set; }
    }

    public class NewsShareState
    {
        /// <summary>第一次掃描只建立基準（把現有的全部當成已知），不產生事件，不然一啟用就噴出整個資料庫</summary>
        public bool Baselined { get; set; }
        /// <summary>上次「完整掃描」（不看資料夾修改時間）的時間（UTC 毫秒）</summary>
        public long LastFullScan { get; set; }
        public Dictionary<string, NewsUnitState> Units { get; set; } = new Dictionary<string, NewsUnitState>();
        public Dictionary<string, NewsAlbumState> Albums { get; set; } = new Dictionary<string, NewsAlbumState>();
    }

    public class NewsState
    {
        public Dictionary<string, NewsShareState> Shares { get; set; } = new Dictionary<string, NewsShareState>();
    }

    /// <summary>
    /// 站內「新上架」通知。完全獨立於搜尋索引（Lucene），資料來源是檔案系統本身，自己維護快照（data/news-state.json）。
    ///
    /// 「事件當提示，掃描當事實」：
    ///  1. 自己的 FileSystemWatcher 收到新增／改名事件，不解讀細節，只標記「這個作品資料夾／專輯要檢查」，等幾秒合併連續事件，
    ///     然後只對那一個資料夾做一次小掃描（一次查詢），套用快照與穩定判斷；
    ///  2. 看到新項目後登記成「等待確認」，每隔幾秒複查，大小連續 stableMinutes 不變才通知（所以複製到一半不會通知）；
    ///  3. 檔案監看在網路磁碟上不可靠（事件太多會漏、斷線會失效），所以保留完整掃描當保險：啟動後一次、每 scanMinutes 一次
    ///     （只在 activeFrom～activeTo 時段內）、監看出錯重建後立刻一次；
    ///  4. 改名（同一個父資料夾內）用事件裡的「舊路徑→新路徑」直接搬移快照紀錄與已產生的通知；搬到別的資料夾時作業系統送來的是
    ///     「刪除舊路徑＋新增新路徑」，所以刪除時先把快照紀錄暫存成「墓碑」兩分鐘，新位置出現同名資料夾（或同名同大小的檔案）就把紀錄接回去，
    ///     兩者都不會誤報成新上架。
    ///
    /// 兩種模式：episodes（動畫：出現新作品，或既有作品資料夾多了新的影片檔）、albums（音樂：只有新的專輯資料夾出現才算，之後補進歌曲不算）。
    /// albums 自動辨認專輯資料夾：直接放著音樂檔的資料夾；光碟資料夾（Disc 1 / CD2…）由母資料夾代表，所以歌手\專輯與專輯直接放在根目錄的混合結構都可以。
    /// 事件存 data/news.json，每個會員的已讀時間存 data/userData/news-read.json；每次處理的耗時與查詢數寫進日誌（搜尋「新上架」）。
    /// 所有快照的修改都在同一條背景執行緒上，監看的回呼只把事件放進佇列。
    /// </summary>
    public class NewsService : INewsService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        const int MaxEvents = 300;
        const int MaxEventAgeDays = 90;
        const long MergeWindowMs = 12L * 3600 * 1000;
        const double DebounceSeconds = 3;
        const double PendingRecheckSeconds = 15;
        static readonly HashSet<string> TempExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".part", ".!qb", ".crdownload", ".tmp", ".aria2", ".download", ".partial" };

        class ScanStats
        {
            public int DirQueries;
            public int Skipped;
            public int Events;
            public bool Full;
        }

        class ShareRuntime
        {
            public DateTime NextDue;
            public bool ForceFull;
        }

        class FsEvent
        {
            public string Kind;       // created / renamed
            public string Path;
            public string OldPath;
        }

        class ShareWatch
        {
            public NewsShareConfig Sc;
            public FolderConfig Root;
            public FileSystemWatcher Watcher;
            public bool Broken;
            public DateTime NextRetry;
        }

        /// <summary>
        /// 被刪除（或搬走）的資料夾／檔案的快照紀錄，暫存 TombstoneSeconds 秒。搬移＝刪除＋新增，如果新位置很快出現同名資料夾（檔案則是同名同大小），
        /// 就把紀錄接回新路徑，不當成新上架。
        /// </summary>
        class Tomb
        {
            public string OldRel;
            public string Name;
            public DateTime Time;
            public bool IsFile;
            public NewsFileState File;           // 檔案
            public long FileSize;
            public Dictionary<string, NewsUnitState> Units;       // 資料夾（episodes）：舊路徑為 key
            public Dictionary<string, NewsAlbumState> Albums;     // 資料夾（albums）
        }

        const double TombstoneSeconds = 120;
        const int MaxEventFiles = 200;

        /// <summary>等著處理的檢查工作：unit＝一個作品資料夾、album＝一個專輯資料夾、walk＝一個整個新出現的資料夾（往下找作品／專輯）</summary>
        class Target
        {
            public string Kind;
            public string Share;
            public string Rel;
            public DateTime Due;
            public string Key { get { return Kind + "|" + Share + "|" + Rel; } }
        }

        readonly SystemConfig config = Ioc.GetConfig();
        readonly IRatingService ratingService = Ioc.Get<IRatingService>();
        readonly object gate = new object();
        readonly ConcurrentQueue<FsEvent> fsEvents = new ConcurrentQueue<FsEvent>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Dictionary<string, ShareWatch> watches = new Dictionary<string, ShareWatch>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Target> work = new Dictionary<string, Target>();
        readonly List<Tomb> tombs = new List<Tomb>();
        List<NewsEvent> events;
        Dictionary<string, long> lastRead;
        NewsState state;
        bool stateDirty;
        DateTime lastSave = DateTime.MinValue;
        Thread worker;

        public bool Enabled
        {
            get { return config.News != null && config.News.Shares != null && config.News.Shares.Count > 0; }
        }

        static string DataPath(params string[] parts)
        {
            return parts.Length == 1
                ? Helper.GetRelativePath("data", parts[0])
                : Helper.GetRelativePath("data", parts[0], parts[1]);
        }

        static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        T LoadJson<T>(string path, Func<T> fallback)
        {
            try
            {
                if (File.Exists(path))
                {
                    T value = JsonHelper.Deserialize<T>(File.ReadAllText(path));
                    if (value != null)
                    {
                        return value;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 {0} 失敗，以空白資料開始", path);
            }
            return fallback();
        }

        static void SaveJson(string path, object value)
        {
            // 先寫暫存檔再取代；檔案被防毒等程式暫時鎖住時會自動重試
            SafeFile.WriteAllTextAtomic(path, JsonHelper.Serialize(value, indented: true));
        }

        void EnsureLoaded()
        {
            lock (gate)
            {
                if (events == null)
                {
                    events = LoadJson(DataPath("news.json"), () => new List<NewsEvent>());
                    lastRead = LoadJson(DataPath("userData", "news-read.json"), () => new Dictionary<string, long>());
                }
            }
        }

        // ---------- 讀取（網頁 API 使用） ----------

        public List<NewsEvent> GetEvents(int limit)
        {
            EnsureLoaded();
            List<NewsEvent> list;
            lock (gate)
            {
                list = events.OrderByDescending(e => e.Time).Take(limit).ToList();
            }
            return list.Where(Exists).ToList();
        }

        public NewsBadges GetBadges(string folderRel)
        {
            NewsBadges result = new NewsBadges();
            if (Enabled == false || string.IsNullOrEmpty(folderRel))
            {
                return result;
            }
            EnsureLoaded();
            long cutoff = NowMs() - (long)(Math.Max(0, config.News.BadgeDays) * 86400000d);
            string folder = folderRel.TrimEnd('/');
            string prefix = folder + "/";
            lock (gate)
            {
                foreach (NewsEvent e in events)
                {
                    if (e.Time < cutoff || string.IsNullOrEmpty(e.Path))
                    {
                        continue;
                    }
                    if (string.Equals(e.Path, folder, StringComparison.OrdinalIgnoreCase))
                    {
                        if (e.Files != null)
                        {
                            foreach (string f in e.Files)
                            {
                                result.Files.Add(f);
                            }
                        }
                    }
                    else if (e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Path.IndexOf('/', prefix.Length) < 0)
                    {
                        string name = e.Path.Substring(prefix.Length);
                        NewsBadge old;
                        if (result.Folders.TryGetValue(name, out old) == false || old.Time < e.Time)
                        {
                            result.Folders[name] = new NewsBadge { Kind = e.Kind, Count = e.Count, Time = e.Time };
                        }
                    }
                }
            }
            return result;
        }

        static bool Exists(NewsEvent e)
        {
            string abs;
            return PathUtility.TryGetAbsolutePath(e.Path, out abs) && Directory.Exists(abs);
        }

        public long GetLastRead(string member)
        {
            EnsureLoaded();
            lock (gate)
            {
                long value;
                if (string.IsNullOrEmpty(member) == false && lastRead.TryGetValue(member, out value))
                {
                    return value;
                }
                value = NowMs();
                if (string.IsNullOrEmpty(member) == false)
                {
                    lastRead[member] = value;
                    try { SaveJson(DataPath("userData", "news-read.json"), lastRead); } catch (Exception ex) { logger.Warn(ex, "寫入 news-read.json 失敗"); }
                }
                return value;
            }
        }

        public int GetUnread(string member)
        {
            long last = GetLastRead(member);
            List<NewsEvent> candidates;
            lock (gate)
            {
                candidates = events.Where(e => e.Time > last).ToList();
            }
            return candidates.Count(Exists);
        }

        public void RemoveMember(string member)
        {
            if (string.IsNullOrEmpty(member))
            {
                return;
            }
            EnsureLoaded();
            lock (gate)
            {
                if (lastRead.Remove(member))
                {
                    try { SaveJson(DataPath("userData", "news-read.json"), lastRead); } catch (Exception ex) { logger.Warn(ex, "寫入 news-read.json 失敗"); }
                }
            }
        }

        public void MarkRead(string member)
        {
            if (string.IsNullOrEmpty(member))
            {
                return;
            }
            EnsureLoaded();
            lock (gate)
            {
                lastRead[member] = NowMs();
                try { SaveJson(DataPath("userData", "news-read.json"), lastRead); } catch (Exception ex) { logger.Warn(ex, "寫入 news-read.json 失敗"); }
            }
        }

        // ---------- 完整掃描的時段與間隔 ----------

        static bool TryParseTime(string text, out TimeSpan value)
        {
            value = TimeSpan.Zero;
            return string.IsNullOrWhiteSpace(text) == false && TimeSpan.TryParse(text.Trim(), out value);
        }

        // 沒設定（或格式錯誤）就不限制時段
        bool TryGetWindow(out TimeSpan from, out TimeSpan to)
        {
            from = TimeSpan.Zero;
            to = TimeSpan.Zero;
            return TryParseTime(config.News.ActiveFrom, out from) && TryParseTime(config.News.ActiveTo, out to) && from != to;
        }

        bool InWindow(DateTime local)
        {
            TimeSpan from, to;
            if (TryGetWindow(out from, out to) == false)
            {
                return true;
            }
            TimeSpan t = local.TimeOfDay;
            return from < to ? (t >= from && t < to) : (t >= from || t < to);       // from > to 表示跨午夜
        }

        // 時段外就延到下一個時段的開始；時段內回傳原時間
        DateTime NextWindowStart(DateTime local)
        {
            TimeSpan from, to;
            if (InWindow(local) || TryGetWindow(out from, out to) == false)
            {
                return local;
            }
            TryGetWindow(out from, out to);
            DateTime start = local.Date + from;
            return start > local ? start : start.AddDays(1);
        }

        double IntervalSeconds(NewsShareConfig sc)
        {
            double minutes = sc.ScanMinutes.HasValue ? sc.ScanMinutes.Value : config.News.ScanMinutes;
            return Math.Max(1.0, minutes * 60);
        }

        // ---------- 啟動 ----------

        public void Start()
        {
            if (Enabled == false || worker != null)
            {
                return;
            }
            EnsureLoaded();
            state = LoadJson(DataPath("news-state.json"), () => new NewsState());
            if (config.News.Watch)
            {
                foreach (NewsShareConfig sc in config.News.Shares)
                {
                    FolderConfig root = config.Folders.FirstOrDefault(f => string.Equals(f.Title, sc.Name, StringComparison.OrdinalIgnoreCase));
                    if (root == null)
                    {
                        continue;
                    }
                    ShareWatch sw = new ShareWatch { Sc = sc, Root = root };
                    watches[sc.Name] = sw;
                    StartWatcher(sw);
                }
            }
            worker = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "NewsWorker" };
            worker.Start();
            TimeSpan from, to;
            string window = TryGetWindow(out from, out to) ? (from.ToString(@"hh\:mm") + "–" + to.ToString(@"hh\:mm")) : "不限時段";
            logger.Info("新上架已啟動：{0}；檔案監看 {1}；穩定 {2} 分鐘才通知；完整掃描每 {3} 分鐘一次（限 {4}）、每 {5} 小時不看修改時間再掃一次",
                string.Join("、", config.News.Shares.Select(s => s.Name + "(" + s.Mode + ")")),
                config.News.Watch ? "開啟" : "關閉（只靠定期掃描，建議把 scanMinutes 調短）",
                config.News.StableMinutes, Math.Round(config.News.ScanMinutes, 1), window, config.News.FullScanHours);
        }

        // ---------- 檔案監看（只負責把事件放進佇列） ----------

        void StartWatcher(ShareWatch sw)
        {
            try
            {
                if (Directory.Exists(sw.Root.Path) == false)
                {
                    throw new DirectoryNotFoundException(sw.Root.Path);
                }
                FileSystemWatcher w = new FileSystemWatcher(sw.Root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    // 預設的緩衝區只有 8KB，一次複製很多檔案就會溢位漏事件；放大一點，溢位時仍會有 Error 事件，由完整掃描補回
                    InternalBufferSize = Math.Max(4096, config.News.WatchBufferKB * 1024)
                };
                w.Created += (s, e) => Enqueue("created", e.FullPath, null);
                w.Deleted += (s, e) => Enqueue("deleted", e.FullPath, null);
                w.Renamed += (s, e) => Enqueue("renamed", e.FullPath, e.OldFullPath);
                w.Error += (s, e) => OnWatcherError(sw, e.GetException());
                w.EnableRaisingEvents = true;
                sw.Watcher = w;
                sw.Broken = false;
            }
            catch (Exception ex)
            {
                sw.Broken = true;
                sw.NextRetry = DateTime.Now.AddSeconds(60);
                logger.Warn("新上架：{0} 的檔案監看啟動失敗（{1}），60 秒後重試；期間只靠定期掃描", sw.Sc.Name, ex.Message);
            }
        }

        void OnWatcherError(ShareWatch sw, Exception ex)
        {
            logger.Warn("新上架：{0} 的檔案監看出錯（{1}），可能漏了事件；稍後重建並立刻做一次完整掃描補回", sw.Sc.Name, ex == null ? "?" : ex.Message);
            sw.Broken = true;
            sw.NextRetry = DateTime.Now.AddSeconds(5);
            signal.Set();
        }

        void Enqueue(string kind, string path, string oldPath)
        {
            fsEvents.Enqueue(new FsEvent { Kind = kind, Path = path, OldPath = oldPath });
            signal.Set();
        }

        void RetryBrokenWatchers(Dictionary<string, ShareRuntime> runtime)
        {
            foreach (ShareWatch sw in watches.Values)
            {
                if (sw.Broken == false || sw.NextRetry > DateTime.Now)
                {
                    continue;
                }
                try { if (sw.Watcher != null) { sw.Watcher.Dispose(); } } catch (Exception) { }
                sw.Watcher = null;
                StartWatcher(sw);
                if (sw.Broken == false)
                {
                    logger.Info("新上架：{0} 的檔案監看已重建，立刻做一次完整掃描補回可能漏掉的事件", sw.Sc.Name);
                    ShareRuntime rt;
                    if (runtime.TryGetValue(sw.Sc.Name, out rt))
                    {
                        rt.ForceFull = true;
                        rt.NextDue = DateTime.Now;
                    }
                }
            }
        }

        // ---------- 背景處理主迴圈 ----------

        void Loop()
        {
            Dictionary<string, ShareRuntime> runtime = new Dictionary<string, ShareRuntime>(StringComparer.OrdinalIgnoreCase);
            // 啟動後稍等一下再做第一次完整掃描，不要拖慢啟動
            double startupDelay = Math.Min(30.0, config.News.Shares.Min(s => IntervalSeconds(s)));
            DateTime first = DateTime.Now.AddSeconds(startupDelay);
            foreach (NewsShareConfig sc in config.News.Shares)
            {
                runtime[sc.Name] = new ShareRuntime { NextDue = first };
            }
            while (true)
            {
                try
                {
                    RetryBrokenWatchers(runtime);
                    DrainEvents();
                    ExpireTombs();
                    ProcessWork();
                    RunFullScans(runtime);
                    SaveStateIfDirty();
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "新上架處理失敗");
                }
                double wait = 30;
                DateTime now = DateTime.Now;
                if (work.Count > 0)
                {
                    wait = Math.Min(wait, (work.Values.Min(t => t.Due) - now).TotalSeconds);
                }
                foreach (ShareRuntime rt in runtime.Values)
                {
                    wait = Math.Min(wait, (rt.NextDue - now).TotalSeconds);
                }
                foreach (ShareWatch sw in watches.Values.Where(w => w.Broken))
                {
                    wait = Math.Min(wait, (sw.NextRetry - now).TotalSeconds);
                }
                signal.WaitOne(TimeSpan.FromSeconds(Math.Max(0.5, wait)));
            }
        }

        void SaveStateIfDirty()
        {
            if (stateDirty && (DateTime.Now - lastSave).TotalSeconds >= 3)
            {
                try { SaveJson(DataPath("news-state.json"), state); } catch (Exception ex) { logger.Warn(ex, "寫入 news-state.json 失敗"); }
                stateDirty = false;
                lastSave = DateTime.Now;
            }
        }

        // ---------- 分享根與路徑的小工具 ----------

        NewsShareConfig ShareOf(string fullPath, out FolderConfig root)
        {
            root = null;
            if (string.IsNullOrEmpty(fullPath))
            {
                return null;
            }
            foreach (NewsShareConfig sc in config.News.Shares)
            {
                FolderConfig r = config.Folders.FirstOrDefault(f => string.Equals(f.Title, sc.Name, StringComparison.OrdinalIgnoreCase));
                if (r == null || fullPath.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }
                bool boundary = fullPath.Length == r.Path.Length
                    || fullPath[r.Path.Length] == Path.DirectorySeparatorChar || fullPath[r.Path.Length] == Path.AltDirectorySeparatorChar
                    || r.Path.EndsWith(Path.DirectorySeparatorChar.ToString());
                if (boundary)
                {
                    root = r;
                    return sc;
                }
            }
            return null;
        }

        NewsShareConfig ShareConfig(string name)
        {
            return config.News.Shares.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        NewsShareState ShareState(NewsShareConfig sc)
        {
            NewsShareState st;
            if (state.Shares.TryGetValue(sc.Name, out st) == false)
            {
                st = new NewsShareState();
                state.Shares[sc.Name] = st;
            }
            return st;
        }

        static string RelPath(string fullPath)
        {
            string rel;
            return PathUtility.TryGetRelPath(fullPath, out rel) ? rel : null;
        }

        static string AbsPath(string rel)
        {
            string abs;
            return PathUtility.TryGetAbsolutePath(rel, out abs) && string.IsNullOrEmpty(abs) == false ? abs : null;
        }

        static string[] Segments(string rel)
        {
            return rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        static int DepthOf(string rel)
        {
            return Segments(rel).Length - 1;      // 分享根本身為 0
        }

        static string ParentRel(string rel)
        {
            int i = rel.LastIndexOf('/');
            return i > 0 ? rel.Substring(0, i) : string.Empty;
        }

        static string NameOf(string rel)
        {
            int i = rel.LastIndexOf('/');
            return i >= 0 ? rel.Substring(i + 1) : rel;
        }

        static bool IsHidden(DirectoryInfo d)
        {
            return d.Name.StartsWith(".") || d.Name.StartsWith("$") || d.Attributes.HasFlag(FileAttributes.Hidden);
        }

        static bool IsDiscName(string name)
        {
            return MainService.DiscNameRegex.IsMatch(name);
        }

        static bool IsVideoExt(string ext)
        {
            return TempExtensions.Contains(ext) == false && Helper.GetFileDocType(ext) == Helper.ConstStrings.Video;
        }

        static bool IsMusicExt(string ext)
        {
            return TempExtensions.Contains(ext) == false && Helper.GetFileDocType(ext) == Helper.ConstStrings.Music;
        }

        static bool IsMusicFile(FileInfo f)
        {
            return f.Attributes.HasFlag(FileAttributes.Hidden) == false && IsMusicExt(f.Extension);
        }

        bool IsStable(long lastChange, long now)
        {
            return now - lastChange >= (long)(config.News.StableMinutes * 60 * 1000);
        }

        static bool IsKnownFileType(string ext)
        {
            string t = Helper.GetFileDocType(ext);
            return t == Helper.ConstStrings.Video || t == Helper.ConstStrings.Music || t == Helper.ConstStrings.Image || t == Helper.ConstStrings.Text;
        }

        // 這個路徑看起來是檔案嗎（用副檔名判斷，副檔名不認得時才問檔案系統，因為資料夾名稱也可能有點，像 Vol.1）
        static bool LooksLikeFile(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                return false;
            }
            if (TempExtensions.Contains(ext) || IsKnownFileType(ext))
            {
                return true;
            }
            return Directory.Exists(path) == false;
        }

        // ---------- 事件 → 檢查工作 ----------

        void DrainEvents()
        {
            FsEvent ev;
            while (fsEvents.TryDequeue(out ev))
            {
                try
                {
                    if (ev.Kind == "renamed")
                    {
                        HandleRenamed(ev);
                    }
                    else if (ev.Kind == "deleted")
                    {
                        HandleDeleted(ev.Path);
                    }
                    else
                    {
                        HandleCreated(ev.Path);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "新上架處理事件失敗 {0}", ev.Path);
                }
            }
        }

        void HandleCreated(string path)
        {
            FolderConfig root;
            NewsShareConfig sc = ShareOf(path, out root);
            if (sc == null)
            {
                return;
            }
            NewsShareState st = ShareState(sc);
            if (st.Baselined == false)
            {
                return;        // 還沒建立基準：第一次完整掃描會把現有的全部當成已知
            }
            MarkFromPath(sc, st, path);
        }

        // 刪除（或搬走）：把對應的快照紀錄暫存成墓碑。只處理快照裡有的，所以不會被大量無關事件拖累
        void HandleDeleted(string path)
        {
            FolderConfig root;
            NewsShareConfig sc = ShareOf(path, out root);
            if (sc == null)
            {
                return;
            }
            NewsShareState st = ShareState(sc);
            string rel = RelPath(path);
            if (st.Baselined == false || rel == null)
            {
                return;
            }
            bool albums = string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase);
            if (albums)
            {
                var keys = st.Albums.Keys.Where(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (keys.Count > 0)
                {
                    Tomb t = new Tomb { OldRel = rel, Name = NameOf(rel), Time = DateTime.Now, Albums = new Dictionary<string, NewsAlbumState>() };
                    foreach (string k in keys)
                    {
                        t.Albums[k] = st.Albums[k];
                        st.Albums.Remove(k);
                    }
                    tombs.Add(t);
                    stateDirty = true;
                }
                return;
            }
            var unitKeys = st.Units.Keys.Where(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            if (unitKeys.Count > 0)
            {
                Tomb t = new Tomb { OldRel = rel, Name = NameOf(rel), Time = DateTime.Now, Units = new Dictionary<string, NewsUnitState>() };
                foreach (string k in unitKeys)
                {
                    t.Units[k] = st.Units[k];
                    st.Units.Remove(k);
                }
                tombs.Add(t);
                stateDirty = true;
                return;
            }
            // 作品資料夾裡的單一檔案被刪除或搬走
            NewsUnitState us;
            NewsFileState fs;
            if (st.Units.TryGetValue(ParentRel(rel), out us) && us.Files.TryGetValue(NameOf(rel), out fs))
            {
                us.Files.Remove(NameOf(rel));
                tombs.Add(new Tomb { OldRel = rel, Name = NameOf(rel), Time = DateTime.Now, IsFile = true, File = fs, FileSize = fs.Size });
                stateDirty = true;
            }
        }

        void ExpireTombs()
        {
            tombs.RemoveAll(t => (DateTime.Now - t.Time).TotalSeconds > TombstoneSeconds);
        }

        // 這個路徑底下在快照裡完全沒有紀錄，但剛剛有同名資料夾被刪除／搬走：把紀錄接回新路徑（並讓已產生的通知跟著改路徑）
        void TryRestoreFromTomb(NewsShareConfig sc, NewsShareState st, string rel)
        {
            bool albums = string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase);
            bool hasState = albums
                ? st.Albums.Keys.Any(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase))
                : st.Units.Keys.Any(k => string.Equals(k, rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase));
            if (hasState)
            {
                return;
            }
            string name = NameOf(rel);
            Tomb tomb = tombs.FirstOrDefault(t => t.IsFile == false && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                && (albums ? t.Albums != null : t.Units != null)
                && string.Equals(t.OldRel, rel, StringComparison.OrdinalIgnoreCase) == false);
            if (tomb == null)
            {
                return;
            }
            if (albums)
            {
                foreach (var kv in tomb.Albums)
                {
                    st.Albums[rel + kv.Key.Substring(tomb.OldRel.Length)] = kv.Value;
                }
            }
            else
            {
                foreach (var kv in tomb.Units)
                {
                    st.Units[rel + kv.Key.Substring(tomb.OldRel.Length)] = kv.Value;
                }
            }
            tombs.Remove(tomb);
            UpdateEventPaths(tomb.OldRel, rel);
            stateDirty = true;
            logger.Debug("新上架：偵測到搬移 {0} -> {1}，已接回快照紀錄", tomb.OldRel, rel);
        }

        // 新出現的檔案如果剛剛有同名同大小的檔案被刪除（搬移），沿用它的紀錄
        NewsFileState TakeFileTomb(string name, long size)
        {
            Tomb tomb = tombs.FirstOrDefault(t => t.IsFile && t.FileSize == size && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tomb == null)
            {
                return null;
            }
            tombs.Remove(tomb);
            return tomb.File;
        }

        void HandleRenamed(FsEvent ev)
        {
            FolderConfig rootOld, rootNew;
            NewsShareConfig scOld = ShareOf(ev.OldPath, out rootOld);
            NewsShareConfig scNew = ShareOf(ev.Path, out rootNew);
            if (scNew == null)
            {
                return;        // 搬到分享根外面等於刪除：不處理
            }
            if (scOld == null || string.Equals(scOld.Name, scNew.Name, StringComparison.OrdinalIgnoreCase) == false)
            {
                HandleCreated(ev.Path);        // 從外面（或別的分享根）搬進來：當成新增
                return;
            }
            NewsShareState st = ShareState(scNew);
            if (st.Baselined == false)
            {
                return;
            }
            ApplyRename(scNew, st, ev.OldPath, ev.Path);
            MarkFromPath(scNew, st, ev.Path);
        }

        void AddWork(Target t, double dueSeconds)
        {
            Target existing;
            DateTime due = DateTime.Now.AddSeconds(dueSeconds);
            if (work.TryGetValue(t.Key, out existing))
            {
                existing.Due = due;
                return;
            }
            t.Due = due;
            work[t.Key] = t;
        }

        // 只做字串運算（不碰檔案系統），因為大量複製時會有成千上萬個事件
        void MarkFromPath(NewsShareConfig sc, NewsShareState st, string path)
        {
            string rel = RelPath(path);
            if (rel == null)
            {
                return;
            }
            string[] segs = Segments(rel);
            int depth = segs.Length - 1;
            if (depth <= 0)
            {
                return;
            }
            if (string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase))
            {
                int maxDepth = Math.Max(1, sc.MaxDepth);
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) == false && LooksLikeFile(path))
                {
                    // 檔案：只有音樂檔有意義；專輯就是它所在的資料夾（光碟資料夾由母資料夾代表）
                    if (IsMusicExt(ext) == false)
                    {
                        return;
                    }
                    string dirRel = ParentRel(rel);
                    int dirDepth = DepthOf(dirRel);
                    if (dirDepth < 1 || dirDepth > maxDepth)
                    {
                        return;
                    }
                    string albumRel = (dirDepth >= 2 && IsDiscName(NameOf(dirRel))) ? ParentRel(dirRel) : dirRel;
                    NewsAlbumState known;
                    if (st.Albums.TryGetValue(albumRel, out known) && known.Announced)
                    {
                        return;        // 已通知過的專輯補進歌曲：不算，連檢查都不用
                    }
                    AddWork(new Target { Kind = "album", Share = sc.Name, Rel = albumRel }, DebounceSeconds);
                }
                else if (depth <= maxDepth)
                {
                    AddWork(new Target { Kind = "walk", Share = sc.Name, Rel = rel }, DebounceSeconds);
                }
                return;
            }
            // episodes
            int d = Math.Max(1, sc.Depth);
            if (depth >= d + 1)
            {
                // 作品資料夾底下的任何東西（檔案或子資料夾）：檢查它所屬的作品資料夾
                string unitRel = "/" + string.Join("/", segs.Take(d + 1));
                AddWork(new Target { Kind = "unit", Share = sc.Name, Rel = unitRel }, DebounceSeconds);
            }
            else if (depth == d)
            {
                // 作品資料夾本身，或直接放在季資料夾底下的檔案（後者不算）
                if (LooksLikeFile(path) == false)
                {
                    AddWork(new Target { Kind = "unit", Share = sc.Name, Rel = rel }, DebounceSeconds);
                }
            }
            else if (LooksLikeFile(path) == false)
            {
                // 比作品資料夾更上層的資料夾（例如整個季資料夾複製進來）：往下找出所有作品資料夾
                AddWork(new Target { Kind = "walk", Share = sc.Name, Rel = rel }, DebounceSeconds);
            }
        }

        // ---------- 改名／搬移：搬移快照紀錄與已產生的通知，不通知 ----------

        static void MovePrefix<T>(Dictionary<string, T> dict, string oldRel, string newRel)
        {
            List<string> keys = dict.Keys.Where(k => string.Equals(k, oldRel, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            List<KeyValuePair<string, T>> moved = new List<KeyValuePair<string, T>>();
            foreach (string k in keys)
            {
                moved.Add(new KeyValuePair<string, T>(newRel + k.Substring(oldRel.Length), dict[k]));
                dict.Remove(k);
            }
            foreach (var kv in moved)
            {
                dict[kv.Key] = kv.Value;
            }
        }

        void ApplyRename(NewsShareConfig sc, NewsShareState st, string oldFull, string newFull)
        {
            string oldRel = RelPath(oldFull), newRel = RelPath(newFull);
            if (oldRel == null || newRel == null || string.Equals(oldRel, newRel, StringComparison.Ordinal))
            {
                return;
            }
            bool isDir = Directory.Exists(newFull);
            if (isDir)
            {
                MovePrefix(st.Units, oldRel, newRel);
                MovePrefix(st.Albums, oldRel, newRel);
                UpdateEventPaths(oldRel, newRel);
                logger.Debug("新上架：資料夾改名／搬移 {0} -> {1}，已搬移快照紀錄", oldRel, newRel);
            }
            else if (string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase) == false)
            {
                // 檔案改名／搬到別的作品資料夾：帶著「是否已通知過」的紀錄一起搬，這樣不會被當成新集數
                string oldDir = ParentRel(oldRel), newDir = ParentRel(newRel);
                NewsUnitState ou;
                NewsFileState fs;
                if (st.Units.TryGetValue(oldDir, out ou) && ou.Files.TryGetValue(NameOf(oldRel), out fs))
                {
                    ou.Files.Remove(NameOf(oldRel));
                    NewsUnitState nu = null;
                    if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                    {
                        nu = ou;
                    }
                    else
                    {
                        st.Units.TryGetValue(newDir, out nu);
                    }
                    if (nu != null)
                    {
                        nu.Files[NameOf(newRel)] = fs;
                    }
                    if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateEventFileName(oldDir, NameOf(oldRel), NameOf(newRel));        // 剛上架的集數改名：「新」標記跟著走
                    }
                }
            }
            stateDirty = true;
        }

        void UpdateEventFileName(string dirRel, string oldName, string newName)
        {
            EnsureLoaded();
            lock (gate)
            {
                bool changed = false;
                foreach (NewsEvent e in events)
                {
                    if (e.Files == null || string.Equals(e.Path, dirRel, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        continue;
                    }
                    int i = e.Files.FindIndex(f => string.Equals(f, oldName, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        e.Files[i] = newName;
                        changed = true;
                    }
                }
                if (changed)
                {
                    Persist();
                }
            }
        }

        void UpdateEventPaths(string oldRel, string newRel)
        {
            EnsureLoaded();
            lock (gate)
            {
                bool changed = false;
                foreach (NewsEvent e in events)
                {
                    if (string.Equals(e.Path, oldRel, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Path = newRel;
                        e.Title = e.Kind == "album" ? NameOf(newRel) : ratingService.Resolve(NameOf(newRel)).Plain;
                        changed = true;
                    }
                    else if (e.Path != null && e.Path.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Path = newRel + e.Path.Substring(oldRel.Length);
                        changed = true;
                    }
                }
                if (changed)
                {
                    Persist();
                }
            }
        }

        // ---------- 單一資料夾的小掃描 ----------

        void ProcessWork()
        {
            DateTime now = DateTime.Now;
            List<Target> due = work.Values.Where(t => t.Due <= now).ToList();
            foreach (Target t in due)
            {
                work.Remove(t.Key);
                try
                {
                    Rescan(t);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "新上架小掃描失敗 {0}", t.Rel);
                }
            }
        }

        void Rescan(Target t)
        {
            NewsShareConfig sc = ShareConfig(t.Share);
            if (sc == null)
            {
                return;
            }
            NewsShareState st = ShareState(sc);
            if (st.Baselined == false)
            {
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ScanStats stats = new ScanStats { Full = true };       // 小掃描一律不看修改時間
            long now = NowMs();
            string abs = AbsPath(t.Rel);
            if (abs == null || Directory.Exists(abs) == false)
            {
                return;
            }
            bool albums = string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase);
            TryRestoreFromTomb(sc, st, t.Rel);
            if (t.Kind == "unit")
            {
                ProcessUnit(sc, st, new DirectoryInfo(abs), t.Rel, false, true, now, stats);
                if (UnitPending(st, t.Rel))
                {
                    AddWork(new Target { Kind = "unit", Share = sc.Name, Rel = t.Rel }, PendingRecheckSeconds);
                }
            }
            else if (albums)
            {
                Dictionary<string, FoundAlbum> found = new Dictionary<string, FoundAlbum>();
                DirectoryInfo dir = new DirectoryInfo(abs);
                if (t.Kind == "album")
                {
                    stats.DirQueries++;
                    FileSystemInfo[] entries = dir.GetFileSystemInfos();
                    DirectoryInfo[] subs = entries.OfType<DirectoryInfo>().Where(d => IsHidden(d) == false).ToArray();
                    FoundAlbum f = EvaluateAlbum(dir, t.Rel, DepthOf(t.Rel), entries, subs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), stats);
                    if (f != null)
                    {
                        found[t.Rel] = f;
                    }
                }
                else
                {
                    WalkAlbums(dir, DepthOf(t.Rel), Math.Max(1, sc.MaxDepth), found, st, stats);
                }
                if (t.Kind != "album")
                {
                    foreach (string rel in found.Keys.ToList())
                    {
                        TryRestoreFromTomb(sc, st, rel);
                    }
                }
                ApplyFoundAlbums(sc, st, found, false, now, stats);
                foreach (string rel in found.Keys)
                {
                    NewsAlbumState a;
                    if (st.Albums.TryGetValue(rel, out a) && a.Announced == false)
                    {
                        AddWork(new Target { Kind = "album", Share = sc.Name, Rel = rel }, PendingRecheckSeconds);
                    }
                }
            }
            else
            {
                // episodes，上層資料夾（整個季資料夾複製進來）：往下找出所有作品資料夾逐一處理
                foreach (DirectoryInfo unit in UnitsUnder(new DirectoryInfo(abs), DepthOf(t.Rel), Math.Max(1, sc.Depth), stats))
                {
                    string rel = RelPath(unit.FullName);
                    if (rel == null)
                    {
                        continue;
                    }
                    TryRestoreFromTomb(sc, st, rel);        // 剛搬進來的季資料夾底下的作品：先接回搬走前的紀錄
                    ProcessUnit(sc, st, unit, rel, false, true, now, stats);
                    if (UnitPending(st, rel))
                    {
                        AddWork(new Target { Kind = "unit", Share = sc.Name, Rel = rel }, PendingRecheckSeconds);
                    }
                }
            }
            stateDirty = true;
            if (stats.Events > 0)
            {
                logger.Info("新上架（即時）{0} {1}：耗時 {2}ms，查詢 {3} 個資料夾，新增 {4} 筆通知", sc.Name, t.Rel, watch.ElapsedMilliseconds, stats.DirQueries, stats.Events);
            }
            else
            {
                logger.Debug("新上架（即時）{0} {1}：耗時 {2}ms，查詢 {3} 個資料夾，沒有新增通知", sc.Name, t.Rel, watch.ElapsedMilliseconds, stats.DirQueries);
            }
        }

        static bool UnitPending(NewsShareState st, string rel)
        {
            NewsUnitState us;
            return st.Units.TryGetValue(rel, out us) && (us.Announced == false || us.Files.Values.Any(f => f.Announced == false));
        }

        // 從 dir（深度 depth）往下找出深度 d 的資料夾（作品資料夾）
        List<DirectoryInfo> UnitsUnder(DirectoryInfo dir, int depth, int d, ScanStats stats)
        {
            List<DirectoryInfo> result = new List<DirectoryInfo>();
            if (depth >= d)
            {
                result.Add(dir);
                return result;
            }
            stats.DirQueries++;
            foreach (DirectoryInfo sub in dir.GetDirectories().Where(x => IsHidden(x) == false))
            {
                result.AddRange(UnitsUnder(sub, depth + 1, d, stats));
            }
            return result;
        }

        // ---------- 完整掃描（保險） ----------

        void RunFullScans(Dictionary<string, ShareRuntime> runtime)
        {
            foreach (NewsShareConfig sc in config.News.Shares)
            {
                ShareRuntime rt = runtime[sc.Name];
                if (rt.NextDue > DateTime.Now)
                {
                    continue;
                }
                NewsShareState st;
                bool baselined = state.Shares.TryGetValue(sc.Name, out st) && st.Baselined;
                // 時段外不做定期的完整掃描；但還沒建立基準的要先掃一次，不然之後進來的東西會被當成已知而漏掉；監看重建後的補掃也不受時段限制
                if (baselined && rt.ForceFull == false && InWindow(DateTime.Now) == false)
                {
                    rt.NextDue = NextWindowStart(DateTime.Now);
                    logger.Info("新上架完整掃描 {0}：目前不在掃描時段內，下一次 {1:MM/dd HH:mm:ss}", sc.Name, rt.NextDue);
                    continue;
                }
                ScanShare(sc, rt);
                DateTime planned = DateTime.Now.AddSeconds(IntervalSeconds(sc));
                rt.NextDue = NextWindowStart(planned);
                if (rt.NextDue > planned.AddSeconds(1))
                {
                    logger.Info("新上架完整掃描 {0}：下一次排在 {1:MM/dd HH:mm:ss}（掃描時段外，延到時段開始）", sc.Name, rt.NextDue);
                }
            }
        }

        void ScanShare(NewsShareConfig sc, ShareRuntime rt)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            FolderConfig root = config.Folders.FirstOrDefault(f => string.Equals(f.Title, sc.Name, StringComparison.OrdinalIgnoreCase));
            if (root == null || Directory.Exists(root.Path) == false)
            {
                // 網路磁碟暫時連不上時不要動快照，否則恢復連線後所有東西都會被當成新的
                logger.Warn("新上架完整掃描：找不到分享根 {0}，略過", sc.Name);
                return;
            }
            NewsShareState st = ShareState(sc);
            long now = NowMs();
            ScanStats stats = new ScanStats
            {
                Full = st.Baselined == false || rt.ForceFull || now - st.LastFullScan >= (long)(config.News.FullScanHours * 3600 * 1000)
            };
            bool albums = string.Equals(sc.Mode, "albums", StringComparison.OrdinalIgnoreCase);
            try
            {
                bool reliable = albums ? ScanAlbums(sc, root, st, stats) : ScanEpisodes(sc, root, st, stats);
                if (reliable == false)
                {
                    return;
                }
                if (stats.Full)
                {
                    st.LastFullScan = now;
                }
                rt.ForceFull = false;
                // 還在等大小穩定的項目交給小掃描去複查
                if (albums)
                {
                    foreach (var kv in st.Albums.Where(a => a.Value.Announced == false))
                    {
                        AddWork(new Target { Kind = "album", Share = sc.Name, Rel = kv.Key }, PendingRecheckSeconds);
                    }
                }
                else
                {
                    foreach (var kv in st.Units.Where(u => u.Value.Announced == false || u.Value.Files.Values.Any(f => f.Announced == false)))
                    {
                        AddWork(new Target { Kind = "unit", Share = sc.Name, Rel = kv.Key }, PendingRecheckSeconds);
                    }
                }
                stateDirty = true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "新上架完整掃描 {0} 失敗", sc.Name);
                return;
            }
            logger.Info("新上架完整掃描 {0}({1})：耗時 {2}ms，查詢 {3} 個資料夾，略過 {4} 個沒變動的{5}，新增 {6} 筆通知{7}",
                sc.Name, albums ? "albums" : "episodes", watch.ElapsedMilliseconds, stats.DirQueries, stats.Skipped,
                albums ? "專輯" : "作品資料夾", stats.Events, stats.Full ? "（不看修改時間的完整掃描）" : "");
        }

        // 回傳 false 表示這次掃描不可信（沒動快照）
        bool ScanEpisodes(NewsShareConfig sc, FolderConfig root, NewsShareState st, ScanStats stats)
        {
            // 走到作品資料夾那一層；子資料夾是從父資料夾的查詢順便拿到的，不必再各查一次
            List<DirectoryInfo> units = new List<DirectoryInfo> { new DirectoryInfo(root.Path) };
            for (int level = 0; level < Math.Max(1, sc.Depth); level++)
            {
                List<DirectoryInfo> next = new List<DirectoryInfo>();
                foreach (DirectoryInfo d in units)
                {
                    stats.DirQueries++;
                    next.AddRange(d.GetDirectories().Where(x => IsHidden(x) == false));
                }
                units = next;
            }
            // 列不到任何資料夾、但快照裡有很多：很可能是磁碟暫時沒回應，不可信
            if (units.Count == 0 && st.Units.Count > 5)
            {
                logger.Warn("新上架完整掃描：{0} 列不到任何作品資料夾（快照有 {1} 筆），略過這次", sc.Name, st.Units.Count);
                return false;
            }
            long now = NowMs();
            bool baseline = st.Baselined == false;
            foreach (DirectoryInfo unit in units)
            {
                string rel = RelPath(unit.FullName);
                if (rel != null)
                {
                    ProcessUnit(sc, st, unit, rel, baseline, stats.Full, now, stats);
                }
            }
            st.Baselined = true;
            return true;
        }

        void ProcessUnit(NewsShareConfig sc, NewsShareState st, DirectoryInfo unit, string rel, bool baseline, bool force, long now, ScanStats stats)
        {
            // 先取資料夾修改時間再列檔案：列檔案期間如果又有變動，下一輪日期對不上就會再列一次。
            // 一定要對這個資料夾「本身」重新查詢（Refresh）：從父資料夾的列表拿到的子資料夾修改時間，在 NTFS 上會延遲更新（實測舊了一個多月都有可能），
            // 拿它判斷會漏掉新集數。重新查詢是一次輕量的屬性查詢，比列出所有檔案便宜，而且準
            unit.Refresh();
            stats.DirQueries++;
            long mtime = unit.LastWriteTimeUtc.Ticks;
            NewsUnitState us;
            bool known = st.Units.TryGetValue(rel, out us);
            if (known == false)
            {
                us = new NewsUnitState { Announced = baseline };
                st.Units[rel] = us;
            }
            bool pending = us.Announced == false || us.Files.Values.Any(f => f.Announced == false);
            if (known && force == false && pending == false && us.Mtime == mtime)
            {
                stats.Skipped++;       // 新集數一定是資料夾「直接」多一個檔案（資料夾修改時間會變），沒變就不用列
                return;
            }
            stats.DirQueries++;
            int newlyStable = 0;
            List<string> newNames = new List<string>();
            foreach (FileInfo f in unit.GetFiles())
            {
                if (f.Attributes.HasFlag(FileAttributes.Hidden) || IsVideoExt(f.Extension) == false)
                {
                    continue;
                }
                NewsFileState fs;
                if (us.Files.TryGetValue(f.Name, out fs) == false)
                {
                    fs = baseline ? null : TakeFileTomb(f.Name, f.Length);         // 剛從別的作品資料夾搬過來的檔案：沿用原本的紀錄
                    if (fs == null)
                    {
                        fs = new NewsFileState { Size = f.Length, LastChange = now, Announced = baseline };
                    }
                    us.Files[f.Name] = fs;
                }
                if (fs.Announced)
                {
                    continue;
                }
                if (fs.Size != f.Length)
                {
                    fs.Size = f.Length;
                    fs.LastChange = now;
                }
                else if (IsStable(fs.LastChange, now))
                {
                    fs.Announced = true;
                    newlyStable++;
                    newNames.Add(f.Name);
                }
            }
            us.Mtime = mtime;
            if (newlyStable > 0)
            {
                string kind = us.Announced ? "episodes" : "series";
                us.Announced = true;
                AddEvent(sc.Name, kind, rel, ratingService.Resolve(unit.Name).Plain, newlyStable, now, newNames);
                stats.Events++;
            }
        }

        class FoundAlbum
        {
            public string Name;
            public int Tracks;
            public long Size;
            public long Mtime;
        }

        bool ScanAlbums(NewsShareConfig sc, FolderConfig root, NewsShareState st, ScanStats stats)
        {
            // 找出所有專輯資料夾：相對路徑 → 資訊（已通知過、修改時間沒變的專輯不會被列出內容，也就不在這裡）
            Dictionary<string, FoundAlbum> found = new Dictionary<string, FoundAlbum>();
            WalkAlbums(new DirectoryInfo(root.Path), 0, Math.Max(1, sc.MaxDepth), found, st, stats);
            if (found.Count + stats.Skipped == 0 && st.Albums.Count > 5)
            {
                logger.Warn("新上架完整掃描：{0} 找不到任何專輯（快照有 {1} 筆），略過這次", sc.Name, st.Albums.Count);
                return false;
            }
            ApplyFoundAlbums(sc, st, found, st.Baselined == false, NowMs(), stats);
            st.Baselined = true;
            return true;
        }

        void ApplyFoundAlbums(NewsShareConfig sc, NewsShareState st, Dictionary<string, FoundAlbum> found, bool baseline, long now, ScanStats stats)
        {
            foreach (var kv in found)
            {
                NewsAlbumState a;
                if (st.Albums.TryGetValue(kv.Key, out a) == false)
                {
                    a = new NewsAlbumState { Size = kv.Value.Size, LastChange = now, Announced = baseline };
                    st.Albums[kv.Key] = a;
                }
                a.Mtime = kv.Value.Mtime;
                if (a.Announced)
                {
                    continue;      // 已經通知過（或是基準）的專輯，之後補進歌曲不再通知
                }
                if (a.Size != kv.Value.Size)
                {
                    a.Size = kv.Value.Size;
                    a.LastChange = now;
                }
                else if (IsStable(a.LastChange, now))
                {
                    a.Announced = true;
                    AddEvent(sc.Name, "album", kv.Key, kv.Value.Name, kv.Value.Tracks, now);
                    stats.Events++;
                }
            }
        }

        // 評估單一資料夾是不是一張專輯；不是回傳 null。skip 會填入「已併入這張專輯、不必再單獨往下找」的光碟資料夾名稱
        FoundAlbum EvaluateAlbum(DirectoryInfo dir, string rel, int depth, FileSystemInfo[] entries, DirectoryInfo[] subs, HashSet<string> skip, ScanStats stats)
        {
            if (depth <= 0 || rel == null)
            {
                return null;        // 分享根本身不算
            }
            List<FileInfo> music = entries.OfType<FileInfo>().Where(IsMusicFile).ToList();
            if (music.Count > 0)
            {
                if (depth >= 2 && IsDiscName(dir.Name))
                {
                    return null;    // 光碟資料夾由母資料夾代表
                }
                return new FoundAlbum { Name = dir.Name, Tracks = music.Count, Size = music.Sum(f => f.Length), Mtime = dir.LastWriteTimeUtc.Ticks };
            }
            // 光碟資料夾（Disc 1 / CD2…）底下有音樂：以母資料夾為一張專輯
            DirectoryInfo[] discs = subs.Where(d => IsDiscName(d.Name)).ToArray();
            if (discs.Length > 0)
            {
                List<FileInfo> all = new List<FileInfo>();
                foreach (DirectoryInfo d in discs)
                {
                    stats.DirQueries++;
                    all.AddRange(d.GetFiles().Where(IsMusicFile));
                }
                if (all.Count > 0)
                {
                    foreach (DirectoryInfo d in discs)
                    {
                        skip.Add(d.Name);
                    }
                    return new FoundAlbum { Name = dir.Name, Tracks = all.Count, Size = all.Sum(f => f.Length), Mtime = dir.LastWriteTimeUtc.Ticks };
                }
            }
            return null;
        }

        void WalkAlbums(DirectoryInfo dir, int depth, int maxDepth, Dictionary<string, FoundAlbum> found, NewsShareState st, ScanStats stats)
        {
            // 一次查詢同時取得子資料夾與檔案
            stats.DirQueries++;
            FileSystemInfo[] entries = dir.GetFileSystemInfos();
            DirectoryInfo[] subs = entries.OfType<DirectoryInfo>().Where(d => IsHidden(d) == false).ToArray();
            HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string rel = RelPath(dir.FullName);
            FoundAlbum album = EvaluateAlbum(dir, rel, depth, entries, subs, skip, stats);
            if (album != null)
            {
                found[rel] = album;
            }
            if (depth >= maxDepth)
            {
                return;
            }
            foreach (DirectoryInfo sub in subs)
            {
                if (skip.Contains(sub.Name) || MainService.AuxFolderRegex.IsMatch(sub.Name))
                {
                    continue;
                }
                // 已通知過（或基準裡）的專輯當成葉節點：修改時間沒變就整個略過，連裡面都不列
                string subRel = RelPath(sub.FullName);
                NewsAlbumState known;
                if (stats.Full == false && subRel != null && st.Albums.TryGetValue(subRel, out known)
                    && known.Announced && known.Mtime == sub.LastWriteTimeUtc.Ticks)
                {
                    stats.Skipped++;
                    continue;
                }
                WalkAlbums(sub, depth + 1, maxDepth, found, st, stats);
            }
        }

        // ---------- 事件 ----------

        void AddEvent(string share, string kind, string path, string title, int count, long now, List<string> files = null)
        {
            EnsureLoaded();
            lock (gate)
            {
                // 同一部動畫 12 小時內陸續上架的集數，合併成同一筆（並更新時間，所以會再算成未讀）
                if (kind != "album")
                {
                    NewsEvent last = events.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) && e.Kind != "album")
                        .OrderByDescending(e => e.Time).FirstOrDefault();
                    if (last != null && now - last.Time < MergeWindowMs)
                    {
                        last.Count += count;
                        last.Time = now;
                        if (files != null && files.Count > 0)
                        {
                            last.Files = (last.Files ?? new List<string>()).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEventFiles).ToList();
                        }
                        Trim();
                        Persist();
                        logger.Info("新上架（合併）：{0} +{1}", title, count);
                        return;
                    }
                }
                events.Add(new NewsEvent { Id = Guid.NewGuid().ToString("N"), Share = share, Kind = kind, Path = path, Title = title, Count = count, Time = now,
                    Files = (files == null || files.Count == 0) ? null : files.Take(MaxEventFiles).ToList() });
                Trim();
                Persist();
                logger.Info("新上架：[{0}] {1} {2}（{3}）", share, kind, title, count);
            }
        }

        void Trim()
        {
            long cutoff = NowMs() - MaxEventAgeDays * 86400000L;
            events.RemoveAll(e => e.Time < cutoff);
            if (events.Count > MaxEvents)
            {
                events = events.OrderByDescending(e => e.Time).Take(MaxEvents).ToList();
            }
        }

        void Persist()
        {
            try { SaveJson(DataPath("news.json"), events); } catch (Exception ex) { logger.Warn(ex, "寫入 news.json 失敗"); }
        }
    }
}
