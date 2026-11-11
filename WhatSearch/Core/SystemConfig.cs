using WhatSearch.Utility;
using WhatSearch.Utilities;
using System.Collections.Generic;
using System.IO;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;

namespace WhatSearch.Core
{
    public class SystemConfig
    {
        [JsonPropertyName("watch")]
        public bool EnableWatch { get; set; }

        public LoginConfig Login { get; set; }

        [JsonPropertyName("folders")]
        public List<FolderConfig> Folders { get; set; }

        [JsonPropertyName("playtypes")]
        public HashSet<string> PlayTypes { get; set; }

        /// <summary>
        /// 改由 HostEnviroment 取得
        /// </summary>
        [JsonIgnore]
        public string ContentRootPath { get; set; }

        [JsonPropertyName("maxSearchResult")]
        public int MaxSearchResult { get; set; }

        [JsonPropertyName("upload")]
        public UploadConfig Upload { get; set; }

        /// <summary>站內「新上架」通知的設定；沒設定 shares 就不啟用</summary>
        [JsonPropertyName("news")]
        public NewsConfig News { get; set; } = new NewsConfig();

        /// <summary>動畫星等評分的設定；沒設定 shares 就不啟用</summary>
        [JsonPropertyName("rating")]
        public RatingConfig Rating { get; set; } = new RatingConfig();

        /// <summary>音量平衡（我的最愛單曲）的設定；沒寫就用預設值</summary>
        [JsonPropertyName("loudness")]
        public LoudnessConfig Loudness { get; set; } = new LoudnessConfig();
        [JsonPropertyName("transcode")]
        public TranscodeConfig Transcode { get; set; } = new TranscodeConfig();

        /// <summary>
        /// curl 執行檔的完整路徑（要是自帶 OpenSSL 的版本，例如 C:\tools\curl\curl.exe）。
        /// 舊版 Windows（如 Windows 7）的系統 TLS 跟不上現在的網站，連封面來源會握手失敗；設定後遇到這種失敗就改叫 curl 連。沒設定或檔案不存在就不使用
        /// </summary>
        [JsonPropertyName("curlPath")]
        public string CurlPath { get; set; }

        /// <summary>異常請求偵測（有人亂試網址）；全部都有預設值，不寫也會啟用</summary>
        [JsonPropertyName("abnormal")]
        public AbnormalRequestConfig Abnormal { get; set; } = new AbnormalRequestConfig();

        /// <summary>播放監控的統計存檔設定（重新啟動後承接）；全部都有預設值，不寫也會啟用</summary>
        [JsonPropertyName("monitor")]
        public MonitorConfig Monitor { get; set; } = new MonitorConfig();

        //public HashSet

        public static SystemConfig Reload()
        {
            string configPath = Helper.GetRelativePath("config.json");
            string json = File.ReadAllText(configPath);
            Console.WriteLine("config " + configPath + " ok");
            // 各機器自己的覆蓋設定（不進版控）：有就疊加在 config.json 上，格式錯誤時忽略並提示，不讓站台起不來
            string localPath = Helper.GetRelativePath("config.local.json");
            if (File.Exists(localPath))
            {
                try
                {
                    json = ConfigMerger.Merge(json, File.ReadAllText(localPath));
                    Console.WriteLine("config " + localPath + " merged");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("config.local.json 解析失敗，已忽略：" + ex.Message);
                }
            }
            SystemConfig result;
            try
            {
                result = JsonHelper.Deserialize<SystemConfig>(json, caseInsensitive: true);
            } catch (Exception ex)
            {
                result = new SystemConfig();
            }
            // JSON 反序列化出來的 HashSet 預設區分大小寫，要重建成不分大小寫，.MP3 才比對得到 .mp3
            result.PlayTypes = new HashSet<string>(
                result.PlayTypes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (result.MaxSearchResult == 0)
            {
                result.MaxSearchResult = 100;
            }
            if (result.Loudness == null)
            {
                result.Loudness = new LoudnessConfig();
            }
            if (result.Rating == null)
            {
                result.Rating = new RatingConfig();
            }
            if (result.Transcode == null)
            {
                result.Transcode = new TranscodeConfig();
            }
            if (result.Abnormal == null)
            {
                result.Abnormal = new AbnormalRequestConfig();
            }
            if (result.Monitor == null)
            {
                result.Monitor = new MonitorConfig();
            }
            if (result.Abnormal.IgnoreIps == null)
            {
                result.Abnormal.IgnoreIps = new List<string>();
            }
            if (result.Abnormal.TrustedProxies == null)
            {
                result.Abnormal.TrustedProxies = new List<string>();
            }
            if (result.Rating.Albums == null)
            {
                result.Rating.Albums = new RatingAlbumConfig();
            }
            if (result.Rating.Albums.Shares == null)
            {
                result.Rating.Albums.Shares = new List<string>();
            }
            if (result.News == null)
            {
                result.News = new NewsConfig();
            }
            if (result.News.Shares == null)
            {
                result.News.Shares = new List<NewsShareConfig>();
            }

            return result;
        }



    }

