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
        /// <summary>來源網站名稱（AniList、Bangumi、Kitsu）</summary>
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
        /// 同時到 AniList、Bangumi、Kitsu 搜尋動畫：關鍵字先轉成簡體再查 Bangumi（它的資料是簡體）；
        /// Bangumi 找到的作品再用日文原名到 AniList 與 Kitsu 補查（它們不認中文，但認日文原名）；全部依和關鍵字的相關度排序，明顯不相關的不列出。
        /// 只有一個來源失敗時，結果照常回傳並在 Warnings 說明；全部失敗才丟出 InvalidOperationException（訊息可直接顯示）。
        /// </summary>
        Task<CoverSearchResult> SearchAsync(string keyword);

        /// <summary>
        /// 同時到 iTunes（台灣、美國、日本商店）與 MusicBrainz／Cover Art Archive 搜尋專輯封面（都是方形大圖）；有演出者就一起查、一起算相關度。
        /// 失敗處理與 SearchAsync 相同。
        /// </summary>
        Task<CoverSearchResult> SearchAlbumAsync(string album, string artist);

        /// <summary>這個網址是不是可以由伺服器下載的封面來源（只收 AniList、Bangumi、Kitsu、iTunes、Cover Art Archive 圖片網域的 https）</summary>
        bool IsAllowedImageUrl(string url);

        /// <summary>下載候選封面；只收 JPEG、最大 8 MB，不符合會丟出 InvalidOperationException（訊息可直接顯示）</summary>
        Task<byte[]> DownloadJpegAsync(string url);
    }

    public class CoverSearchService : ICoverSearchService
    {
        const string ApiUrl = "https://graphql.anilist.co";
        const string BangumiApiUrl = "https://api.bgm.tv/v0/search/subjects?limit=10";
        const string KitsuApiUrl = "https://kitsu.io/api/edge/anime?filter%5Btext%5D=";
        const string ITunesApiUrl = "https://itunes.apple.com/search?media=music&entity=album&limit=12&country=";
        const string MusicBrainzApiUrl = "https://musicbrainz.org/ws/2/release-group/?fmt=json&limit=8&query=";
        const string CoverArtArchiveUrl = "https://coverartarchive.org/release-group/";
        // MusicBrainz 搜到的專輯不一定有封面，只檢查相關度最高的前幾筆
        const int MusicBrainzProbeMax = 6;
        // 台灣商店中文專輯最全，美國商店歐美最全，日本商店補日系專輯
        static readonly string[] ITunesCountries = { "TW", "US", "JP" };
        const int MaxImageBytes = 8 * 1024 * 1024;

        // 相關度：最低門檻，以及「不低於最佳結果的幾成」
        const double MinScore = 0.34;
        const double RelativeCutoff = 0.5;
        const int MinKeep = 1;
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
                Timeout = TimeSpan.FromSeconds(12)
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
            Task<List<CoverCandidate>> kitsuTask = SearchKitsuAsync(keyword, 10);
            try { await Task.WhenAll(bangumiTasks.Cast<Task>().Concat(new Task[] { aniListTask, kitsuTask })); } catch (Exception) { /* 個別結果在下面處理 */ }

            CoverSearchResult result = new CoverSearchResult();
            List<CoverCandidate> bangumi = CollectMany("Bangumi", bangumiTasks, result);
            List<CoverCandidate> aniList = CollectMany("AniList", new List<Task<List<CoverCandidate>>> { aniListTask }, result);
            List<CoverCandidate> kitsu = CollectMany("Kitsu", new List<Task<List<CoverCandidate>>> { kitsuTask }, result);
            if (bangumi == null && aniList == null && kitsu == null)
            {
                throw new InvalidOperationException(string.Join("；", result.Warnings));
            }
            bangumi = DistinctByImage(bangumi ?? new List<CoverCandidate>());
            aniList = DistinctByImage(aniList ?? new List<CoverCandidate>());
            kitsu = DistinctByImage(kitsu ?? new List<CoverCandidate>());

            List<string> normalizedKeys = keys.Select(Normalize).Where(s => s.Length > 0).ToList();
            foreach (CoverCandidate c in bangumi.Concat(aniList).Concat(kitsu))
            {
                c.Score = normalizedKeys.Count == 0 ? 0 : normalizedKeys.Max(k => BestSimilarity(k, c.Names));
            }

            // 第二段：Bangumi 找到的相關作品，用日文原名到 AniList 與 Kitsu 補查（它們不認中文，但認日文原名）
            List<CoverCandidate> spawns = PickSpawns(bangumi);
            if (spawns.Count == 0)
            {
                // 動畫類型裡沒有夠相關的（例如作品在 Bangumi 只有漫畫、小說條目）：不限類型再查一次，
                // 只拿它的日文原名去補查，這些條目本身（書籍封面）不會列出來
                List<Task<List<CoverCandidate>>> anyTypeTasks = keys.Select(k => SearchBangumiAsync(k, true)).ToList();
                try { await Task.WhenAll(anyTypeTasks); } catch (Exception) { /* 查不到就算了 */ }
                List<CoverCandidate> anyType = anyTypeTasks.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result).ToList();
                foreach (CoverCandidate c in anyType)
                {
                    c.Score = normalizedKeys.Count == 0 ? 0 : normalizedKeys.Max(k => BestSimilarity(k, c.Names));
                }
                spawns = PickSpawns(anyType);
            }
            List<CoverCandidate> extra = new List<CoverCandidate>();
            if (spawns.Count > 0)
            {
                List<Task<List<CoverCandidate>>> tasks = spawns.Select(s => SearchAniListAsync(s.NativeName, 3))
                    .Concat(spawns.Select(s => SearchKitsuAsync(s.NativeName, 3))).ToList();
                try { await Task.WhenAll(tasks); } catch (Exception) { /* 補查失敗不影響主要結果 */ }
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i].IsCompletedSuccessfully == false)
                    {
                        continue;
                    }
                    CoverCandidate spawn = spawns[i % spawns.Count];
                    string native = Normalize(spawn.NativeName);
                    foreach (CoverCandidate c in tasks[i].Result)
                    {
                        // 分數 = 該 Bangumi 作品的相關度 × 這筆和日文原名有多像（原名完全相同就等於繼承相關度）
                        c.Score = spawn.Score * BestSimilarity(native, c.Names);
                        extra.Add(c);
                    }
                }
            }

            List<CoverCandidate> all = DistinctByImage(bangumi.Concat(aniList).Concat(kitsu).Concat(extra).ToList());
            // 同一張圖重複時保留分數高的，再依相關度由高到低排序（分數相同維持原來的順序）
            all = all.OrderByDescending(c => c.Score).ToList();
            // 最好的一筆都不夠相關，就當作找不到，不要硬湊幾筆不相關的
            if (all.Count > 0 && all[0].Score >= MinScore)
            {
                double cutoff = Math.Max(MinScore, all[0].Score * RelativeCutoff);
                result.Items = all.Where((c, i) => i < MinKeep || c.Score >= cutoff).Take(MaxItems).ToList();
            }
            return result;
        }

        // 挑出夠相關、有日文原名的前幾部（同名只留一筆），拿去補查
        static List<CoverCandidate> PickSpawns(List<CoverCandidate> list)
        {
            return list
                .Where(c => c.Score >= SpawnMinScore && string.IsNullOrEmpty(c.NativeName) == false)
                .OrderByDescending(c => c.Score)
                .GroupBy(c => c.NativeName)
                .Select(g => g.First())
                .Take(SpawnMax)
                .ToList();
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

        async Task<List<CoverCandidate>> SearchKitsuAsync(string keyword, int limit)
        {
            string url = KitsuApiUrl + Uri.EscapeDataString(keyword) + "&page%5Blimit%5D=" + limit;
            string json;
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Accept.ParseAdd("application/vnd.api+json");
                    using (HttpResponseMessage res = await http.SendAsync(req))
                    {
                        json = await res.Content.ReadAsStringAsync();
                        if (res.IsSuccessStatusCode == false)
                        {
                            throw new InvalidOperationException("Kitsu 回應錯誤（" + (int)res.StatusCode + "）");
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("連不上 Kitsu");
            }

            List<CoverCandidate> list = new List<CoverCandidate>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach (JsonElement m in doc.RootElement.GetProperty("data").EnumerateArray())
                    {
                        JsonElement attr = m.GetProperty("attributes");
                        if (attr.TryGetProperty("posterImage", out JsonElement poster) == false || poster.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        // 原圖若是 jpg／jpeg 就用原圖（解析度最高），否則用 large（Kitsu 的縮圖都是 JPEG）
                        string original = GetString(poster, "original");
                        string image = original != null && System.Text.RegularExpressions.Regex.IsMatch(original, @"\.jpe?g(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                            ? original : GetString(poster, "large");
                        string thumb = GetString(poster, "small");
                        if (IsAllowedImageUrl(image) == false)
                        {
                            continue;
                        }
                        List<string> names = new List<string>();
                        string canonical = GetString(attr, "canonicalTitle");
                        if (canonical != null) { names.Add(canonical); }
                        string japanese = null;
                        if (attr.TryGetProperty("titles", out JsonElement titles) && titles.ValueKind == JsonValueKind.Object)
                        {
                            foreach (JsonProperty p in titles.EnumerateObject())
                            {
                                if (p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Value.GetString()) == false)
                                {
                                    names.Add(p.Value.GetString());
                                    if (p.Name == "ja_jp")
                                    {
                                        japanese = p.Value.GetString();
                                    }
                                }
                            }
                        }
                        names = names.Distinct().ToList();
                        string title = japanese ?? canonical;
                        string english = names.FirstOrDefault(n => n != title && n.All(ch => ch < 0x2E80));
                        string start = GetString(attr, "startDate");
                        int year;
                        int? y = start != null && start.Length >= 4 && int.TryParse(start.Substring(0, 4), out year) ? year : (int?)null;
                        list.Add(new CoverCandidate
                        {
                            Title = title,
                            SubTitle = english,
                            Year = y,
                            Format = GetString(attr, "subtype") == null ? null : GetString(attr, "subtype").ToUpperInvariant(),
                            Source = "Kitsu",
                            Names = names,
                            Thumb = IsAllowedImageUrl(thumb) ? thumb : image,
                            Image = image
                        });
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException("Kitsu 回傳的資料看不懂");
            }
            return list;
        }

        // anyType = true 時不限條目類型（含書籍、遊戲…），只給「找日文原名」用
        async Task<List<CoverCandidate>> SearchBangumiAsync(string keyword, bool anyType = false)
        {
            string body = anyType
                ? JsonSerializer.Serialize(new { keyword = keyword })
                : JsonSerializer.Serialize(new { keyword = keyword, filter = new { type = new[] { 2 } } });
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

        public async Task<CoverSearchResult> SearchAlbumAsync(string album, string artist)
        {
            string term = string.IsNullOrWhiteSpace(artist) ? album : artist.Trim() + " " + album;
            List<Task<List<CoverCandidate>>> iTunesTasks = ITunesCountries.Select(c => SearchITunesAsync(term, c)).ToList();
            Task<List<CoverCandidate>> brainzTask = SearchMusicBrainzAsync(album, artist);
            try { await Task.WhenAll(iTunesTasks.Cast<Task>().Concat(new Task[] { brainzTask })); } catch (Exception) { /* 個別結果在下面處理 */ }

            CoverSearchResult result = new CoverSearchResult();
            List<CoverCandidate> iTunes = CollectMany("iTunes", iTunesTasks, result);
            List<CoverCandidate> brainz = CollectMany("MusicBrainz", new List<Task<List<CoverCandidate>>> { brainzTask }, result);
            if (iTunes == null && brainz == null)
            {
                throw new InvalidOperationException(string.Join("；", result.Warnings));
            }

            // 專輯名稱是主要依據（7 成），有演出者再加上演出者的相符程度（3 成）
            string nAlbum = Normalize(album);
            string nArtist = Normalize(artist);
            List<CoverCandidate> all = (iTunes ?? new List<CoverCandidate>()).Concat(brainz ?? new List<CoverCandidate>()).ToList();
            foreach (CoverCandidate c in all)
            {
                double albumScore = nAlbum.Length == 0 ? 0 : BestSimilarity(nAlbum, c.Names);
                c.Score = nArtist.Length == 0 ? albumScore : 0.7 * albumScore + 0.3 * BestSimilarity(nArtist, new[] { c.SubTitle });
            }
            // 不同商店常是同一張圖；同圖保留分數高的，再依相關度排序
            all = DistinctByImage(all).OrderByDescending(c => c.Score).ToList();
            if (all.Count > 0 && all[0].Score >= MinScore)
            {
                double cutoff = Math.Max(MinScore, all[0].Score * RelativeCutoff);
                result.Items = all.Where((c, i) => i < MinKeep || c.Score >= cutoff).Take(MaxItems).ToList();
            }
            return result;
        }

        async Task<string> GetJsonAsync(string url, string name)
        {
            try
            {
                using (HttpResponseMessage res = await http.GetAsync(url))
                {
                    string json = await res.Content.ReadAsStringAsync();
                    if (res.StatusCode == (HttpStatusCode)429 || res.StatusCode == HttpStatusCode.Forbidden || res.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        throw new InvalidOperationException(name + " 要求稍後再試，請等一下再搜尋");
                    }
                    if (res.IsSuccessStatusCode == false)
                    {
                        throw new InvalidOperationException(name + " 回應錯誤（" + (int)res.StatusCode + "）");
                    }
                    return json;
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new InvalidOperationException("連不上 " + name);
            }
        }

        async Task<List<CoverCandidate>> SearchITunesAsync(string term, string country)
        {
            string json = await GetJsonAsync(ITunesApiUrl + country + "&term=" + Uri.EscapeDataString(term), "iTunes");
            List<CoverCandidate> list = new List<CoverCandidate>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach (JsonElement m in doc.RootElement.GetProperty("results").EnumerateArray())
                    {
                        string name = GetString(m, "collectionName");
                        string art = GetString(m, "artworkUrl100");
                        if (name == null || art == null)
                        {
                            continue;
                        }
                        // 圖片網址尾端是尺寸與格式（…/100x100bb.jpg），改成 1000×1000 的 JPEG
                        string image = System.Text.RegularExpressions.Regex.Replace(art, @"/\d+x\d+bb(\.\w+)?$", "/1000x1000bb.jpg");
                        string thumb = System.Text.RegularExpressions.Regex.Replace(art, @"/\d+x\d+bb(\.\w+)?$", "/300x300bb.jpg");
                        if (IsAllowedImageUrl(image) == false)
                        {
                            continue;
                        }
                        string date = GetString(m, "releaseDate");
                        int year;
                        int? y = date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out year) ? year : (int?)null;
                        list.Add(new CoverCandidate
                        {
                            Title = name,
                            SubTitle = GetString(m, "artistName"),
                            Year = y,
                            Source = "iTunes",
                            Names = new List<string> { name },
                            Thumb = IsAllowedImageUrl(thumb) ? thumb : image,
                            Image = image
                        });
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException("iTunes 回傳的資料看不懂");
            }
            return list;
        }

        // MusicBrainz 查專輯（release group），再到 Cover Art Archive 確認有沒有封面；有才列出
        async Task<List<CoverCandidate>> SearchMusicBrainzAsync(string album, string artist)
        {
            string query = "releasegroup:\"" + EscapeLucene(album) + "\"";
            if (string.IsNullOrWhiteSpace(artist) == false)
            {
                query += " AND artist:\"" + EscapeLucene(artist.Trim()) + "\"";
            }
            string json = await GetJsonAsync(MusicBrainzApiUrl + Uri.EscapeDataString(query), "MusicBrainz");
            List<(string Id, CoverCandidate Item)> found = new List<(string, CoverCandidate)>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach (JsonElement m in doc.RootElement.GetProperty("release-groups").EnumerateArray())
                    {
                        string id = GetString(m, "id");
                        string name = GetString(m, "title");
                        if (id == null || name == null || Guid.TryParse(id, out _) == false)
                        {
                            continue;
                        }
                        StringBuilder artistName = new StringBuilder();
                        if (m.TryGetProperty("artist-credit", out JsonElement credits) && credits.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement c in credits.EnumerateArray())
                            {
                                artistName.Append(GetString(c, "name")).Append(GetString(c, "joinphrase"));
                            }
                        }
                        string date = GetString(m, "first-release-date");
                        int year;
                        int? y = date != null && date.Length >= 4 && int.TryParse(date.Substring(0, 4), out year) ? year : (int?)null;
                        string type = GetString(m, "primary-type");
                        found.Add((id, new CoverCandidate
                        {
                            Title = name,
                            SubTitle = artistName.Length == 0 ? null : artistName.ToString(),
                            Year = y,
                            Format = type == null ? null : type.ToUpperInvariant(),
                            Source = "MusicBrainz",
                            Names = new List<string> { name },
                            Thumb = CoverArtArchiveUrl + id + "/front-250",
                            Image = CoverArtArchiveUrl + id + "/front-1200"
                        }));
                    }
                }
            }
            catch (Exception)
            {
                throw new InvalidOperationException("MusicBrainz 回傳的資料看不懂");
            }

            // 有沒有封面要問 Cover Art Archive（有封面會回轉址 3xx，沒有是 404）
            List<(string Id, CoverCandidate Item)> top = found.Take(MusicBrainzProbeMax).ToList();
            bool[] hasArt = await Task.WhenAll(top.Select(t => HasCoverArtAsync(t.Item.Thumb)));
            return top.Where((t, i) => hasArt[i]).Select(t => t.Item).ToList();
        }

        async Task<bool> HasCoverArtAsync(string url)
        {
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, url))
                using (HttpResponseMessage res = await http.SendAsync(req))
                {
                    int code = (int)res.StatusCode;
                    return code >= 300 && code < 400;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Lucene 查詢字串裡，雙引號與反斜線要跳脫
        static string EscapeLucene(string text)
        {
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
                && (uri.Host.EndsWith(".anilist.co", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".bgm.tv", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".kitsu.app", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".kitsu.io", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("coverartarchive.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("archive.org", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));
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
                // 自己跟著轉址（Cover Art Archive 會轉到 archive.org），每一跳都要通過白名單
                string current = url;
                HttpResponseMessage first = null;
                for (int hop = 0; ; hop++)
                {
                    first = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
                    int code = (int)first.StatusCode;
                    if (code < 300 || code >= 400)
                    {
                        break;
                    }
                    Uri next = first.Headers.Location == null ? null : new Uri(new Uri(current), first.Headers.Location);
                    first.Dispose();
                    if (hop >= 5 || next == null || IsAllowedImageUrl(next.AbsoluteUri) == false)
                    {
                        throw new InvalidOperationException("不支援的圖片來源");
                    }
                    current = next.AbsoluteUri;
                }
                using (HttpResponseMessage res = first)
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
