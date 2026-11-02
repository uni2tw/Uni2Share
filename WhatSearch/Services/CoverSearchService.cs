using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WhatSearch.Core;
using WhatSearch.Services.Interfaces;

namespace WhatSearch.Services
{
    public class CoverCandidate
    {
        public string Title { get; set; }
        public string SubTitle { get; set; }
        public int? Year { get; set; }
        public string Format { get; set; }
        /// <summary>來源網站名稱（AniList、Bangumi）</summary>
        public string Source { get; set; }
        /// <summary>縮圖（給挑選畫面用）</summary>
        public string Thumb { get; set; }
        /// <summary>大圖（選定後由伺服器下載）</summary>
        public string Image { get; set; }
        /// <summary>作品的各種名稱（中文、日文、英文…），算相關度用</summary>
        public List<string> Names { get; set; } = new List<string>();
        /// <summary>原文（日文）名稱，拿來去 AniList 找同一部作品；沒有就是 null</summary>
        public string NativeName { get; set; }
        /// <summary>和搜尋關鍵字的相關度（0～1），排序與過濾用</summary>
        public double Score { get; set; }
    }

    public class CoverSearchResult
    {
        public List<CoverCandidate> Items { get; set; } = new List<CoverCandidate>();
        /// <summary>有來源查詢失敗時的說明（其他來源仍有結果）</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public interface ICoverSearchService
    {
        /// <summary>
        /// 同時到 AniList 與 Bangumi 搜尋動畫：關鍵字先轉成簡體再查 Bangumi（它的資料是簡體）；
        /// Bangumi 找到的作品再用日文原名到 AniList 補查；全部依和關鍵字的相關度排序，明顯不相關的不列出。
        /// 只有一個來源失敗時，結果照常回傳並在 Warnings 說明；全部失敗才丟出 InvalidOperationException（訊息可直接顯示）。
        /// </summary>
        Task<CoverSearchResult> SearchAsync(string keyword);

        /// <summary>這個網址是不是可以由伺服器下載的封面來源（只收 AniList、Bangumi 圖片網域的 https）</summary>
        bool IsAllowedImageUrl(string url);

        /// <summary>下載候選封面；只收 JPEG、最大 8 MB，不符合會丟出 InvalidOperationException（訊息可直接顯示）</summary>
        Task<byte[]> DownloadJpegAsync(string url);
    }

    public class CoverSearchService : ICoverSearchService
    {
        const string ApiUrl = "https://graphql.anilist.co";
        const string BangumiApiUrl = "https://api.bgm.tv/v0/search/subjects?limit=10";
        const int MaxImageBytes = 8 * 1024 * 1024;

        // 相關度：最低門檻，以及「不低於最佳結果的幾成」
        const double MinScore = 0.34;
        const double RelativeCutoff = 0.5;
        const int MinKeep = 3;
        const int MaxItems = 12;
        // Bangumi 結果中夠相關的前幾部，拿它們的日文原名去 AniList 補查
        const double SpawnMinScore = 0.5;
        const int SpawnMax = 2;

        const string Query = @"query ($keyword: String!, $perPage: Int!) {
  Page(page: 1, perPage: $perPage) {
    media(search: $keyword, type: ANIME) {
      title { native romaji english }
      seasonYear
      format
      coverImage { extraLarge medium }
    }
  }
}";

        // 不自動跟著轉址，避免被導到允許網域以外的地方
        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            // Bangumi 的 API 要求要有識別用的 User-Agent
            client.DefaultRequestHeaders.UserAgent.ParseAdd("uni2tw-Uni2Share/1.0 (https://github.com/uni2tw/Uni2Share)");
            return client;
        }

