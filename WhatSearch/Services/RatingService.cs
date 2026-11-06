using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using WhatSearch.Core;
using WhatSearch.Models;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public interface IRatingService
    {
        /// <summary>這個資料夾是否可評分（依設定的分享根與層數）</summary>
        bool IsRateable(string folderFullPath);
        /// <summary>把可評分資料夾的「顯示名稱（去掉星等後綴）、評分」填進檢視；不可評分就不動</summary>
        void Apply(FileInfoView view, string folderFullPath);
        /// <summary>資料夾名稱 → 評分用的名稱與有效評分（系統記錄優先，沒有就用名稱結尾的 (★★★☆)）</summary>
        RatingInfo Resolve(string folderName);
        /// <summary>設定評分（0 = 未評分），key 為 Resolve 回傳的 Key</summary>
        void Set(string key, double rating);
        /// <summary>這個資料夾位在啟用專輯評分的分享根底下，而且不是分享根本身、也不是專輯裡的光碟或附屬資料夾（Disc 1、Scans…）；還要由呼叫端確認它是專輯</summary>
        bool IsAlbumShare(string folderFullPath);
        /// <summary>專輯名稱 → 評分用的名稱與評分（key 加 album| 前綴，與動畫分開）；沒有評分為 null</summary>
        RatingInfo ResolveAlbum(string folderName);
        /// <summary>列表上的專輯資料夾：已評分才把評分填進 AlbumRating</summary>
        void ApplyAlbumListRating(FileInfoView view, string folderFullPath);
        /// <summary>啟動資料夾改名的監看：評分記錄跟著新名稱走（沒啟用評分就什麼都不做）</summary>
        void StartWatch();
        /// <summary>記錄總筆數（不掃資料夾）</summary>
        int Count();
        /// <summary>掃描分享根，找出評分記錄裡對不到資料夾的項目（要走訪資料夾，網路磁碟上會慢，只在管理者要求時才做）</summary>
        RatingScanResult Scan();
        /// <summary>刪除指定的髒資料（會重新掃描，只刪仍然對不到資料夾的；刪之前先備份 ratings.json）。回傳實際刪除筆數</summary>
        int DeleteOrphans(IEnumerable<string> keys, out string backupName);
    }

    public class RatingInfo
    {
        /// <summary>去掉結尾星等、空白收斂後的名稱，同時也是評分的 key</summary>
        public string Key { get; set; }
        /// <summary>去掉結尾星等後的名稱（保留原本的空白，只去頭尾）</summary>
        public string Plain { get; set; }
        /// <summary>有效評分：0.5~5；沒有評分為 null；0 代表使用者明確設為「未評分」</summary>
        public double? Rating { get; set; }
    }

    /// <summary>
    /// 動畫（追番）的星等評分，整個系統共用一份，存在 data/ratings.json（{ 名稱: 評分 }）。
    /// 評分記在系統裡而不是改資料夾名稱，所以不會改變資料夾 id（續播進度、收藏、書籤都不受影響）；
    /// key 是去掉結尾星等的名稱，動畫搬到別的資料夾評分仍然跟著走。
    /// 系統裡沒有評分時，沿用資料夾名稱結尾的「(★★★☆)」（★ 算 1、結尾的 ☆ 算 0.5）當預設值。
    /// </summary>
    public class RatingService : IRatingService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        static readonly object thisLock = new object();
        // 名稱結尾的 (★★★☆)，全形括號也接受
        static readonly Regex SuffixRegex = new Regex(@"\s*[\(（]\s*([★☆]+)\s*[\)）]\s*$", RegexOptions.Compiled);
        static readonly Regex SpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        readonly SystemConfig config = Ioc.GetConfig();
        Dictionary<string, double> cache;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "ratings.json");
        }

        Dictionary<string, double> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            cache = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        cache = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 ratings.json 失敗，以空白資料開始");
            }
            return cache;
        }

        public bool IsRateable(string folderFullPath)
        {
            var rc = config.Rating;
            if (rc == null || rc.Shares == null || rc.Shares.Count == 0 || string.IsNullOrEmpty(folderFullPath))
            {
                return false;
            }
            string rel;
            if (PathUtility.TryGetRelPath(folderFullPath, out rel) == false)
            {
                return false;
            }
            string[] parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }
            // parts[0] 是分享根的名稱；其下第 N 層才是動畫（Anime/2026-4/作品 = 第 2 層）
            bool shareMatch = rc.Shares.Any(s => string.Equals(s, parts[0], StringComparison.OrdinalIgnoreCase));
            return shareMatch && parts.Length - 1 == Math.Max(1, rc.Depth);
        }

        public void Apply(FileInfoView view, string folderFullPath)
        {
            if (IsRateable(folderFullPath) == false)
            {
                return;
            }
            RatingInfo info = Resolve(Path.GetFileName(folderFullPath.TrimEnd('\\', '/')));
            view.Rateable = true;
            view.TitlePlain = info.Plain;
            view.Rating = info.Rating;
        }

        const string AlbumKeyPrefix = "album|";

        public bool IsAlbumShare(string folderFullPath)
        {
            var shares = config.Rating == null || config.Rating.Albums == null ? null : config.Rating.Albums.Shares;
            if (shares == null || shares.Count == 0 || string.IsNullOrEmpty(folderFullPath))
            {
                return false;
            }
            string rel;
            if (PathUtility.TryGetRelPath(folderFullPath, out rel) == false)
            {
                return false;
            }
            string[] parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || shares.Any(s => string.Equals(s, parts[0], StringComparison.OrdinalIgnoreCase)) == false)
            {
                return false;
            }
            string name = parts[parts.Length - 1];
            // 光碟資料夾（Disc 1）與附屬資料夾（Scans、Artwork）是專輯的一部分，評分記在它們的母資料夾
            return MainService.DiscNameRegex.IsMatch(name) == false && MainService.AuxFolderRegex.IsMatch(name) == false;
        }

        public RatingInfo ResolveAlbum(string folderName)
        {
            string plain = (folderName ?? string.Empty).Trim();
            string key = AlbumKeyPrefix + SpaceRegex.Replace(plain, " ");
            double? rating = null;
            lock (thisLock)
            {
                double v;
                if (Load().TryGetValue(key, out v))
                {
                    rating = v;
                }
            }
            return new RatingInfo { Key = key, Plain = plain, Rating = rating };
        }

        public void ApplyAlbumListRating(FileInfoView view, string folderFullPath)
        {
            if (IsAlbumShare(folderFullPath) == false)
            {
                return;
            }
            RatingInfo info = ResolveAlbum(Path.GetFileName(folderFullPath.TrimEnd('\\', '/')));
            if (info.Rating.HasValue && info.Rating.Value > 0)
            {
                view.AlbumRating = info.Rating;
            }
        }

        public RatingInfo Resolve(string folderName)
        {
            folderName = folderName ?? string.Empty;
            double? legacy = null;
            string plain = folderName;
            Match m = SuffixRegex.Match(folderName);
            if (m.Success)
            {
                string stars = m.Groups[1].Value;
                int full = stars.Count(c => c == '★');
                bool half = stars.Contains('☆');
                legacy = Math.Min(5.0, full + (half ? 0.5 : 0.0));
                plain = folderName.Substring(0, m.Index);
            }
            plain = plain.Trim();
            string key = SpaceRegex.Replace(plain, " ");
            double? effective = legacy;
            lock (thisLock)
            {
                double sys;
                if (Load().TryGetValue(key, out sys))
                {
                    effective = sys;
                }
            }
            return new RatingInfo { Key = key, Plain = plain, Rating = effective };
        }

        public void Set(string key, double rating)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            lock (thisLock)
            {
                var all = Load();
                all[key] = rating;
                Save(all);
            }
        }

        // 呼叫端要自己持有 thisLock
        void Save(Dictionary<string, double> all)
        {
            try
            {
                // 先寫暫存檔再取代，避免寫到一半當掉把評分清空；檔案被防毒等程式暫時鎖住時會自動重試
                SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(all, indented: true));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "寫入 ratings.json 失敗");
                throw;
            }
        }

        // 與 Resolve 相同的 key 規則：去掉結尾星等、空白收斂
        static string AnimeKey(string folderName)
        {
            string plain = SuffixRegex.Replace(folderName ?? string.Empty, string.Empty).Trim();
            return SpaceRegex.Replace(plain, " ");
        }

        static string AlbumKey(string folderName)
        {
            return AlbumKeyPrefix + SpaceRegex.Replace((folderName ?? string.Empty).Trim(), " ");
        }

        public int Count()
        {
            lock (thisLock)
            {
                return Load().Count;
            }
        }

        // ---------- 資料夾改名：評分記錄跟著走 ----------

        static List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        public void StartWatch()
        {
            bool anime = config.Rating != null && config.Rating.Shares != null && config.Rating.Shares.Count > 0;
            bool album = config.Rating != null && config.Rating.Albums != null && config.Rating.Albums.Shares != null && config.Rating.Albums.Shares.Count > 0;
            if ((anime || album) == false || config.Folders == null)
            {
                return;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (anime) { foreach (string n in config.Rating.Shares) { names.Add(n); } }
            if (album) { foreach (string n in config.Rating.Albums.Shares) { names.Add(n); } }
            foreach (FolderConfig f in config.Folders.Where(t => t != null && names.Contains(t.Title ?? string.Empty)))
            {
                try
                {
                    var w = new FileSystemWatcher(f.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.DirectoryName,
                        InternalBufferSize = 64 * 1024
                    };
                    string title = f.Title;
                    w.Renamed += (s, e) => OnFolderRenamed(e.OldFullPath, e.FullPath);
                    w.Error += (s, e) => logger.Warn("評分：{0} 的檔案監看出錯（{1}），改名可能沒有跟上；可到「評分管理」檢查", title, e.GetException() == null ? "?" : e.GetException().Message);
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                    logger.Info("評分：監看 {0} 的資料夾改名", title);
                }
                catch (Exception ex)
                {
                    logger.Warn("評分：{0} 的檔案監看啟動失敗（{1}），改名不會自動跟上", f.Title, ex.Message);
                }
            }
        }

        // 事件在背景執行緒，沒接住的例外會讓整個程式結束
        void OnFolderRenamed(string oldPath, string newPath)
        {
            try
            {
                if (Directory.Exists(newPath) == false)
                {
                    return;
                }
                string oldName = Path.GetFileName(oldPath.TrimEnd('\\', '/'));
                string newName = Path.GetFileName(newPath.TrimEnd('\\', '/'));
                if (IsRateable(newPath))
                {
                    MoveKey(AnimeKey(oldName), AnimeKey(newName));
                }
                if (IsAlbumShare(newPath))
                {
                    MoveKey(AlbumKey(oldName), AlbumKey(newName));
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "評分：處理資料夾改名失敗：{0} → {1}", oldPath, newPath);
            }
        }

        void MoveKey(string oldKey, string newKey)
        {
            if (oldKey == newKey)
            {
                return;
            }
            lock (thisLock)
            {
                var all = Load();
                double v;
                if (all.TryGetValue(oldKey, out v) == false)
                {
                    return;
                }
                if (all.ContainsKey(newKey))
                {
                    logger.Info("評分：「{0}」改名為「{1}」，但新名稱已有評分，兩筆都保留（舊的可到「評分管理」刪除）", oldKey, newKey);
                    return;
                }
                all.Remove(oldKey);
                all[newKey] = v;
                Save(all);
                logger.Info("評分：資料夾改名，評分 {0} 從「{1}」跟到「{2}」", v, oldKey, newKey);
            }
        }

        // ---------- 管理：找出對不到資料夾的評分 ----------

        public RatingScanResult Scan()
        {
            var result = new RatingScanResult();
            Dictionary<string, double> snapshot;
            lock (thisLock)
            {
                snapshot = new Dictionary<string, double>(Load(), StringComparer.Ordinal);
            }
            result.Total = snapshot.Count;

            HashSet<string> animeKeys = null, albumKeys = null;
            var rc = config.Rating;
            if (rc != null && rc.Shares != null && rc.Shares.Count > 0)
            {
                animeKeys = CollectKeys(rc.Shares, Math.Max(1, rc.Depth), true, AnimeKey, "動畫", result);
                result.AnimeChecked = animeKeys != null;
            }
            if (rc != null && rc.Albums != null && rc.Albums.Shares != null && rc.Albums.Shares.Count > 0)
            {
                albumKeys = CollectKeys(rc.Albums.Shares, int.MaxValue, false, AlbumKey, "專輯", result);
                result.AlbumChecked = albumKeys != null;
            }
            foreach (var kv in snapshot.OrderBy(t => t.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                bool isAlbum = kv.Key.StartsWith(AlbumKeyPrefix, StringComparison.Ordinal);
                var known = isAlbum ? albumKeys : animeKeys;
                if (known == null || known.Contains(kv.Key))
                {
                    continue;
                }
                result.Orphans.Add(new RatingOrphan
                {
                    Key = kv.Key,
                    Kind = isAlbum ? "album" : "anime",
                    Name = isAlbum ? kv.Key.Substring(AlbumKeyPrefix.Length) : kv.Key,
                    Rating = kv.Value
                });
            }
            return result;
        }

        /// <summary>走訪分享根，回傳目前存在的資料夾所對應的 key 集合；任何一個分享根讀不到（沒掛載、權限）就回傳 null（不可驗證，絕不能把全部評分當髒資料）</summary>
        HashSet<string> CollectKeys(List<string> shareNames, int depth, bool exactDepth, Func<string, string> keyOf, string label, RatingScanResult result)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string shareName in shareNames)
            {
                FolderConfig root = (config.Folders ?? new List<FolderConfig>()).FirstOrDefault(f => f != null && string.Equals(f.Title, shareName, StringComparison.OrdinalIgnoreCase));
                if (root == null || string.IsNullOrEmpty(root.Path) || Directory.Exists(root.Path) == false)
                {
                    result.Errors.Add(label + "：找不到分享根「" + shareName + "」（沒掛載或路徑不存在），所以不檢查" + label + "評分");
                    return null;
                }
                try
                {
                    Walk(root.Path, 1, depth, exactDepth, keyOf, keys);
                }
                catch (Exception ex)
                {
                    result.Errors.Add(label + "：讀取「" + shareName + "」失敗（" + ex.Message + "），所以不檢查" + label + "評分");
                    return null;
                }
            }
            return keys;
        }

        static void Walk(string dir, int level, int depth, bool exactDepth, Func<string, string> keyOf, HashSet<string> keys)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (exactDepth == false || level == depth)
                {
                    keys.Add(keyOf(Path.GetFileName(sub)));
                }
                if (level < depth)
                {
                    Walk(sub, level + 1, depth, exactDepth, keyOf, keys);
                }
            }
        }

        public int DeleteOrphans(IEnumerable<string> keys, out string backupName)
        {
            backupName = null;
            var wanted = new HashSet<string>(keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0)
            {
                return 0;
            }
            // 重新掃描：這段時間內如果資料夾又出現（或改名跟上了），就不刪
            var scan = Scan();
            var toDelete = scan.Orphans.Where(o => wanted.Contains(o.Key)).Select(o => o.Key).ToList();
            if (toDelete.Count == 0)
            {
                return 0;
            }
            lock (thisLock)
            {
                var all = Load();
                string path = DataPath();
                if (File.Exists(path))
                {
                    string dir = Path.GetDirectoryName(path);
                    backupName = "ratings.backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
                    File.Copy(path, Path.Combine(dir, backupName), true);
                    // 備份只留最近 10 份
                    var old = Directory.GetFiles(dir, "ratings.backup-*.json").OrderByDescending(t => t, StringComparer.Ordinal).Skip(10);
                    foreach (string f in old)
                    {
                        try { File.Delete(f); } catch (Exception) { }
                    }
                }
                int n = 0;
                foreach (string k in toDelete)
                {
                    if (all.Remove(k)) { n++; }
                }
                Save(all);
                logger.Info("評分：管理者刪除 {0} 筆找不到資料夾的評分（備份 {1}）", n, backupName ?? "無");
                return n;
            }
        }
    }
}
