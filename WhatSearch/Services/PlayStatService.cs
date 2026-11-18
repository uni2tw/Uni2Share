using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using NLog;
using WhatSearch.Utility;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public interface IPlayStatService
    {
        /// <summary>記一筆「聽過」。回傳是否有寫入（參數不合法會略過）</summary>
        bool Record(string member, string id, string title, string artist, string album, double duration);
        /// <summary>
        /// 資料夾改名／搬移後，把紀錄裡「在舊路徑底下」的曲目 id 改成新路徑（id 是相對路徑的 Base64）。
        /// oldRel／newRel 是 /分享名/資料夾 形式。回傳改了幾筆
        /// </summary>
        int MoveFolder(string oldRel, string newRel);
    }

    /// <summary>一筆播放紀錄（一行 JSON）</summary>
    public class PlayRecord
    {
        /// <summary>聽的時間（伺服器本地時間，yyyy-MM-ddTHH:mm:ss）</summary>
        [JsonPropertyName("t")]
        public string Time { get; set; }
        /// <summary>會員名稱</summary>
        [JsonPropertyName("m")]
        public string Member { get; set; }
        /// <summary>曲目 id：一般音樂檔是相對路徑的 Base64；CUE 拆出來的曲目是 cue:key:曲號</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }
        /// <summary>當時的曲名／演出者／專輯快照，之後檔案改名或刪掉也還看得出聽的是什麼</summary>
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("artist")]
        public string Artist { get; set; }
        [JsonPropertyName("album")]
        public string Album { get; set; }
        /// <summary>曲目長度（秒），不知道就是 0</summary>
        [JsonPropertyName("dur")]
        public int Duration { get; set; }
    }

    /// <summary>
    /// 會員聽過哪些曲目的長期紀錄，只追加不修改，存在 data/userData/plays.jsonl（一行一筆 JSON）。
    /// 由前端判斷「算聽過一次」（實際聽滿 30 秒或過半）後才回報，伺服器只負責驗證與記錄。
    /// </summary>
    public class PlayStatService : IPlayStatService
    {
        const int MaxTextLength = 200;
        const int MaxIdLength = 1000;

        static ILogger logger = LogManager.GetCurrentClassLogger();
        static readonly object thisLock = new object();

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "userData", "plays.jsonl");
        }

        // 一般音樂檔的 id 要能解成 / 開頭的相對路徑；CUE 曲目是 cue:key:曲號
        static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
            {
                return false;
            }
            if (id.StartsWith("cue:", StringComparison.Ordinal))
            {
                string[] parts = id.Split(':');
                int n;
                return parts.Length == 3 && parts[1].Length > 0 && int.TryParse(parts[2], out n) && n > 0;
            }
            try
            {
                return Helper.DecodeUrlBase64(id).Replace('\\', '/').StartsWith("/", StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string Trunc(string text)
        {
            text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= MaxTextLength ? text : text.Substring(0, MaxTextLength);
        }

        public bool Record(string member, string id, string title, string artist, string album, double duration)
        {
            if (string.IsNullOrEmpty(member) || IsValidId(id) == false)
            {
                return false;
            }
            var rec = new PlayRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Member = member,
                Id = id,
                Title = Trunc(title),
                Artist = Trunc(artist),
                Album = Trunc(album),
                Duration = (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0 || duration > 86400) ? 0 : (int)Math.Round(duration)
            };
            string line = JsonHelper.Serialize(rec) + "\r\n";
            lock (thisLock)
            {
                string path = DataPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            return true;
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
                string path = DataPath();
                if (File.Exists(path) == false)
                {
                    return 0;
                }
                int changed = 0;
                var sb = new StringBuilder();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    string outLine = line;
                    try
                    {
                        PlayRecord rec = JsonHelper.Deserialize<PlayRecord>(line);
                        string newId = rec == null ? null : FavoriteService.RemapId(rec.Id, oldRel, newRel);
                        if (newId != null)
                        {
                            rec.Id = newId;
                            outLine = JsonHelper.Serialize(rec);
                            changed++;
                        }
                    }
                    catch (Exception)
                    {
                        // 壞掉的行原樣保留，不讓它擋住其他筆
                    }
                    sb.Append(outLine).Append("\r\n");
                }
                if (changed > 0)
                {
                    SafeFile.WriteAllTextAtomic(path, sb.ToString());
                }
                return changed;
            }
        }
    }
}