        public async Task<CoverSearchResult> SearchAsync(string keyword)
        {
            // 原關鍵字和簡體版都查（轉換可能轉錯專有名詞，所以不只用轉換後的）
            string simplified = ToSimplified(keyword);
            List<string> keys = new List<string> { keyword };
            if (string.Equals(simplified, keyword, StringComparison.Ordinal) == false)
            {
                keys.Add(simplified);
            }

            List<Task<List<CoverCandidate>>> bangumiTasks = keys.Select(k => SearchBangumiAsync(k)).ToList();
            Task<List<CoverCandidate>> aniListTask = SearchAniListAsync(keyword, 10);
            try { await Task.WhenAll(bangumiTasks.Cast<Task>().Concat(new Task[] { aniListTask })); } catch (Exception) { /* 個別結果在下面處理 */ }

            CoverSearchResult result = new CoverSearchResult();
            List<CoverCandidate> bangumi = CollectMany("Bangumi", bangumiTasks, result);
            List<CoverCandidate> aniList = CollectMany("AniList", new List<Task<List<CoverCandidate>>> { aniListTask }, result);
            if (bangumi == null && aniList == null)
            {
                throw new InvalidOperationException(string.Join("；", result.Warnings));
            }
            bangumi = DistinctByImage(bangumi ?? new List<CoverCandidate>());
            aniList = DistinctByImage(aniList ?? new List<CoverCandidate>());

            List<string> normalizedKeys = keys.Select(Normalize).Where(s => s.Length > 0).ToList();
            foreach (CoverCandidate c in bangumi.Concat(aniList))
            {
                c.Score = normalizedKeys.Count == 0 ? 0 : normalizedKeys.Max(k => BestSimilarity(k, c.Names));
            }

            // 第二段：Bangumi 找到的相關作品，用日文原名到 AniList 補查（AniList 不認中文，但認日文原名）
            List<CoverCandidate> spawns = bangumi
                .Where(c => c.Score >= SpawnMinScore && string.IsNullOrEmpty(c.NativeName) == false)
                .OrderByDescending(c => c.Score)
                .GroupBy(c => c.NativeName)
                .Select(g => g.First())
                .Take(SpawnMax)
                .ToList();
            List<CoverCandidate> extra = new List<CoverCandidate>();
            if (spawns.Count > 0)
            {
                List<Task<List<CoverCandidate>>> tasks = spawns.Select(s => SearchAniListAsync(s.NativeName, 3)).ToList();
                try { await Task.WhenAll(tasks); } catch (Exception) { /* 補查失敗不影響主要結果 */ }
                for (int i = 0; i < spawns.Count; i++)
                {
                    if (tasks[i].IsCompletedSuccessfully == false)
                    {
                        continue;
                    }
                    string native = Normalize(spawns[i].NativeName);
                    foreach (CoverCandidate c in tasks[i].Result)
                    {
                        // 分數 = 該 Bangumi 作品的相關度 × 這筆和日文原名有多像（原名完全相同就等於繼承相關度）
                        c.Score = spawns[i].Score * BestSimilarity(native, c.Names);
                        extra.Add(c);
                    }
                }
            }

            List<CoverCandidate> all = DistinctByImage(bangumi.Concat(aniList).Concat(extra).ToList());
            // 同一張圖重複時保留分數高的，再依相關度由高到低排序（分數相同維持原來的順序）
            all = all.OrderByDescending(c => c.Score).ToList();
            if (all.Count > 0)
            {
                double cutoff = Math.Max(MinScore, all[0].Score * RelativeCutoff);
                result.Items = all.Where((c, i) => i < MinKeep || c.Score >= cutoff).Take(MaxItems).ToList();
            }
            return result;
        }

        // 同一張圖（網址相同）只留一筆，保留分數較高的
        static List<CoverCandidate> DistinctByImage(List<CoverCandidate> list)
        {
            return list.GroupBy(c => c.Image).Select(g => g.OrderByDescending(c => c.Score).First()).ToList();
        }

