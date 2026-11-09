using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public interface IPinService
    {
        /// <summary>會員置頂的資料夾 id（依置頂的先後順序）</summary>
        List<string> Get(string member);
        /// <summary>置頂或取消置頂；已經是想要的狀態回傳 true（不算錯）。置頂超過上限回傳 false 並說明原因</summary>
        bool Set(string member, string id, bool pin, out string error);
    }

    /// <summary>
    /// 會員置頂的資料夾，存在 data/userData/pins.json，格式 { 會員名稱: [資料夾 id…] }。
    /// 置頂只影響這位會員的列表（置頂的資料夾排在同一層的最前面）；只存資料夾 id（相對路徑的 Base64），資料夾改名後 id 就對不上，目前不自動跟隨。
    /// </summary>
    public class PinService : IPinService
    {
        /// <summary>每人最多置頂幾個，太多就失去置頂的意義</summary>
        public const int MaxPins = 50;

        static ILogger logger = LogManager.GetCurrentClassLogger();
        static readonly object thisLock = new object();
        Dictionary<string, List<string>> cache;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "userData", "pins.json");
        }

        Dictionary<string, List<string>> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            cache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        foreach (var kv in loaded)
                        {
                            cache[kv.Key] = kv.Value ?? new List<string>();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 pins.json 失敗，以空白資料開始");
            }
            return cache;
        }

        public List<string> Get(string member)
        {
            lock (thisLock)
            {
                List<string> list;
                if (string.IsNullOrEmpty(member) || Load().TryGetValue(member, out list) == false)
                {
                    return new List<string>();
                }
                return list.ToList();
            }
        }

        public bool Set(string member, string id, bool pin, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(member) || string.IsNullOrEmpty(id))
            {
                error = "參數錯誤";
                return false;
            }
            lock (thisLock)
            {
                var all = Load();
                List<string> list;
                if (all.TryGetValue(member, out list) == false)
                {
                    list = new List<string>();
                    all[member] = list;
                }
                bool exists = list.Contains(id);
                if (pin == exists)
                {
                    return true;
                }
                if (pin)
                {
                    if (list.Count >= MaxPins)
                    {
                        error = "最多只能置頂 " + MaxPins + " 個資料夾，請先取消一些";
                        return false;
                    }
                    list.Add(id);
                }
                else
                {
                    list.Remove(id);
                }
                // 先寫暫存檔再取代，避免寫到一半當掉把置頂清空
                SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(all, indented: true));
                return true;
            }
        }
    }
}
