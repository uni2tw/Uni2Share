using System.Text.Json.Serialization;

namespace WhatSearch.Models
{
    public class FileInfoView
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
        [JsonPropertyName("get_url")]
        public string GetUrl { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; }
        [JsonPropertyName("type")]
        public string Type { get; set; }
        [JsonPropertyName("modify")]
        public string Modify { get; set; }
        [JsonPropertyName("size")]
        public string Size { get; set; }
        [JsonPropertyName("path")]
        public string Path { get; set; }
        [JsonPropertyName("artist")]
        public string Artist { get; set; }
        [JsonPropertyName("album")]
        public string Album { get; set; }
        /// <summary>音樂 ID3 標題（沒有標籤為 null）</summary>
        [JsonPropertyName("track_title")]
        public string TrackTitle { get; set; }
        /// <summary>音樂 ID3 音軌編號</summary>
        [JsonPropertyName("track")]
        public uint? Track { get; set; }
        /// <summary>音樂 ID3 年份</summary>
        [JsonPropertyName("year")]
        public uint? Year { get; set; }
        /// <summary>音樂 ID3 專輯演出者</summary>
        [JsonPropertyName("album_artist")]
        public string AlbumArtist { get; set; }
        /// <summary>檔案大小（位元組）；供前端估算影片位元率</summary>
        [JsonPropertyName("bytes")]
        public long? Bytes { get; set; }
        /// <summary>影片長度（秒）；非影片或讀不到為 null</summary>
        [JsonPropertyName("duration")]
        public double? Duration { get; set; }

        /// <summary>修改時間（UTC，自 1970-01-01 起的毫秒數），供前端依時間排序；畫面上顯示的 modify 是文字，無法正確排序</summary>
        [JsonPropertyName("modify_ts")]
        public long? ModifyTs { get; set; }
        /// <summary>是否為可評分的資料夾（動畫）；其他為 null</summary>
        [JsonPropertyName("rateable")]
        public bool? Rateable { get; set; }
        /// <summary>可評分資料夾去掉結尾星等後的名稱，用來顯示</summary>
        [JsonPropertyName("title_plain")]
        public string TitlePlain { get; set; }
        /// <summary>評分 0.5~5（0 表示明確設為未評分）；尚未評分為 null</summary>
        [JsonPropertyName("rating")]
        public double? Rating { get; set; }
        /// <summary>音樂專輯資料夾的評分（只在已評分時有值，列表上顯示星星；評分要進專輯頁才能改）</summary>
        [JsonPropertyName("album_rating")]
        public double? AlbumRating { get; set; }
        /// <summary>動畫資料夾被管理者標註為「無法播放」（只在有標註時為 true）</summary>
        [JsonPropertyName("unplayable")]
        public bool? Unplayable { get; set; }
        /// <summary>檔案格式不在可開啟的清單內（點了只會得到 404）；前端據此直接跳訊息，不開新分頁。只在不支援時為 true</summary>
        [JsonPropertyName("unsupported")]
        public bool? Unsupported { get; set; }
        /// <summary>音量平衡：這首歌量測出的響度（LUFS）；尚未分析或無法分析為 null</summary>
        [JsonPropertyName("loudness")]
        public double? Loudness { get; set; }
        /// <summary>音量平衡：要調整的 dB（負數為調小聲）；尚未分析或無法分析為 null</summary>
        [JsonPropertyName("gain_db")]
        public double? GainDb { get; set; }
        /// <summary>音量平衡的狀態：ready / pending / failed / unsupported；不是「我的最愛」單曲則為 null</summary>
        [JsonPropertyName("loudness_state")]
        public string LoudnessState { get; set; }
        /// <summary>FLAC 轉 MP3 的狀態：ready / pending / failed；不是 FLAC 或沒有啟用轉檔為 null</summary>
        [JsonPropertyName("transcode_state")]
        public string TranscodeState { get; set; }
        /// <summary>新上架標記（badgeDays 內）：資料夾為 series／episodes／album，檔案為 file；沒有就是 null</summary>
        [JsonPropertyName("news_kind")]
        public string NewsKind { get; set; }
        /// <summary>新上架標記的集數（資料夾 series／episodes 用）</summary>
        [JsonPropertyName("news_count")]
        public int? NewsCount { get; set; }

        public override string ToString()
        {
            return Title;
        }
    }

}
