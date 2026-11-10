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