    public class FolderConfig
    {
        [JsonPropertyName("path")]
        public string Path { get; set; }
        [JsonPropertyName("title")]
        public string Title { get; set; }

        public override string ToString()
        {
            return this.Path;
        }
    }

    public class LoginConfig
    {
        public int CookieDays { get; set; }
    }

    public class NewsConfig
    {
        /// <summary>要通知的分享根與各自的算法；空代表不啟用</summary>
        [JsonPropertyName("shares")]
        public List<NewsShareConfig> Shares { get; set; } = new List<NewsShareConfig>();
        /// <summary>檔案（或專輯）大小要連續多久不變才算上架，避免複製、下載到一半就通知（分鐘）。有檔案監看時這個值決定通知有多即時</summary>
        [JsonPropertyName("stableMinutes")]
        public double StableMinutes { get; set; } = 2;
        /// <summary>是否用檔案監看（FileSystemWatcher）即時偵測新增與改名；關閉就只靠定期的完整掃描（這時建議把 scanMinutes 調短）。獨立於搜尋索引的 watch 設定</summary>
        [JsonPropertyName("watch")]
        public bool Watch { get; set; } = true;
        /// <summary>檔案監看的緩衝區大小（KB）。預設的 8KB 在一次複製很多檔案時會溢位漏事件；溢位會觸發完整掃描補回</summary>
        [JsonPropertyName("watchBufferKB")]
        public int WatchBufferKB { get; set; } = 64;
        /// <summary>「完整掃描」的預設間隔（分鐘）；它是檔案監看漏事件時的保險，所以不用太勤，預設 6 小時。分享根可以用自己的 scanMinutes 覆蓋</summary>
        [JsonPropertyName("scanMinutes")]
        public double ScanMinutes { get; set; } = 360;
        /// <summary>定期的完整掃描只在這個時段進行（本機時間，HH:mm）；兩個都留空表示不限時段。預設 08:00–22:00。可以跨午夜（例如 22:00–06:00）。不影響檔案監看的即時偵測</summary>
        [JsonPropertyName("activeFrom")]
        public string ActiveFrom { get; set; } = "08:00";
        [JsonPropertyName("activeTo")]
        public string ActiveTo { get; set; } = "22:00";
        /// <summary>每隔幾小時，定期掃描那一輪改成「完整掃描」（不看資料夾修改時間），當作修改時間不可靠時的保險</summary>
        [JsonPropertyName("fullScanHours")]
        public double FullScanHours { get; set; } = 24;
        /// <summary>資料夾列表裡「新」標記（新作品／新增 N 集／新專輯／新集數）顯示幾天；從上架通知產生的時間算起</summary>
        [JsonPropertyName("badgeDays")]
        public double BadgeDays { get; set; } = 3;
    }

