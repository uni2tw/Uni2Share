using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using NLog;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    /// <summary>會員最後一次的動態（每人只留最後一筆，不留歷程）</summary>
    public class MemberActivity
    {
        /// <summary>最後一次來訪（開頁面、瀏覽資料夾、搜尋、播放）</summary>
        [JsonPropertyName("seen")]
        public DateTime? Seen { get; set; }
        /// <summary>當時的裝置，例如 iPhone iOS17.4/Safari</summary>
        [JsonPropertyName("device")]
        public string Device { get; set; }
        /// <summary>最後瀏覽的資料夾（/分享名/資料夾 形式）</summary>
        [JsonPropertyName("folder")]
        public string Folder { get; set; }
        [JsonPropertyName("folderOn")]
        public DateTime? FolderTime { get; set; }
        /// <summary>最後播放（或下載）的檔案（/分享名/資料夾/檔名 形式）</summary>
        [JsonPropertyName("played")]
        public string Played { get; set; }
        [JsonPropertyName("playedOn")]
        public DateTime? PlayedTime { get; set; }
    }

    public interface IActivityService
    {
        /// <summary>記一次來訪；device 可以是 null（不更新裝置）</summary>
        void Seen(string member, string device);
        /// <summary>記最後瀏覽的資料夾（relPath：/分享名/資料夾）</summary>
        void SetFolder(string member, string relPath);
        /// <summary>記最後播放的檔案（relPath：/分享名/資料夾/檔名）。同一個檔案的連續請求（Range、拖進度）不會一直更新時間</summary>
        void SetPlayed(string member, string relPath);
        MemberActivity Get(string member);
        void RemoveMember(string member);
        /// <summary>立刻把變動存檔（平常每分鐘存一次，程式結束時也會呼叫）</summary>
        void Flush();
    }

    /// <summary>
    /// 會員最後來訪、最後瀏覽、最後播放的記錄。全部放記憶體，變動後每分鐘存一次到 data/userData/activity.json，
    /// 不寫進 users.json（那份檔案每次更新都整份重寫，請求一多會變慢）。
    /// </summary>
    public class ActivityService : IActivityService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        static readonly object thisLock = new object();
        // 同一個檔案超過這段時間後再來，視為重新播放而更新時間（影片的 Range 請求會連續好幾個）
        static readonly TimeSpan ReplayGap = TimeSpan.FromMinutes(30);

        Dictionary<string, MemberActivity> cache;
        bool dirty;
        Timer timer;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "userData", "activity.json");
        }

        public ActivityService()
        {
            timer = new Timer(_ => Flush(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
        }

        Dictionary<string, MemberActivity> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            cache = new Dictionary<string, MemberActivity>(StringComparer.Ordinal);
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<Dictionary<string, MemberActivity>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        cache = new Dictionary<string, MemberActivity>(loaded, StringComparer.Ordinal);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 activity.json 失敗，以空白資料開始");
            }
            return cache;
        }

        MemberActivity Entry(string member)
        {
            var all = Load();
            MemberActivity a;
            if (all.TryGetValue(member, out a) == false)
            {
                a = new MemberActivity();
                all[member] = a;
            }
            return a;
        }

        static string Trunc(string text, int max)
        {
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }

        public void Seen(string member, string device)
        {
            if (string.IsNullOrEmpty(member))
            {
                return;
            }
            lock (thisLock)
            {
                MemberActivity a = Entry(member);
                a.Seen = DateTime.Now;
                if (string.IsNullOrEmpty(device) == false)
                {
                    a.Device = Trunc(device, 80);
                }
                dirty = true;
            }
        }

        public void SetFolder(string member, string relPath)
        {
            if (string.IsNullOrEmpty(member) || string.IsNullOrEmpty(relPath))
            {
                return;
            }
            lock (thisLock)
            {
                MemberActivity a = Entry(member);
                a.Folder = Trunc(relPath, 400);
                a.FolderTime = DateTime.Now;
                dirty = true;
            }
        }

        public void SetPlayed(string member, string relPath)
        {
            if (string.IsNullOrEmpty(member) || string.IsNullOrEmpty(relPath))
            {
                return;
            }
            relPath = Trunc(relPath, 400);
            lock (thisLock)
            {
                MemberActivity a = Entry(member);
                DateTime now = DateTime.Now;
                if (string.Equals(a.Played, relPath, StringComparison.Ordinal) && a.PlayedTime.HasValue && now - a.PlayedTime.Value < ReplayGap)
                {
                    return;
                }
                a.Played = relPath;
                a.PlayedTime = now;
                dirty = true;
            }
        }

        public MemberActivity Get(string member)
        {
            if (string.IsNullOrEmpty(member))
            {
                return null;
            }
            lock (thisLock)
            {
                MemberActivity a;
                if (Load().TryGetValue(member, out a) == false)
                {
                    return null;
                }
                return new MemberActivity
                {
                    Seen = a.Seen,
                    Device = a.Device,
                    Folder = a.Folder,
                    FolderTime = a.FolderTime,
                    Played = a.Played,
                    PlayedTime = a.PlayedTime
                };
            }
        }

        public void RemoveMember(string member)
        {
            if (string.IsNullOrEmpty(member))
            {
                return;
            }
            lock (thisLock)
            {
                if (Load().Remove(member))
                {
                    dirty = true;
                }
            }
        }

        public void Flush()
        {
            try
            {
                lock (thisLock)
                {
                    if (dirty == false || cache == null)
                    {
                        return;
                    }
                    SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(cache, indented: true));
                    dirty = false;
                }
            }
            catch (Exception ex)
            {
                // 存檔失敗（檔案被鎖住等）留到下一分鐘再試；dirty 仍是 true
                logger.Warn(ex, "存 activity.json 失敗，稍後重試");
            }
        }
    }
}