        // 全部失敗回傳 null（並記下警告）；有任何一個成功就合併成功的結果（失敗的不影響）
        static List<CoverCandidate> CollectMany(string name, List<Task<List<CoverCandidate>>> tasks, CoverSearchResult result)
        {
            List<CoverCandidate> merged = new List<CoverCandidate>();
            Exception firstError = null;
            bool anyOk = false;
            foreach (Task<List<CoverCandidate>> task in tasks)
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    firstError = firstError ?? (task.Exception == null ? null : task.Exception.InnerException);
                    continue;
                }
                anyOk = true;
                merged.AddRange(task.Result);
            }
            if (anyOk == false)
            {
                result.Warnings.Add(name + "：" + (firstError is InvalidOperationException ? firstError.Message : "查詢失敗"));
                return null;
            }
            return merged;
        }

        static string ToSimplified(string text)
        {
            try
            {
                string s = Ioc.Get<IChineseConverter>().ToSimplifiedChinese(text);
                return string.IsNullOrEmpty(s) ? text : s;
            }
            catch (Exception)
            {
                return text;
            }
        }

        // 比對用：轉簡體、小寫、只留文字與數字（標點、空白都去掉）
        static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            StringBuilder sb = new StringBuilder();
            foreach (char ch in ToSimplified(text).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        static double BestSimilarity(string normalizedQuery, IEnumerable<string> names)
        {
            double best = 0;
            foreach (string name in names)
            {
                best = Math.Max(best, Similarity(normalizedQuery, Normalize(name)));
            }
            return best;
        }

        // 關鍵字的「連續兩個字」有多少出現在名稱裡（召回，佔 75%），加上兩邊長度相近的程度（Dice，佔 25%）。
        // 關鍵字通常只是名稱的一部分，所以以召回為主；完全相同是 1
        static double Similarity(string query, string name)
        {
            HashSet<string> q = Bigrams(query);
            HashSet<string> n = Bigrams(name);
            if (q.Count == 0 || n.Count == 0)
            {
                return 0;
            }
            int common = q.Count(n.Contains);
            double recall = (double)common / q.Count;
            double dice = 2.0 * common / (q.Count + n.Count);
            return 0.75 * recall + 0.25 * dice;
        }

        static HashSet<string> Bigrams(string s)
        {
            HashSet<string> set = new HashSet<string>();
            if (string.IsNullOrEmpty(s))
            {
                return set;
            }
            if (s.Length == 1)
            {
                set.Add(s);
                return set;
            }
            for (int i = 0; i < s.Length - 1; i++)
            {
                set.Add(s.Substring(i, 2));
            }
            return set;
        }

        async Task<List<CoverCandidate>> SearchBangumiAsync(string keyword)
        {
            string body = JsonSerializer.Serialize(new { keyword = keyword, filter = new { type = new[] { 2 } } });
            string json;
            try
            {
                using (HttpResponseMessage res = await http.PostAsync(BangumiApiUrl, new StringContent(body, Encoding.UTF8, "application/json")))
                {
                    json = await res.Content.ReadAsStringAsync();
                    if (res.IsSuccessStatusCode == false)
                    {
                        throw new InvalidOperationException("Bangumi 回應錯誤（" + (int)res.StatusCode + "）");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("連不上 Bangumi");
            }

            List<CoverCandidate> list = new List<CoverCandidate>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach (JsonElement m in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        string nameCn = GetString(m, "name_cn");
                        string name = GetString(m, "name");
                        JsonElement images = m.GetProperty("images");
                        string image = GetString(images, "large");
                        string thumb = GetString(images, "small");
                        if (IsAllowedImageUrl(image) == false)
                        {
                            continue;
                        }
                        string date = GetString(m, "date");
                        int year;
                        int? y = date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out year) ? year : (int?)null;
                        string title = nameCn ?? name;
                        list.Add(new CoverCandidate
                        {
                            Title = title,
                            SubTitle = name != null && name != title ? name : null,
                            Year = y,
                            Format = GetString(m, "platform"),
                            Source = "Bangumi",
                            NativeName = name,
                            Names = new[] { nameCn, name }.Where(s => s != null).ToList(),
                            Thumb = IsAllowedImageUrl(thumb) ? thumb : image,
                            Image = image
                        });
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Bangumi 回傳的資料看不懂");
            }
            return list;
        }

        async Task<List<CoverCandidate>> SearchAniListAsync(string keyword, int perPage)
        {
            string body = JsonSerializer.Serialize(new { query = Query, variables = new { keyword = keyword, perPage = perPage } });
            string json;
            try
            {
                using (HttpResponseMessage res = await http.PostAsync(ApiUrl, new StringContent(body, Encoding.UTF8, "application/json")))
                {
                    json = await res.Content.ReadAsStringAsync();
                    if (res.StatusCode == (HttpStatusCode)429)
                    {
                        throw new InvalidOperationException("AniList 要求稍後再試，請等一下再搜尋");
                    }
                    if (res.IsSuccessStatusCode == false)
                    {
                        throw new InvalidOperationException("AniList 回應錯誤（" + (int)res.StatusCode + "）");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("連不上 AniList，請確認主機可以連到外網");
            }

            List<CoverCandidate> list = new List<CoverCandidate>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement media = doc.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media");
                    foreach (JsonElement m in media.EnumerateArray())
                    {
                        JsonElement title = m.GetProperty("title");
                        string native = GetString(title, "native");
                        string romaji = GetString(title, "romaji");
                        string english = GetString(title, "english");
                        string image = GetString(m.GetProperty("coverImage"), "extraLarge");
                        string thumb = GetString(m.GetProperty("coverImage"), "medium");
                        if (IsAllowedImageUrl(image) == false)
                        {
                            continue;
                        }
                        int? year = m.TryGetProperty("seasonYear", out JsonElement y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : (int?)null;
                        list.Add(new CoverCandidate
                        {
                            Title = native ?? romaji ?? english,
                            SubTitle = string.Join(" / ", new[] { romaji, english }.Where(s => s != null && s != (native ?? romaji ?? english))),
                            Year = year,
                            Format = GetString(m, "format"),
                            Source = "AniList",
                            Names = new[] { native, romaji, english }.Where(s => s != null).ToList(),
                            Thumb = IsAllowedImageUrl(thumb) ? thumb : image,
                            Image = image
                        });
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException("AniList 回傳的資料看不懂");
            }
            return list;
        }

        static string GetString(JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            return null;
        }

        public bool IsAllowedImageUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || Uri.TryCreate(url, UriKind.Absolute, out Uri uri) == false)
            {
                return false;
            }
            return uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.Host.EndsWith(".anilist.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bgm.tv", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<byte[]> DownloadJpegAsync(string url)
        {
            if (IsAllowedImageUrl(url) == false)
            {
                throw new InvalidOperationException("不支援的圖片來源");
            }
            byte[] data;
            try
            {
                using (HttpResponseMessage res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (res.IsSuccessStatusCode == false)
                    {
                        throw new InvalidOperationException("下載圖片失敗（" + (int)res.StatusCode + "）");
                    }
                    if (res.Content.Headers.ContentLength > MaxImageBytes)
                    {
                        throw new InvalidOperationException("圖片太大");
                    }
                    using (Stream s = await res.Content.ReadAsStreamAsync())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920];
                        int n;
                        while ((n = await s.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            ms.Write(buffer, 0, n);
                            if (ms.Length > MaxImageBytes)
                            {
                                throw new InvalidOperationException("圖片太大");
                            }
                        }
                        data = ms.ToArray();
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("下載圖片失敗，請確認主機可以連到外網");
            }
            if (data.Length < 3 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF)
            {
                throw new InvalidOperationException("這張圖不是 JPEG，無法存成 cover.jpg");
            }
            return data;
        }
    }
}