    public class NewsShareConfig
    {
        /// <summary>分享根的名稱（config 裡 folders 的 title）</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }
        /// <summary>episodes：動畫（新作品，或作品資料夾新增影片檔）；albums：音樂（只有新專輯資料夾出現才算）</summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "episodes";
        /// <summary>episodes 用：作品資料夾位在分享根底下第幾層（Anime\2026-4\作品 為 2）</summary>
        [JsonPropertyName("depth")]
        public int Depth { get; set; } = 2;
        /// <summary>albums 用：往下找專輯資料夾最多找幾層（歌手\專輯 為 2）</summary>
        [JsonPropertyName("maxDepth")]
        public int MaxDepth { get; set; } = 4;
        /// <summary>這個分享根自己的掃描間隔（分鐘）；沒寫就用 news.scanMinutes</summary>
        [JsonPropertyName("scanMinutes")]
        public double? ScanMinutes { get; set; }
    }

    public class RatingConfig
    {
        /// <summary>啟用評分的分享根名稱（config 裡 folders 的 title），例如 ["Anime"]；空代表不啟用</summary>
        [JsonPropertyName("shares")]
        public List<string> Shares { get; set; } = new List<string>();
        /// <summary>動畫資料夾位在分享根底下第幾層：Anime6-4\作品 為 2，只有這一層的資料夾可評分</summary>
        [JsonPropertyName("depth")]
        public int Depth { get; set; } = 2;
        /// <summary>音樂專輯評分：在這些分享根底下，專輯頁（資料夾裡有 2 首以上音樂，或多片裝）可以評分；空代表不啟用</summary>
        [JsonPropertyName("albums")]
        public RatingAlbumConfig Albums { get; set; } = new RatingAlbumConfig();
    }

    public class RatingAlbumConfig
    {
        /// <summary>啟用專輯評分的分享根名稱（config 裡 folders 的 title），例如 ["Music"]</summary>
        [JsonPropertyName("shares")]
        public List<string> Shares { get; set; } = new List<string>();
    }

    /// <summary>FLAC 轉 MP3（需要伺服器上有 ffmpeg 執行檔）；ffmpeg 沒設定或找不到時整個功能停用</summary>
    public class TranscodeConfig
    {
        /// <summary>ffmpeg 執行檔的完整路徑，例如 C:\tools\ffmpeg.exe</summary>
        [JsonPropertyName("ffmpeg")]
        public string Ffmpeg { get; set; }
        /// <summary>輸出 MP3 的位元率（kbps）</summary>
        [JsonPropertyName("bitrate")]
        public int Bitrate { get; set; } = 192;
        /// <summary>轉好的 MP3 放哪裡；留空用執行目錄下的 data\transcode</summary>
        [JsonPropertyName("cacheFolder")]
        public string CacheFolder { get; set; }
        /// <summary>快取上限（GB），超過就從最久沒被播放的開始刪</summary>
        [JsonPropertyName("maxCacheGB")]
        public double MaxCacheGB { get; set; } = 20;
        /// <summary>同時轉幾個檔案</summary>
        [JsonPropertyName("concurrency")]
        public int Concurrency { get; set; } = 1;
        /// <summary>MKV 轉 H.264 的畫質（x264 的 CRF，越小畫質越好、檔案越大；18～23 常用）</summary>
        [JsonPropertyName("videoCrf")]
        public int VideoCrf { get; set; } = 20;
        /// <summary>MKV 轉 H.264 的速度預設（ultrafast … medium … slow；越慢檔案越小、越花時間）</summary>
        [JsonPropertyName("videoPreset")]
        public string VideoPreset { get; set; } = "medium";
        /// <summary>MKV 轉檔時音訊重新編成 AAC 的位元率（kbps；原本就是 AAC 的直接複製）</summary>
        [JsonPropertyName("audioBitrate")]
        public int AudioBitrate { get; set; } = 192;
        /// <summary>MKV 轉檔時 x264 最多用幾個執行緒（ffmpeg 的 -threads）；0 = 不限制（ffmpeg 自己決定，通常用滿所有核心）。想保留 CPU 給其它程式就設小一點，轉檔時間會跟著變長</summary>
        [JsonPropertyName("videoThreads")]
        public int VideoThreads { get; set; } = 0;
        /// <summary>會員點 CUE／FLAC 時即時拆軌的暫存資料夾；留空用執行目錄下的 data\cuecache</summary>
        [JsonPropertyName("cueCacheFolder")]
        public string CueCacheFolder { get; set; }
        /// <summary>拆軌暫存的容量上限（GB），超過就從最久沒被開過的專輯開始刪</summary>
        [JsonPropertyName("cueCacheGB")]
        public double CueCacheGB { get; set; } = 2;
        /// <summary>拆軌暫存幾天沒被開過就刪</summary>
        [JsonPropertyName("cueCacheDays")]
        public int CueCacheDays { get; set; } = 30;
    }

