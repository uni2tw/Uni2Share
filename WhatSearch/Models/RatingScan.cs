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
        /// <summary>誰評了多少，例如「admin 4 星、共用（舊） 3 星」</summary>
        [JsonPropertyName("detail")]
        public string Detail { get; set; }
    }

    public class RatingUserCount
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("count")]
        public int Count { get; set; }
    }

    /// <summary>評分的分布：舊的共用評分有幾筆，以及每位使用者各有幾筆</summary>
    public class RatingStats
    {
        [JsonPropertyName("legacy")]
        public int Legacy { get; set; }
        [JsonPropertyName("users")]
        public List<RatingUserCount> Users { get; set; } = new List<RatingUserCount>();
    }

    /// <summary>一位使用者的評分統計（0 = 明確設為「未評分」不算）</summary>
    public class RatingUserStat
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        /// <summary>有給分（大於 0）的筆數</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }
        [JsonPropertyName("anime")]
        public int Anime { get; set; }
        [JsonPropertyName("album")]
        public int Album { get; set; }
        /// <summary>平均給分；沒有評分時為 0</summary>
        [JsonPropertyName("avg")]
        public double Avg { get; set; }
        /// <summary>星等分布：6 格，依序是 2.5、3、3.5、4、4.5、5 星各幾筆（評分只收 2.5～5；更低的舊資料算進 2.5 那格）</summary>
        [JsonPropertyName("dist")]
        public int[] Dist { get; set; } = new int[6];
    }

    /// <summary>被兩位以上使用者評過的作品</summary>
    public class RatingTitleStat
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        /// <summary>anime（動畫）／album（專輯）</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; }
        /// <summary>幾位使用者評過</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }
        [JsonPropertyName("avg")]
        public double Avg { get; set; }
        /// <summary>最高分與最低分的差</summary>
        [JsonPropertyName("spread")]
        public double Spread { get; set; }
        /// <summary>誰評了多少，例如「admin 5 星、bob 2 星」</summary>
        [JsonPropertyName("detail")]
        public string Detail { get; set; }
    }

    /// <summary>評分管理頁的統計：每位使用者的排名資料、最多人評過、分歧最大的作品（只算每人自己的評分，舊的共用評分不算）</summary>
    public class RatingInsights
    {
        [JsonPropertyName("users")]
        public List<RatingUserStat> Users { get; set; } = new List<RatingUserStat>();
        [JsonPropertyName("mostRated")]
        public List<RatingTitleStat> MostRated { get; set; } = new List<RatingTitleStat>();
        [JsonPropertyName("divergent")]
        public List<RatingTitleStat> Divergent { get; set; } = new List<RatingTitleStat>();
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
