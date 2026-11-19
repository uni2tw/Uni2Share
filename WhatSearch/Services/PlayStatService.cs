using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// <summary>某會員的聽歌統計。days：只算最近幾天，0 是全部</summary>
        PlayStatsResult GetStats(string member, int days);
    }

    public class PlayStatsResult
    {
        [JsonPropertyName("plays")] public int Plays { get; set; }
        [JsonPropertyName("tracks")] public int Tracks { get; set; }
        [JsonPropertyName("albums")] public int Albums { get; set; }
        /// <summary>每次聽過的曲目長度加總（秒；實際可能沒聽完）</summary>
        [JsonPropertyName("seconds")] public long Seconds { get; set; }
        /// <summary>這位會員最早的一筆紀錄時間（不受 days 影響），沒有紀錄是 null</summary>
        [JsonPropertyName("since")] public string Since { get; set; }
        [JsonPropertyName("topTracks")] public List<PlayStatTrack> TopTracks { get; set; } = new List<PlayStatTrack>();
        [JsonPropertyName("topAlbums")] public List<PlayStatAlbum> TopAlbums { get; set; } = new List<PlayStatAlbum>();
        [JsonPropertyName("recent")] public List<PlayStatTrack> Recent { get; set; } = new List<PlayStatTrack>();
    }

    public class PlayStatTrack
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("artist")] public string Artist { get; set; }
        [JsonPropertyName("album")] public string Album { get; set; }
        /// <summary>聽過幾次（recent 清單不用）</summary>
        [JsonPropertyName("count")] public int Count { get; set; }
        /// <summary>最近一次聽的時間</summary>
        [JsonPropertyName("last")] public string Last { get; set; }
        /// <summary>所在資料夾 id（前端用來跳過去）；CUE 曲目沒有</summary>
        [JsonPropertyName("folderId")] public string FolderId { get; set; }
        /// <summary>檔案目前已經不存在（由 API 層檢查後標上；CUE 曲目不檢查）</summary>
        [JsonPropertyName("missing")] public bool Missing { get; set; }
    }

    public class PlayStatAlbum
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        /// <summary>聽過幾首不同的歌</summary>
        [JsonPropertyName("tracks")] public int Tracks { get; set; }
        [JsonPropertyName("last")] public string Last { get; set; }
        [JsonPropertyName("folderId")] public string FolderId { get; set; }
        /// <summary>資料夾目前已經不存在（由 API 層檢查後標上）</summary>
        [JsonPropertyName("missing")] public bool Missing { get; set; }
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

        /// <summary>Time 解析後的值（記憶體快取用，不存檔）</summary>
        [JsonIgnore]
        public DateTime Parsed { get; set; }
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

        // 全部紀錄的記憶體快取（依檔案順序，也就是時間順序）；一年約三萬筆、數 MB，統計時不用每次重讀檔
        List<PlayRecord> cache;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "userData", "plays.jsonl");
        }

        List<PlayRecord> Load()
        {
            if (cache != null)
            {
                return cache;
            }
            var list = new List<PlayRecord>();
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        if (line.Length == 0)
                        {
                            continue;
                        }
                        try
                        {
                            PlayRecord rec = JsonHelper.Deserialize<PlayRecord>(line);
                            DateTime dt;
                            if (rec != null && string.IsNullOrEmpty(rec.Id) == false && DateTime.TryParse(rec.Time, out dt))
                            {
                                rec.Parsed = dt;
                                list.Add(rec);
                            }
                        }
                        catch (Exception)
                        {
                            // 壞掉的行略過
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 plays.jsonl 失敗，統計以空白資料開始");
            }
            cache = list;
            return cache;
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
                if (cache != null)
                {
                    rec.Parsed = DateTime.Now;
                    cache.Add(rec);
                }
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
                    cache = null;
                }
                return changed;
            }
        }

        // 曲目所在的資料夾（id 解開後去掉檔名）；CUE 曲目沒有對應資料夾
        static string FolderOf(string id)
        {
            if (id.StartsWith("cue:", StringComparison.Ordinal))
            {
                return null;
            }
            try
            {
                string rel = Helper.DecodeUrlBase64(id).Replace('\\', '/');
                int i = rel.LastIndexOf('/');
                return i > 0 ? rel.Substring(0, i) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string FolderIdOf(string id)
        {
            string dir = FolderOf(id);
            return dir == null ? null : Helper.EncodeUrlBase64(dir);
        }

        // 專輯分組：一般曲目用所在資料夾，CUE 曲目用 cue:key
        static string AlbumKeyOf(string id)
        {
            if (id.StartsWith("cue:", StringComparison.Ordinal))
            {
                string[] parts = id.Split(':');
                return parts.Length >= 2 ? "cue:" + parts[1] : id;
            }
            return FolderOf(id) ?? "/";
        }

        static string FileTitleOf(string id)
        {
            try
            {
                string rel = Helper.DecodeUrlBase64(id).Replace('\\', '/');
                return Path.GetFileNameWithoutExtension(rel);
            }
            catch (Exception)
            {
                return id;
            }
        }

        static string Fmt(DateTime dt)
        {
            return dt.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        public PlayStatsResult GetStats(string member, int days)
        {
            var result = new PlayStatsResult();
            if (string.IsNullOrEmpty(member))
            {
                return result;
            }
            var mine = new List<PlayRecord>();
            lock (thisLock)
            {
                foreach (PlayRecord r in Load())
                {
                    if (string.Equals(r.Member, member, StringComparison.Ordinal))
                    {
                        mine.Add(r);
                    }
                }
            }
            if (mine.Count == 0)
            {
                return result;
            }
            result.Since = Fmt(mine[0].Parsed);
            DateTime? since = days > 0 ? DateTime.Now.AddDays(-days) : (DateTime?)null;
            List<PlayRecord> list = since.HasValue ? mine.FindAll(r => r.Parsed >= since.Value) : mine;
            result.Plays = list.Count;
            result.Seconds = list.Sum(r => (long)r.Duration);

            var byTrack = list.GroupBy(r => r.Id).Select(g =>
            {
                PlayRecord latest = g.Last();
                return new PlayStatTrack
                {
                    Id = g.Key,
                    Title = string.IsNullOrEmpty(latest.Title) ? FileTitleOf(g.Key) : latest.Title,
                    Artist = latest.Artist ?? string.Empty,
                    Album = latest.Album ?? string.Empty,
                    Count = g.Count(),
                    Last = Fmt(g.Max(r => r.Parsed)),
                    FolderId = FolderIdOf(g.Key)
                };
            }).ToList();
            result.Tracks = byTrack.Count;
            result.TopTracks = byTrack.OrderByDescending(t => t.Count).ThenByDescending(t => t.Last).Take(20).ToList();

            var byAlbum = list.GroupBy(r => AlbumKeyOf(r.Id)).Select(g =>
            {
                PlayRecord latest = g.Last();
                bool isCue = g.Key.StartsWith("cue:", StringComparison.Ordinal);
                string name = latest.Album;
                if (string.IsNullOrEmpty(name))
                {
                    name = isCue ? "（CUE 專輯）" : g.Key.Substring(g.Key.LastIndexOf('/') + 1);
                }
                return new PlayStatAlbum
                {
                    Name = name,
                    Count = g.Count(),
                    Tracks = g.Select(r => r.Id).Distinct().Count(),
                    Last = Fmt(g.Max(r => r.Parsed)),
                    FolderId = isCue ? null : Helper.EncodeUrlBase64(g.Key)
                };
            }).ToList();
            result.Albums = byAlbum.Count;
            result.TopAlbums = byAlbum.OrderByDescending(a => a.Count).ThenByDescending(a => a.Last).Take(10).ToList();

            for (int i = list.Count - 1; i >= 0 && result.Recent.Count < 30; i--)
            {
                PlayRecord r = list[i];
                result.Recent.Add(new PlayStatTrack
                {
                    Id = r.Id,
                    Title = string.IsNullOrEmpty(r.Title) ? FileTitleOf(r.Id) : r.Title,
                    Artist = r.Artist ?? string.Empty,
                    Album = r.Album ?? string.Empty,
                    Last = Fmt(r.Parsed),
                    FolderId = FolderIdOf(r.Id)
                });
            }
            return result;
        }
    }
}