    /// <summary>
    /// 異常請求偵測：同一個 IP 在 windowSeconds 秒內有 threshold 次「沒有對應路由的 404」（例如亂試 /wp-login.php）就記一筆事件、通知管理者
    /// </summary>
    public class AbnormalRequestConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
        /// <summary>時間窗內累積幾次 404 就算異常</summary>
        [JsonPropertyName("threshold")]
        public int Threshold { get; set; } = 3;
        /// <summary>時間窗（秒）</summary>
        [JsonPropertyName("windowSeconds")]
        public int WindowSeconds { get; set; } = 60;
        /// <summary>同一個 IP 觸發後幾分鐘內不再新增事件（只累加次數），避免被掃描器刷滿</summary>
        [JsonPropertyName("cooldownMinutes")]
        public int CooldownMinutes { get; set; } = 10;
        /// <summary>最多保留幾筆事件，超過就刪最舊的</summary>
        [JsonPropertyName("maxEvents")]
        public int MaxEvents { get; set; } = 500;
        /// <summary>不監控的來源 IP（單一 IP 或 a.b.c.d/nn），例如自己的辦公室</summary>
        [JsonPropertyName("ignoreIps")]
        public List<string> IgnoreIps { get; set; } = new List<string>();
        /// <summary>
        /// 站台放在反向代理後面時，代理的 IP（單一 IP 或 a.b.c.d/nn）：只有從這些位址連進來的請求才採用 X-Forwarded-For 當來源 IP。
        /// 沒設定時信任本機與內網位址（10.x、172.16～31.x、192.168.x）
        /// </summary>
        [JsonPropertyName("trustedProxies")]
        public List<string> TrustedProxies { get; set; } = new List<string>();
    }

    /// <summary>
    /// 播放監控的統計（近 7 天流量、播放次數、熱門資料夾、裝置分布）是否存到 data/playback-stats.json，讓重新啟動後承接。
    /// 「現在誰在播」和即時速率不存。用強制結束行程（Stop-Process -Force）關站時不會觸發關閉補存，最多掉 flushSeconds 秒的資料
    /// </summary>
    public class MonitorConfig
    {
        /// <summary>false = 維持舊行為，只放記憶體、重新啟動歸零</summary>
        [JsonPropertyName("persist")]
        public bool Persist { get; set; } = true;
        /// <summary>多久把有變動的統計寫檔一次（秒）；小於 5 以 5 計</summary>
        [JsonPropertyName("flushSeconds")]
        public int FlushSeconds { get; set; } = 30;
    }

    public class LoudnessConfig
    {
        /// <summary>目標響度（LUFS）。數字越接近 0 整體越大聲；-14 與主流串流平台相同，-18 是 ReplayGain 的傳統基準</summary>
        [JsonPropertyName("target")]
        public double Target { get; set; } = -14.0;
        /// <summary>最多調高幾 dB（還會受歌曲峰值限制，避免爆音）</summary>
        [JsonPropertyName("maxBoostDb")]
        public double MaxBoostDb { get; set; } = 12.0;
        /// <summary>最多調低幾 dB</summary>
        [JsonPropertyName("maxCutDb")]
        public double MaxCutDb { get; set; } = 20.0;
    }

    public class UploadConfig
    {
        public bool Enabled { get; set; }
        public string Folder { get; set; }
        public long? LimitMb { get; set; }
    }

}
