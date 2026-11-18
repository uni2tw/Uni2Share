using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public interface IFavoriteService
    {
        /// <summary>會員的最愛（依加入順序，最早加入的在前面）</summary>
        FavoriteData Get(string member);
        /// <summary>加入或移除；回傳是否有實際變動</summary>
        bool Set(string member, string kind, string id, bool fav);
        /// <summary>整份移除某個會員的最愛（刪除會員時用）；回傳移除的筆數（專輯＋單曲）</summary>
        int RemoveMember(string member);
        /// <summary>一次移除某個會員的多筆最愛（清除找不到的項目用），只寫一次檔；回傳實際移除的筆數</summary>
        int RemoveItems(string member, IEnumerable<string> albumIds, IEnumerable<string> trackIds);
        /// <summary>
        /// 依 ids 的順序重排單曲。只動「ids 裡有的、而且確實是最愛」的那些歌：它們原本佔的位置照新順序填回去，
        /// 其他的（找不到所以畫面上沒列出、或是同時間才新加入的）位置不動。回傳順序是否有變。
        /// </summary>
        bool ReorderTracks(string member, IEnumerable<string> ids);
        /// <summary>
        /// 資料夾改名／搬移後，把所有會員最愛裡「在舊路徑底下」的專輯與單曲 id 改成新路徑（id 是相對路徑的 Base64）。
        /// oldRel／newRel 是 /分享名/資料夾 形式。新 id 已經在最愛裡就只留一筆。回傳改了幾筆
        /// </summary>
        int MoveFolder(string oldRel, string newRel);
        /// <summary>監看所有分享根的資料夾改名，讓最愛跟著改名</summary>
        void StartWatch();
    }

    public class FavoriteData
    {
        [JsonPropertyName("albums")]
        public List<string> Albums { get; set; } = new List<string>();
        [JsonPropertyName("tracks")]
        public List<string> Tracks { get; set; } = new List<string>();
    }

    /// <summary>
    /// 會員的「我的最愛」（專輯資料夾、歌曲），存在 data/userData/favorites.json，格式 { 會員名稱: { albums: [id…], tracks: [id…] } }。
    /// 只存資料夾/檔案 id（相對路徑的 Base64），不複製標籤；顯示時才即時解析。
    /// </summary>
    public class FavoriteService : IFavoriteService
    {
        public const string KindAlbum = "album";
        public const string KindTrack = "track";

        static ILogger logger = LogManager.GetCurrentClassLogger();
        static readonly object thisLock = new object();
        Dictionary<string, FavoriteData> cache;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "userData", "favorites.json");
        }

        Dictionary<string, FavoriteData> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            cache = new Dictionary<string, FavoriteData>(StringComparer.Ordinal);
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<Dictionary<string, FavoriteData>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        cache = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 favorites.json 失敗，以空白資料開始");
            }
            return cache;
        }

        void Save()
        {
            // 先寫暫存檔再取代，避免寫到一半當掉把原本的最愛清空；檔案被防毒等程式暫時鎖住時會自動重試
            SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(cache, indented: true));
        }

        public FavoriteData Get(string member)
        {
            lock (thisLock)
            {
                FavoriteData data;
                if (string.IsNullOrEmpty(member) || Load().TryGetValue(member, out data) == false)
                {
                    return new FavoriteData();
                }
                return new FavoriteData { Albums = data.Albums.ToList(), Tracks = data.Tracks.ToList() };
            }
        }

        public int RemoveItems(string member, IEnumerable<string> albumIds, IEnumerable<string> trackIds)
        {
            if (string.IsNullOrEmpty(member))
            {
                return 0;
            }
            lock (thisLock)
            {
                FavoriteData data;
                if (Load().TryGetValue(member, out data) == false)
                {
                    return 0;
                }
                var albums = new HashSet<string>(albumIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                var tracks = new HashSet<string>(trackIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                int n = data.Albums.RemoveAll(albums.Contains) + data.Tracks.RemoveAll(tracks.Contains);
                if (n > 0)
                {
                    Save();
                }
                return n;
            }
        }

        public int RemoveMember(string member)
        {
            if (string.IsNullOrEmpty(member))
            {
                return 0;
            }
            lock (thisLock)
            {
                var all = Load();
                FavoriteData data;
                if (all.TryGetValue(member, out data) == false)
                {
                    return 0;
                }
                int n = data.Albums.Count + data.Tracks.Count;
                all.Remove(member);
                Save();
                return n;
            }
        }

        public bool ReorderTracks(string member, IEnumerable<string> ids)
        {
            if (string.IsNullOrEmpty(member) || ids == null)
            {
                return false;
            }
            lock (thisLock)
            {
                FavoriteData data;
                if (Load().TryGetValue(member, out data) == false)
                {
                    return false;
                }
                var owned = new HashSet<string>(data.Tracks, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var order = new List<string>();
                foreach (string id in ids)
                {
                    if (id != null && owned.Contains(id) && seen.Add(id))
                    {
                        order.Add(id);
                    }
                }
                // 這些歌目前佔的位置（由前到後）
                var slots = new List<int>();
                for (int i = 0; i < data.Tracks.Count; i++)
                {
                    if (seen.Contains(data.Tracks[i]))
                    {
                        slots.Add(i);
                    }
                }
                bool changed = false;
                for (int k = 0; k < slots.Count; k++)
                {
                    if (data.Tracks[slots[k]] != order[k])
                    {
                        data.Tracks[slots[k]] = order[k];
                        changed = true;
                    }
                }
                if (changed)
                {
                    Save();
                }
                return changed;
            }
        }

        public int MoveFolder(string oldRel, string newRel)
        {
            oldRel = (oldRel ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            newRel = (newRel ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (oldRel.Length == 0 || newRel.Length == 0 || string.Equals(oldRel, newRel, StringComparison.Ordinal))
            {
                return 0;
            }
            lock (thisLock)
            {
                int changed = 0;
                foreach (var kv in Load())
                {
                    changed += MoveIds(kv.Value.Albums, oldRel, newRel);
                    changed += MoveIds(kv.Value.Tracks, oldRel, newRel);
                }
                if (changed > 0)
                {
                    Save();
                }
                return changed;
            }
        }

        /// <summary>
        /// id（相對路徑的 Base64）若在 oldRel 之下（含自己），回傳換成 newRel 之後的新 id；不在底下、解不開或沒變就回 null。
        /// 邊界安全：oldRel 是 /A 時 /A2 不會被當成在底下。播放紀錄改名跟隨也用這個。
        /// </summary>
        public static string RemapId(string id, string oldRel, string newRel)
        {
            string rel;
            try
            {
                rel = Helper.DecodeUrlBase64(id).Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
            string tail = null;
            if (string.Equals(rel.TrimEnd('/'), oldRel, StringComparison.OrdinalIgnoreCase))
            {
                tail = rel.Substring(oldRel.Length);
            }
            else if (rel.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase))
            {
                tail = rel.Substring(oldRel.Length);
            }
            if (tail == null)
            {
                return null;
            }
            string newId = Helper.EncodeUrlBase64(newRel + tail);
            return newId == id ? null : newId;
        }

        // 把 list 裡路徑在 oldRel 之下（含自己）的 id 換成新路徑，順序不變；換完已經存在的 id 只留一筆
        static int MoveIds(List<string> list, string oldRel, string newRel)
        {
            int changed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                string newId = RemapId(list[i], oldRel, newRel);
                if (newId == null)
                {
                    continue;
                }
                changed++;
                if (list.Contains(newId))
                {
                    list.RemoveAt(i);
                    i--;
                }
                else
                {
                    list[i] = newId;
                }
            }
            return changed;
        }

        // ---------- 資料夾改名：最愛跟著走 ----------

        static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();

        public void StartWatch()
        {
            var config = Ioc.GetConfig();
            if (config.Folders == null)
            {
                return;
            }
            foreach (FolderConfig f in config.Folders.Where(t => t != null && string.IsNullOrEmpty(t.Path) == false))
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
                    w.Error += (s, e) => logger.Warn("最愛：{0} 的檔案監看出錯（{1}），資料夾改名可能沒有跟上", title, e.GetException() == null ? "?" : e.GetException().Message);
                    w.EnableRaisingEvents = true;
                    watchers.Add(w);
                }
                catch (Exception ex)
                {
                    logger.Warn("最愛：{0} 的檔案監看啟動失敗（{1}），資料夾改名不會自動跟上", f.Title, ex.Message);
                }
            }
            logger.Info("最愛：監看 {0} 個分享根的資料夾改名", watchers.Count);
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
                string oldRel, newRel;
                if (PathUtility.TryGetRelPath(oldPath.TrimEnd('\\', '/'), out oldRel) == false ||
                    PathUtility.TryGetRelPath(newPath.TrimEnd('\\', '/'), out newRel) == false)
                {
                    return;
                }
                int n = MoveFolder(oldRel, newRel);
                if (n > 0)
                {
                    logger.Info("最愛：資料夾改名，{0} 筆最愛從「{1}」跟到「{2}」", n, oldRel, newRel);
                }
                // 播放紀錄也要跟著改名（共用這一組 watcher，不另外再開一組）
                try
                {
                    int p = Ioc.Get<IPlayStatService>().MoveFolder(oldRel, newRel);
                    if (p > 0)
                    {
                        logger.Info("播放紀錄：資料夾改名，{0} 筆紀錄從「{1}」跟到「{2}」", p, oldRel, newRel);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "播放紀錄：處理資料夾改名失敗：{0} → {1}", oldRel, newRel);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "最愛：處理資料夾改名失敗：{0} → {1}", oldPath, newPath);
            }
        }

        public bool Set(string member, string kind, string id, bool fav)
        {
            if (string.IsNullOrEmpty(member) || string.IsNullOrEmpty(id))
            {
                return false;
            }
            lock (thisLock)
            {
                var all = Load();
                FavoriteData data;
                if (all.TryGetValue(member, out data) == false)
                {
                    data = new FavoriteData();
                    all[member] = data;
                }
                List<string> list = kind == KindAlbum ? data.Albums : data.Tracks;
                bool exists = list.Contains(id);
                if (fav == exists)
                {
                    return false;
                }
                if (fav)
                {
                    list.Add(id);
                }
                else
                {
                    list.Remove(id);
                }
                Save();
                return true;
            }
        }
    }
}
