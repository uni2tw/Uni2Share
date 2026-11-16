using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using NLog;
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
