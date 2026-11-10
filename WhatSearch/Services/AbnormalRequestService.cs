using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public interface IAbnormalRequestService
    {
        /// <summary>記一次「沒有對應路由的 404」；同一個 IP 在時間窗內累積到門檻就留下一筆事件</summary>
        void Record(string ip, string path, string userAgent);
        /// <summary>記一筆「管理者帳號登入失敗」事件（連續錯誤到達提醒門檻，或帳號剛被鎖住）；由登入流程呼叫，事件和 404 事件放在同一份清單、共用未讀數</summary>
        void RecordAdminLoginFailure(string account, string ip, int failCount, bool locked);
        AbnormalSnapshot Get();
        /// <summary>管理者上次看之後新增的事件數</summary>
        int GetUnread();
        void MarkAllRead();
        void Clear();
    }

    public class AbnormalEvent
    {
        /// <summary>觸發時間（UTC 毫秒）</summary>
        [JsonPropertyName("time")]
        public long Time { get; set; }
        /// <summary>最後一次 404 的時間（UTC 毫秒）；觸發後的冷卻期間還在請求就會一直更新</summary>
        [JsonPropertyName("last")]
        public long Last { get; set; }
        [JsonPropertyName("ip")]
        public string Ip { get; set; }
        /// <summary>這個 IP 累計的 404 次數（含觸發後冷卻期間的）</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }
        /// <summary>最近幾個被請求的路徑（最多 5 個）</summary>
        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = new List<string>();
        [JsonPropertyName("ua")]
        public string UserAgent { get; set; }
        /// <summary>路徑像是在探測（.php、.env、wp-admin…）</summary>
        [JsonPropertyName("suspicious")]
        public bool Suspicious { get; set; }
        /// <summary>事件種類：null（舊資料也是）= 404 掃描；"login" = 管理者帳號登入失敗</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }
        /// <summary>登入失敗事件：被猜密碼的管理者帳號</summary>
        [JsonPropertyName("account")]
        public string Account { get; set; }
        /// <summary>登入失敗事件：這一筆發生時帳號是否已被鎖住</summary>
        [JsonPropertyName("locked")]
        public bool Locked { get; set; }
    }

    public class AbnormalState
    {
        /// <summary>管理者上次看事件的時間（UTC 毫秒）</summary>
        [JsonPropertyName("lastRead")]
        public long LastRead { get; set; }
        [JsonPropertyName("events")]
        public List<AbnormalEvent> Events { get; set; } = new List<AbnormalEvent>();
    }

    public class AbnormalSnapshot
    {
        /// <summary>新到舊</summary>
        public List<AbnormalEvent> Events { get; set; }
        public long LastRead { get; set; }
        public int Unread { get; set; }
    }

    /// <summary>
    /// 異常請求偵測：網路上的掃描器常會亂試 /wp-login.php、/.env 這類這個站根本沒有的路徑。
    /// 同一個 IP 在 abnormal.windowSeconds 秒內有 abnormal.threshold 次「沒有對應路由的 404」就留下一筆事件，
    /// 存在 data/abnormal-requests.json（最新 abnormal.maxEvents 筆），並在 all.log 寫一行 Warn。
    /// 事件之後 abnormal.cooldownMinutes 分鐘內同一個 IP 不再新增事件，只累加次數。只偵測與記錄，不封鎖。
    /// </summary>
    public class AbnormalRequestService : IAbnormalRequestService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        const int MaxSamplePaths = 5;
        const int MaxTextLength = 200;
        const long PruneIntervalMs = 5 * 60 * 1000;
        const long FlushIntervalMs = 10 * 1000;

        static readonly Regex SuspiciousPath = new Regex(
            @"\.(php\d?|phtml|asp|aspx|jsp|cgi|env|git|svn|sql|bak|ini)(/|$)|wp-(admin|login|content|includes)|phpmyadmin|cgi-bin|xmlrpc|actuator|\.\./",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        class Tracker
        {
            public Queue<long> Hits = new Queue<long>();
            public List<string> RecentPaths = new List<string>();
            public AbnormalEvent Event;
            public long CooldownUntil;
        }

        readonly object gate = new object();
        readonly Dictionary<string, Tracker> trackers = new Dictionary<string, Tracker>(StringComparer.OrdinalIgnoreCase);
        AbnormalState state;
        bool dirty;
        long lastSave;
        long lastPrune;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "abnormal-requests.json");
        }

        static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        AbnormalState Load()
        {
            if (state != null)
            {
                return state;
            }
            state = new AbnormalState();
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    AbnormalState loaded = JsonHelper.Deserialize<AbnormalState>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        state = loaded;
                        state.Events = state.Events ?? new List<AbnormalEvent>();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 abnormal-requests.json 失敗，以空白資料開始");
            }
            return state;
        }

        public void Record(string ip, string path, string userAgent)
        {
            AbnormalRequestConfig cfg = Ioc.GetConfig().Abnormal;
            if (cfg == null || cfg.Enabled == false || string.IsNullOrEmpty(ip) || ClientIpResolver.InList(ip, cfg.IgnoreIps))
            {
                return;
            }
            int threshold = Math.Max(1, cfg.Threshold);
            long windowMs = Math.Max(1, cfg.WindowSeconds) * 1000L;
            long cooldownMs = Math.Max(0, cfg.CooldownMinutes) * 60 * 1000L;
            string cleanPath = Clean(path);
            long now = NowMs();

            lock (gate)
            {
                AbnormalState s = Load();
                Prune(now);
                Tracker t;
                if (trackers.TryGetValue(ip, out t) == false)
                {
                    t = new Tracker();
                    trackers[ip] = t;
                }

                // 冷卻期間：只累加次數，不再新增事件
                if (t.Event != null && now < t.CooldownUntil)
                {
                    t.Event.Count++;
                    t.Event.Last = now;
                    AddSample(t.Event.Paths, cleanPath);
                    t.Event.Suspicious = t.Event.Suspicious || SuspiciousPath.IsMatch(cleanPath);
                    dirty = true;
                    FlushIfDue(now);
                    return;
                }

                t.Hits.Enqueue(now);
                while (t.Hits.Count > 0 && now - t.Hits.Peek() > windowMs)
                {
                    t.Hits.Dequeue();
                }
                AddSample(t.RecentPaths, cleanPath);
                if (t.Hits.Count < threshold)
                {
                    return;
                }

                AbnormalEvent ev = new AbnormalEvent
                {
                    Time = now,
                    Last = now,
                    Ip = ip,
                    Count = t.Hits.Count,
                    Paths = new List<string>(t.RecentPaths),
                    UserAgent = Clean(userAgent),
                    Suspicious = t.RecentPaths.Any(p => SuspiciousPath.IsMatch(p))
                };
                s.Events.Add(ev);
                int maxEvents = Math.Max(10, cfg.MaxEvents);
                if (s.Events.Count > maxEvents)
                {
                    s.Events.RemoveRange(0, s.Events.Count - maxEvents);
                }
                t.Event = ev;
                t.CooldownUntil = now + cooldownMs;
                t.Hits.Clear();
                t.RecentPaths.Clear();
                Save();
                logger.Warn("異常請求：{0} 在 {1} 秒內有 {2} 次 404（{3}）{4}", ip, cfg.WindowSeconds, ev.Count,
                    string.Join(", ", ev.Paths), ev.Suspicious ? "，路徑可疑" : string.Empty);
            }
        }

        public void RecordAdminLoginFailure(string account, string ip, int failCount, bool locked)
        {
            AbnormalRequestConfig cfg = Ioc.GetConfig().Abnormal;
            if (cfg == null || cfg.Enabled == false)
            {
                return;
            }
            long now = NowMs();
            lock (gate)
            {
                AbnormalState s = Load();
                AbnormalEvent ev = new AbnormalEvent
                {
                    Time = now,
                    Last = now,
                    Ip = Clean(ip),
                    Count = failCount,
                    Kind = "login",
                    Account = Clean(account),
                    Locked = locked,
                    UserAgent = string.Empty
                };
                s.Events.Add(ev);
                int maxEvents = Math.Max(10, cfg.MaxEvents);
                if (s.Events.Count > maxEvents)
                {
                    s.Events.RemoveRange(0, s.Events.Count - maxEvents);
                }
                Save();
                logger.Warn("管理者帳號 {0} 登入失敗：密碼連續錯誤 {1} 次{2}（來源 {3}）", ev.Account, failCount, locked ? "，已鎖住" : string.Empty, ev.Ip);
            }
        }

        public AbnormalSnapshot Get()
        {
            lock (gate)
            {
                AbnormalState s = Load();
                FlushIfDue(NowMs());
                return new AbnormalSnapshot
                {
                    Events = s.Events.OrderByDescending(e => e.Time).ToList(),
                    LastRead = s.LastRead,
                    Unread = s.Events.Count(e => e.Time > s.LastRead)
                };
            }
        }

        public int GetUnread()
        {
            lock (gate)
            {
                AbnormalState s = Load();
                return s.Events.Count(e => e.Time > s.LastRead);
            }
        }

        public void MarkAllRead()
        {
            lock (gate)
            {
                Load().LastRead = NowMs();
                Save();
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                Load().Events.Clear();
                foreach (Tracker t in trackers.Values)
                {
                    t.Event = null;
                    t.CooldownUntil = 0;
                }
                Save();
            }
        }

        // 沒有活動、冷卻也過了的 IP 不用再記，避免被大量不同 IP 掃描時吃掉記憶體
        void Prune(long now)
        {
            if (now - lastPrune < PruneIntervalMs)
            {
                return;
            }
            lastPrune = now;
            long windowMs = Math.Max(1, Ioc.GetConfig().Abnormal.WindowSeconds) * 1000L;
            foreach (string key in trackers.Where(p => now >= p.Value.CooldownUntil
                && (p.Value.Hits.Count == 0 || now - p.Value.Hits.Peek() > windowMs)).Select(p => p.Key).ToList())
            {
                trackers.Remove(key);
            }
        }

        static void AddSample(List<string> list, string path)
        {
            if (list.Contains(path))
            {
                return;
            }
            if (list.Count >= MaxSamplePaths)
            {
                list.RemoveAt(0);
            }
            list.Add(path);
        }

        // 去掉控制字元（避免在日誌裡偽造換行）並限制長度
        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder(Math.Min(text.Length, MaxTextLength));
            foreach (char c in text)
            {
                if (sb.Length >= MaxTextLength)
                {
                    break;
                }
                if (c >= 0x20 && c != 0x7f)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // 冷卻期間的累加不每次寫檔（掃描器可能一秒好幾個請求），隔一陣子才寫一次
        void FlushIfDue(long now)
        {
            if (dirty && now - lastSave >= FlushIntervalMs)
            {
                Save();
            }
        }

        // 呼叫端已持有 gate；寫檔被防毒等暫時鎖住時 SafeFile 會自動重試，最後還是失敗只記 Warn，不影響請求
        void Save()
        {
            try
            {
                SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(state, indented: true));
                dirty = false;
                lastSave = NowMs();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "寫入 abnormal-requests.json 失敗");
            }
        }
    }
}
