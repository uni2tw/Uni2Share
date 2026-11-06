using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WhatSearch.Models
{
    /// <summary>評分記錄裡「找不到對應資料夾」的一筆（資料夾可能被改名、刪除、搬離了分享根）</summary>
    public class RatingOrphan
    {
        /// <summary>ratings.json 裡的 key（專輯有 album| 前綴）</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; }
        /// <summary>anime（動畫）／album（專輯）</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }
        /// <summary>去掉前綴後的名稱</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
        /// <summary>0 代表使用者明確設為「未評分」</summary>
        [JsonPropertyName("rating")]
        public double Rating { get; set; }
    }

    public class RatingScanResult
    {
        /// <summary>ratings.json 的總筆數</summary>
        [JsonPropertyName("total")]
        public int Total { get; set; }
        [JsonPropertyName("orphans")]
        public List<RatingOrphan> Orphans { get; set; } = new List<RatingOrphan>();
        /// <summary>有沒有檢查過動畫／專輯的評分（沒啟用，或分享根讀不到時就不檢查，避免把全部都當成髒資料）</summary>
        [JsonPropertyName("animeChecked")]
        public bool AnimeChecked { get; set; }
        [JsonPropertyName("albumChecked")]
        public bool AlbumChecked { get; set; }
        /// <summary>無法檢查的原因</summary>
        [JsonPropertyName("errors")]
        public List<string> Errors { get; set; } = new List<string>();
    }
}
