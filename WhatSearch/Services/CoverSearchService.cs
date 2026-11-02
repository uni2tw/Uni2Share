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
        /// <summary>縮圖（給挑選畫面用）</summary>
        public string Thumb { get; set; }
        /// <summary>大圖（選定後由伺服器下載）</summary>
        public string Image { get; set; }
    }

    public interface ICoverSearchService
    {
        /// <summary>用名稱到 AniList 搜尋動畫，回傳候選封面（最多 10 筆）；連不上或查詢失敗會丟出 InvalidOperationException（訊息可直接顯示）</summary>
        Task<List<CoverCandidate>> SearchAsync(string keyword);

        /// <summary>這個網址是不是可以由伺服器下載的封面來源（只收 AniList 圖片網域的 https）</summary>
        bool IsAllowedImageUrl(string url);

        /// <summary>下載候選封面；只收 JPEG、最大 8 MB，不符合會丟出 InvalidOperationException（訊息可直接顯示）</summary>
        Task<byte[]> DownloadJpegAsync(string url);
    }

    public class CoverSearchService : ICoverSearchService
    {
        const string ApiUrl = "https://graphql.anilist.co";
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
        static readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public async Task<List<CoverCandidate>> SearchAsync(string keyword)
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
                && uri.Host.EndsWith(".anilist.co", StringComparison.OrdinalIgnoreCase);
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
