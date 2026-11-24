using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using NLog;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public interface IAdminMessageService
    {
        /// <summary>新增一則給管理者的站內訊息（例如轉檔完成或失敗）；寫檔失敗只記 log，不影響呼叫端</summary>
        void Add(string kind, string title, string text, bool error);
        AdminMessageSnapshot Get();
        /// <summary>管理者上次看之後新增的訊息數</summary>
        int GetUnread();
        void MarkAllRead();
        void Clear();
    }

    public class AdminMessage
    {
        /// <summary>發生時間（UTC 毫秒）</summary>
        [JsonPropertyName("time")]
        public long Time { get; set; }
        /// <summary>種類，例如 convert（MKV 轉檔）</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("text")]
        public string Text { get; set; }
        /// <summary>是不是出問題的訊息（畫面上用紅色）</summary>
        [JsonPropertyName("error")]
        public bool Error { get; set; }
    }

    public class AdminMessageSnapshot
    {
        [JsonPropertyName("lastRead")]
        public long LastRead { get; set; }
        [JsonPropertyName("unread")]
        public int Unread { get; set; }
        [JsonPropertyName("messages")]
        public List<AdminMessage> Messages { get; set; } = new List<AdminMessage>();
    }

    /// <summary>
    /// 給管理者的站內訊息：系統在背景做完事（例如 MKV 轉檔完成或失敗）時留一則，管理者選單上顯示未讀數，到「管理者訊息」頁查看。
    /// 整個系統共用一份（不分管理者），存在 data/admin-messages.json（最新 200 則 + 上次已讀時間）。
    /// </summary>
    public class AdminMessageService : IAdminMessageService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        const int MaxMessages = 200;
        readonly object gate = new object();
        AdminMessageSnapshot state;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "admin-messages.json");
        }

        AdminMessageSnapshot Load()
        {
            if (state != null)
            {
                return state;
            }
            state = new AdminMessageSnapshot();
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<AdminMessageSnapshot>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        state = loaded;
                        state.Messages = state.Messages ?? new List<AdminMessage>();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 admin-messages.json 失敗，以空白資料開始");
            }
            return state;
        }

        // 呼叫端已持有 gate
        void Save()
        {
            try
            {
                SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(state, indented: true));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "寫入 admin-messages.json 失敗");
            }
        }

        public void Add(string kind, string title, string text, bool error)
        {
            lock (gate)
            {
                var s = Load();
                s.Messages.Add(new AdminMessage { Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Kind = kind, Title = title, Text = text, Error = error });
                if (s.Messages.Count > MaxMessages)
                {
                    s.Messages.RemoveRange(0, s.Messages.Count - MaxMessages);
                }
                Save();
            }
        }

        public AdminMessageSnapshot Get()
        {
            lock (gate)
            {
                var s = Load();
                return new AdminMessageSnapshot
                {
                    LastRead = s.LastRead,
                    Unread = s.Messages.Count(m => m.Time > s.LastRead),
                    Messages = s.Messages.OrderByDescending(m => m.Time).ToList()
                };
            }
        }

        public int GetUnread()
        {
            lock (gate)
            {
                var s = Load();
                return s.Messages.Count(m => m.Time > s.LastRead);
            }
        }

        public void MarkAllRead()
        {
            lock (gate)
            {
                var s = Load();
                s.LastRead = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Save();
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                var s = Load();
                s.Messages.Clear();
                Save();
            }
        }
    }
}
