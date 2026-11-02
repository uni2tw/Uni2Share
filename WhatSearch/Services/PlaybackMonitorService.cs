using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WhatSearch.Services
{
    /// <summary>誰在播放：登入者、顯示名稱、來源 IP、裝置</summary>
    public class PlaybackWho
    {
        public string User { get; set; }
        public string Display { get; set; }
        public string Ip { get; set; }
        public string Device { get; set; }
    }

    /// <summary>一個正在送出內容的影音回應；每送出一段就呼叫 Add，結束（含客戶端中斷）時呼叫 End</summary>
    public interface IPlaybackStream
    {
        void Add(int bytes);
        void End();
    }

    public interface IPlaybackMonitorService
    {
        /// <summary>
        /// 一個影音回應開始送出內容。同一個人、同一個 IP、同一個檔案的多個 Range 請求算同一次播放（沒有連線後 60 秒才結束）。
        /// requestPath 是請求路徑（/get/...），mp3 表示播的是 FLAC 轉好的 MP3 快取版。
        /// </summary>
        IPlaybackStream Begin(PlaybackWho who, string requestPath, bool mp3);
        PlaybackSnapshot GetSnapshot();
    }

    public class PlaybackSession
    {
        public string User { get; set; }
        public string Display { get; set; }
        public string Ip { get; set; }
        public string Device { get; set; }
        public string Folder { get; set; }
        public string Name { get; set; }
        /// <summary>video／audio／mp3（FLAC 轉 MP3 的快取版）</summary>
        public string Kind { get; set; }
        public long StartedAtMs { get; set; }
        public int Seconds { get; set; }
        /// <summary>最近 10 秒的平均傳送速率</summary>
        public double Mbps { get; set; }
        public long Bytes { get; set; }
        /// <summary>true = 現在還有連線在傳；false = 連線都結束了，但最近 60 秒內還有動靜</summary>
        public bool Active { get; set; }
        /// <summary>影片連線還開著，但速率偏低（網路慢、卡頓，也可能只是暫停中）</summary>
        public bool Slow { get; set; }
    }

    public class PlaybackUserStat
    {
        public string User { get; set; }
        public string Display { get; set; }
        public long Bytes { get; set; }
    }

    public class PlaybackCount
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public class PlaybackSnapshot
    {
        public List<PlaybackSession> Sessions { get; set; }
        /// <summary>全部播放的最近 10 秒平均速率</summary>
        public double Mbps { get; set; }
        public long TodayBytes { get; set; }
        public int TodayPlays { get; set; }
        public int SlowCount { get; set; }
        /// <summary>今天 0～23 點每小時送出的位元組</summary>
        public long[] Hours { get; set; }
        public List<PlaybackUserStat> Users { get; set; }
        /// <summary>近 7 天（含今天）播放次數最多的資料夾</summary>
        public List<PlaybackCount> Folders { get; set; }
        /// <summary>近 7 天（含今天）的裝置分布（依播放次數）</summary>
        public List<PlaybackCount> Devices { get; set; }
        public int WeekPlays { get; set; }
        public long StartedAtMs { get; set; }
    }

    /// <summary>
    /// 後台「播放監控」的資料來源：只放在記憶體（重新啟動就歸零），只記錄 /get/ 的影音播放，
    /// 不含封面、字幕、下載。位元組數是實際寫進回應的量（客戶端中途中斷就只算到中斷前）。
    /// </summary>
    public class PlaybackMonitorService : IPlaybackMonitorService
    {
        const int RateWindowSeconds = 10;
        const int IdleSeconds = 60;
        const int KeepDays = 7;
        const int TopFolders = 5;
        const int TopUsers = 10;
        const double SlowMbps = 1.0;
        const int SlowAfterSeconds = 20;

        static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv"
        };

        readonly object gate = new object();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly List<DayStat> days = new List<DayStat>();
        readonly RateWindow total = new RateWindow();
        readonly DateTime startedAt = DateTime.Now;
        DayStat today;

        public IPlaybackStream Begin(PlaybackWho who, string requestPath, bool mp3)
        {
            string rel = requestPath ?? string.Empty;
            if (rel.StartsWith("/get/", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel.Substring(5);
            }
            rel = rel.TrimStart('/');
            int slash = rel.LastIndexOf('/');
            string folder = slash >= 0 ? rel.Substring(0, slash) : string.Empty;
            string name = slash >= 0 ? rel.Substring(slash + 1) : rel;
            string user = who.User ?? string.Empty;
            string key = user + "|" + who.Ip + "|" + rel + (mp3 ? "|mp3" : string.Empty);
            DateTime now = DateTime.Now;

            lock (gate)
            {
                Session s;
                if (sessions.TryGetValue(key, out s) == false)
                {
                    s = new Session
                    {
                        User = user,
                        Display = string.IsNullOrEmpty(who.Display) ? user : who.Display,
                        Ip = who.Ip,
                        Device = who.Device,
                        Folder = folder,
                        Name = name,
                        Mp3 = mp3,
                        IsVideo = VideoExtensions.Contains(Path.GetExtension(name)),
                        StartedAt = now
                    };
                    sessions[key] = s;

                    DayStat d = GetToday(now);
                    d.Plays++;
                    Increment(d.Folders, folder.Length > 0 ? folder : "（根目錄）");
                    Increment(d.Devices, string.IsNullOrEmpty(who.Device) ? "unknown" : who.Device);
                }
                s.InFlight++;
                s.LastActivity = now;
                return new Handle(this, s);
            }
        }

        public PlaybackSnapshot GetSnapshot()
        {
            DateTime now = DateTime.Now;
            long sec = Environment.TickCount64 / 1000;
            lock (gate)
            {
                foreach (string key in sessions.Where(p => p.Value.InFlight <= 0 && (now - p.Value.LastActivity).TotalSeconds > IdleSeconds)
                    .Select(p => p.Key).ToList())
                {
                    sessions.Remove(key);
                }

                List<PlaybackSession> list = new List<PlaybackSession>();
                foreach (Session s in sessions.Values.OrderBy(t => t.StartedAt))
                {
                    double age = (now - s.StartedAt).TotalSeconds;
                    double window = Math.Min(RateWindowSeconds, Math.Max(1, age));
                    double mbps = s.Rate.Sum(sec) * 8d / window / 1000000d;
                    bool active = s.InFlight > 0;
                    list.Add(new PlaybackSession
                    {
                        User = s.User,
                        Display = s.Display,
                        Ip = s.Ip,
                        Device = s.Device,
                        Folder = s.Folder,
                        Name = s.Name,
                        Kind = s.Mp3 ? "mp3" : (s.IsVideo ? "video" : "audio"),
                        StartedAtMs = new DateTimeOffset(s.StartedAt).ToUnixTimeMilliseconds(),
                        Seconds = (int)age,
                        Mbps = Math.Round(mbps, 2),
                        Bytes = s.Bytes,
                        Active = active,
                        Slow = s.IsVideo && active && age >= SlowAfterSeconds && mbps < SlowMbps
                    });
                }

                DayStat d = GetToday(now);
                Dictionary<string, int> folders = new Dictionary<string, int>();
                Dictionary<string, int> devices = new Dictionary<string, int>();
                int weekPlays = 0;
                foreach (DayStat day in days)
                {
                    weekPlays += day.Plays;
                    foreach (KeyValuePair<string, int> p in day.Folders) { Add(folders, p.Key, p.Value); }
                    foreach (KeyValuePair<string, int> p in day.Devices) { Add(devices, p.Key, p.Value); }
                }

                return new PlaybackSnapshot
                {
                    Sessions = list,
                    Mbps = Math.Round(total.Sum(sec) * 8d / RateWindowSeconds / 1000000d, 2),
                    TodayBytes = d.Bytes,
                    TodayPlays = d.Plays,
                    SlowCount = list.Count(t => t.Slow),
                    Hours = (long[])d.Hours.Clone(),
                    Users = d.Users.Select(p => new PlaybackUserStat { User = p.Key, Display = p.Value.Display, Bytes = p.Value.Bytes })
                        .OrderByDescending(t => t.Bytes).Take(TopUsers).ToList(),
                    Folders = folders.Select(p => new PlaybackCount { Name = p.Key, Count = p.Value })
                        .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal).Take(TopFolders).ToList(),
                    Devices = devices.Select(p => new PlaybackCount { Name = p.Key, Count = p.Value })
                        .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal).ToList(),
                    WeekPlays = weekPlays,
                    StartedAtMs = new DateTimeOffset(startedAt).ToUnixTimeMilliseconds()
                };
            }
        }

        // 呼叫端要在 lock (gate) 裡
        DayStat GetToday(DateTime now)
        {
            if (today == null || today.Day != now.Date)
            {
                today = days.FirstOrDefault(t => t.Day == now.Date);
                if (today == null)
                {
                    today = new DayStat { Day = now.Date };
                    days.Add(today);
                }
                days.RemoveAll(t => t.Day < now.Date.AddDays(-(KeepDays - 1)));
            }
            return today;
        }

        static void Increment(Dictionary<string, int> dict, string key)
        {
            Add(dict, key, 1);
        }

        static void Add(Dictionary<string, int> dict, string key, int count)
        {
            int n;
            dict.TryGetValue(key, out n);
            dict[key] = n + count;
        }

        void AddBytes(Session s, int bytes)
        {
            if (bytes <= 0)
            {
                return;
            }
            DateTime now = DateTime.Now;
            long sec = Environment.TickCount64 / 1000;
            lock (gate)
            {
                s.Bytes += bytes;
                s.LastActivity = now;
                s.Rate.Add(bytes, sec);
                total.Add(bytes, sec);

                DayStat d = GetToday(now);
                d.Bytes += bytes;
                d.Hours[now.Hour] += bytes;
                UserBytes u;
                if (d.Users.TryGetValue(s.User, out u) == false)
                {
                    u = new UserBytes { Display = s.Display };
                    d.Users[s.User] = u;
                }
                u.Bytes += bytes;
            }
        }

        void EndStream(Session s)
        {
            lock (gate)
            {
                s.InFlight--;
                s.LastActivity = DateTime.Now;
            }
        }

        sealed class Handle : IPlaybackStream
        {
            readonly PlaybackMonitorService owner;
            readonly Session session;
            int ended;

            public Handle(PlaybackMonitorService owner, Session session)
            {
                this.owner = owner;
                this.session = session;
            }

            public void Add(int bytes)
            {
                owner.AddBytes(session, bytes);
            }

            public void End()
            {
                if (System.Threading.Interlocked.Exchange(ref ended, 1) == 0)
                {
                    owner.EndStream(session);
                }
            }
        }

        sealed class Session
        {
            public string User, Display, Ip, Device, Folder, Name;
            public bool Mp3, IsVideo;
            public DateTime StartedAt, LastActivity;
            public long Bytes;
            public int InFlight;
            public readonly RateWindow Rate = new RateWindow();
        }

        sealed class UserBytes
        {
            public string Display;
            public long Bytes;
        }

        sealed class DayStat
        {
            public DateTime Day;
            public long Bytes;
            public int Plays;
            public readonly long[] Hours = new long[24];
            public readonly Dictionary<string, UserBytes> Users = new Dictionary<string, UserBytes>();
            public readonly Dictionary<string, int> Folders = new Dictionary<string, int>();
            public readonly Dictionary<string, int> Devices = new Dictionary<string, int>();
        }

        /// <summary>最近 RateWindowSeconds 秒、每秒一格的位元組數（用開機後的秒數當時間，不受校時影響）</summary>
        sealed class RateWindow
        {
            readonly long[] buckets = new long[RateWindowSeconds];
            long lastSec = -1;

            void Advance(long sec)
            {
                if (lastSec < 0)
                {
                    lastSec = sec;
                    return;
                }
                long gap = sec - lastSec;
                if (gap <= 0)
                {
                    return;
                }
                if (gap >= RateWindowSeconds)
                {
                    Array.Clear(buckets, 0, buckets.Length);
                }
                else
                {
                    for (long i = 1; i <= gap; i++)
                    {
                        buckets[(lastSec + i) % RateWindowSeconds] = 0;
                    }
                }
                lastSec = sec;
            }

            public void Add(long bytes, long sec)
            {
                Advance(sec);
                buckets[sec % RateWindowSeconds] += bytes;
            }

            public long Sum(long sec)
            {
                Advance(sec);
                return buckets.Sum();
            }
        }
    }
}
