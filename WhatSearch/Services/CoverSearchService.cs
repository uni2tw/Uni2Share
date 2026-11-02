using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

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
        /// 同時到 AniList 與 Bangumi 搜尋動畫，兩邊的候選封面交錯排列（各最多 10 筆）。
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

        const string Query = @"query ($keyword: String!) {
  Page(page: 1, perPage: 10) {
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
            Task<List<CoverCandidate>> aniList = SearchAniListAsync(keyword);
            Task<List<CoverCandidate>> bangumi = SearchBangumiAsync(keyword);
            try { await Task.WhenAll(aniList, bangumi); } catch (Exception) { /* 個別結果在下面處理 */ }

            CoverSearchResult result = new CoverSearchResult();
            List<CoverCandidate> a = Collect("AniList", aniList, result);
            List<CoverCandidate> b = Collect("Bangumi", bangumi, result);
            if (a == null && b == null)
            {
                throw new InvalidOperationException(string.Join("；", result.Warnings));
            }
            a = a ?? new List<CoverCandidate>();
            b = b ?? new List<CoverCandidate>();
            // 交錯排列，兩邊排名前面的都看得到
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
            {
                if (i < a.Count) { result.Items.Add(a[i]); }
                if (i < b.Count) { result.Items.Add(b[i]); }
            }
            return result;
        }

        static List<CoverCandidate> Collect(string name, Task<List<CoverCandidate>> task, CoverSearchResult result)
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                Exception ex = task.Exception == null ? null : task.Exception.InnerException;
                result.Warnings.Add(name + "：" + (ex is InvalidOperationException ? ex.Message : "查詢失敗"));
                return null;
            }
            return task.Result;
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

        async Task<List<CoverCandidate>> SearchAniListAsync(string keyword)
        {
            string body = JsonSerializer.Serialize(new { query = Query, variables = new { keyword = keyword } });
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
