using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using WhatSearch.Core;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public interface IRelatedService
    {
        /// <summary>這個動畫資料夾名稱（與評分同一套 key 規則）的同組作品數（不含自己）；沒有關聯為 0</summary>
        int CountForName(string folderName);
        /// <summary>同組的其他作品（目前資料夾找得到的才列出）</summary>
        List<RelatedWork> GetRelated(string folderName);
        /// <summary>在動畫資料夾裡找名稱符合的作品（多個關鍵字用空白分隔，全部都要符合），排除指定名稱自己</summary>
        List<RelatedWork> Search(string query, string excludeFolderName, int max);
        /// <summary>
        /// 設定這個作品的關聯：keys 是「要留在同組」的其他作品的 key。沒在 keys 裡的現有成員會被移出這一組；
        /// keys 裡還沒在同組的作品會連同它原本的整組一起併進來（同組的作品互相關聯）。回傳設定後同組的其他作品
        /// </summary>
        List<RelatedWork> Apply(string folderName, IEnumerable<string> keys);
    }

    /// <summary>一個關聯作品：key 與評分相同（去掉結尾星等的資料夾名稱），其餘是它目前所在的資料夾</summary>
    public class RelatedWork
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string FolderId { get; set; }
        /// <summary>所在位置，例如 Anime / 2023-4</summary>
        public string Where { get; set; }
    }

    /// <summary>
    /// 跨季作品的「關聯推薦」：只有管理者能設定，所有人能看。一組裡的作品互相關聯（加入一個新作品時，它原本的整組一起併入）。
    /// 存在 data/related.json（[[key, key, ...], ...]，一組一個陣列）。key 與評分相同，所以資料夾搬位置（例如換季）關聯仍然跟著走。
    /// 作品所在位置不存檔，靠走訪評分用的分享根（依設定的層數）建索引，快取 60 秒；設定後立刻失效。
    /// </summary>
    public class RelatedService : IRelatedService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        readonly object gate = new object();
        readonly SystemConfig config = Ioc.GetConfig();
        List<HashSet<string>> groups;
        Dictionary<string, RelatedWork> index;
        DateTime indexTime = DateTime.MinValue;

        static string DataPath()
        {
            return Helper.GetRelativePath("data", "related.json");
        }

        List<HashSet<string>> Load()
        {
            if (groups != null)
            {
                return groups;
            }
            groups = new List<HashSet<string>>();
            try
            {
                string path = DataPath();
                if (File.Exists(path))
                {
                    var loaded = JsonHelper.Deserialize<List<List<string>>>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        foreach (var g in loaded)
                        {
                            if (g != null && g.Count >= 2)
                            {
                                groups.Add(new HashSet<string>(g.Where(k => string.IsNullOrEmpty(k) == false), StringComparer.Ordinal));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "讀取 related.json 失敗，以空白資料開始");
            }
            return groups;
        }

        // 呼叫端已持有 gate
        void Save()
        {
            try
            {
                var data = groups.Where(g => g.Count >= 2).Select(g => g.OrderBy(k => k, StringComparer.Ordinal).ToList()).ToList();
                SafeFile.WriteAllTextAtomic(DataPath(), JsonHelper.Serialize(data, indented: true));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "寫入 related.json 失敗");
                throw;
            }
        }

        // 呼叫端已持有 gate。走訪失敗（分享根沒掛載等）回傳空索引，這時不做「作品是否還在」的判斷
        Dictionary<string, RelatedWork> GetIndex()
        {
            if (index != null && (DateTime.Now - indexTime).TotalSeconds < 60)
            {
                return index;
            }
            var result = new Dictionary<string, RelatedWork>(StringComparer.Ordinal);
            var rc = config.Rating;
            if (rc != null && rc.Shares != null && rc.Shares.Count > 0)
            {
                IFolderIdManager fimgr = Ioc.Get<IFolderIdManager>();
                int depth = Math.Max(1, rc.Depth);
                foreach (string shareName in rc.Shares)
                {
                    FolderConfig root = (config.Folders ?? new List<FolderConfig>()).FirstOrDefault(f => f != null && string.Equals(f.Title, shareName, StringComparison.OrdinalIgnoreCase));
                    if (root == null || string.IsNullOrEmpty(root.Path) || Directory.Exists(root.Path) == false)
                    {
                        continue;
                    }
                    try
                    {
                        Walk(root.Path, 1, depth, fimgr, result);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "關聯推薦：走訪 {0} 失敗", shareName);
                    }
                }
            }
            index = result;
            indexTime = DateTime.Now;
            return index;
        }

        static void Walk(string dir, int level, int depth, IFolderIdManager fimgr, Dictionary<string, RelatedWork> result)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (HiddenFolder.IsHiddenName(name))
                {
                    continue;
                }
                if (level == depth)
                {
                    string key = RatingService.AnimeKey(name);
                    if (key.Length > 0 && result.ContainsKey(key) == false)
                    {
                        string rel;
                        string where = string.Empty;
                        if (PathUtility.TryGetRelPath(dir, out rel))
                        {
                            where = string.Join(" / ", rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
                        }
                        result[key] = new RelatedWork { Key = key, Title = key, FolderId = fimgr.GetIdByFilePath(sub), Where = where };
                    }
                }
                else
                {
                    Walk(sub, level + 1, depth, fimgr, result);
                }
            }
        }

        static string KeyOf(string folderName)
        {
            return RatingService.AnimeKey(folderName);
        }

        HashSet<string> GroupOf(string key)
        {
            return Load().FirstOrDefault(g => g.Contains(key));
        }

        // 同組其他成員裡，目前還找得到資料夾的 key（索引是空的代表無法判斷，全部算有）
        List<string> LiveMembers(string key, HashSet<string> group, Dictionary<string, RelatedWork> idx)
        {
            return group.Where(k => k != key && (idx.Count == 0 || idx.ContainsKey(k))).ToList();
        }

        public int CountForName(string folderName)
        {
            string key = KeyOf(folderName);
            lock (gate)
            {
                HashSet<string> g = GroupOf(key);
                if (g == null)
                {
                    return 0;
                }
                return LiveMembers(key, g, GetIndex()).Count;
            }
        }

        public List<RelatedWork> GetRelated(string folderName)
        {
            string key = KeyOf(folderName);
            lock (gate)
            {
                return RelatedOf(key);
            }
        }

        // 呼叫端已持有 gate
        List<RelatedWork> RelatedOf(string key)
        {
            HashSet<string> g = GroupOf(key);
            if (g == null)
            {
                return new List<RelatedWork>();
            }
            var idx = GetIndex();
            return LiveMembers(key, g, idx)
                .Select(k => idx.ContainsKey(k) ? idx[k] : new RelatedWork { Key = k, Title = k, FolderId = null, Where = string.Empty })
                .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public List<RelatedWork> Search(string query, string excludeFolderName, int max)
        {
            string[] tokens = (query ?? string.Empty).Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return new List<RelatedWork>();
            }
            string self = KeyOf(excludeFolderName);
            lock (gate)
            {
                return GetIndex().Values
                    .Where(w => w.Key != self && tokens.All(t => w.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Take(Math.Max(1, max))
                    .ToList();
            }
        }

        public List<RelatedWork> Apply(string folderName, IEnumerable<string> keys)
        {
            string me = KeyOf(folderName);
            var desired = new HashSet<string>((keys ?? new string[0]).Where(k => string.IsNullOrEmpty(k) == false), StringComparer.Ordinal);
            desired.Remove(me);
            lock (gate)
            {
                var all = Load();
                var idx = GetIndex();
                HashSet<string> mine = GroupOf(me);
                if (mine == null)
                {
                    mine = new HashSet<string>(StringComparer.Ordinal) { me };
                    all.Add(mine);
                }
                // 移出：不在 desired 裡、而且資料夾還在的成員（資料夾已經不在的不動，免得誤刪使用者看不到的舊關聯）
                foreach (string k in mine.ToList())
                {
                    if (k != me && desired.Contains(k) == false && (idx.Count == 0 || idx.ContainsKey(k)))
                    {
                        mine.Remove(k);
                    }
                }
                // 加入：只收找得到資料夾的作品；對方已有的整組一起併進來
                foreach (string k in desired)
                {
                    if (mine.Contains(k) || (idx.Count > 0 && idx.ContainsKey(k) == false))
                    {
                        continue;
                    }
                    HashSet<string> other = all.FirstOrDefault(g => g != mine && g.Contains(k));
                    if (other != null)
                    {
                        mine.UnionWith(other);
                        all.Remove(other);
                    }
                    else
                    {
                        mine.Add(k);
                    }
                }
                all.RemoveAll(g => g.Count < 2);
                Save();
                index = null;
                return RelatedOf(me);
            }
        }
    }
}
