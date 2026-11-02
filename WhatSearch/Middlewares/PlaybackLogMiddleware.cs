using Microsoft.AspNetCore.Http;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WhatSearch.Middlewares
{
    /// <summary>
    /// 記錄 /get/ 的影音檔案請求，用來診斷手機播放一直轉圈圈：
    /// 來源 IP、裝置（iPhone/Android/…）、Range、狀態碼、預定傳送長度、耗時、是否被客戶端中止。
    /// </summary>
    public class PlaybackLogMiddleware
    {
        public static readonly ILogger PlaybackLogger = LogManager.GetLogger("Playback");

        static readonly string[] SkipQueryKeys = { "cover", "subtitle", "subtitleTracks", "preview" };

        static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav"
        };

        static readonly Regex IosVersionRegex = new Regex(@"OS (\d+)[_.](\d+)", RegexOptions.Compiled);

        private readonly RequestDelegate _next;

        public PlaybackLogMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            if (IsMediaRequest(context.Request) == false)
            {
                await _next(context);
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            string error = null;
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name;
                throw;
            }
            finally
            {
                sw.Stop();
                try
                {
                    Log(context, sw.ElapsedMilliseconds, error);
                }
                catch
                {
                }
            }
        }

        public static bool IsMediaRequest(HttpRequest request)
        {
            if (HttpMethods.IsGet(request.Method) == false && HttpMethods.IsHead(request.Method) == false)
            {
                return false;
            }
            if (request.Path.StartsWithSegments("/get") == false)
            {
                return false;
            }
            if (MediaExtensions.Contains(Path.GetExtension(request.Path.Value ?? string.Empty)) == false)
            {
                return false;
            }
            foreach (string key in SkipQueryKeys)
            {
                if (request.Query.ContainsKey(key))
                {
                    return false;
                }
            }
            return true;
        }

        static void Log(HttpContext context, long elapsedMs, string error)
        {
            HttpRequest req = context.Request;
            HttpResponse res = context.Response;
            string ip = context.Connection.RemoteIpAddress == null
                ? "?" : (context.Connection.RemoteIpAddress.IsIPv4MappedToIPv6
                    ? context.Connection.RemoteIpAddress.MapToIPv4().ToString()
                    : context.Connection.RemoteIpAddress.ToString());
            string forwarded = req.Headers["X-Forwarded-For"];
            string ua = req.Headers["User-Agent"];
            bool aborted = context.RequestAborted.IsCancellationRequested ||
                (error != null && (error.Contains("Cancel") || error.Contains("IO") || error.Contains("BadHttpRequest")));
            string range = req.Headers["Range"];
            string contentRange = res.Headers["Content-Range"];
            long? length = res.ContentLength;

            string rate = string.Empty;
            double? mbps = null;
            if (aborted == false && error == null && length.HasValue && length.Value >= 65536 && elapsedMs > 0)
            {
                mbps = length.Value * 8d / 1000d / elapsedMs;
                rate = string.Format(" rate={0:0.0}Mbps", mbps.Value);
            }

            // 一般請求只寫進檔案（Debug）；異常才提升到 Info/Warn，讓主控台只顯示值得注意的
            int status = res.StatusCode;
            LogLevel level = LogLevel.Debug;
            if (aborted == false && (error != null || status >= 400 || (status >= 300 && status < 400)))
            {
                level = LogLevel.Warn;
            }
            else if (mbps.HasValue && mbps.Value < 3 && length.Value >= 1024 * 1024)
            {
                level = LogLevel.Info;
            }

            PlaybackLogger.Log(level,
                "play {0}{1} {2} {3} {4} range={5} -> {6} {7} len={8} time={9}ms {10}{11}{12}",
                ip,
                string.IsNullOrEmpty(forwarded) ? string.Empty : " (xff " + forwarded + ")",
                DescribeDevice(ua),
                req.Method,
                Uri.UnescapeDataString(req.Path.Value ?? string.Empty) + (IsMp3Request(req) ? " [mp3]" : string.Empty),
                string.IsNullOrEmpty(range) ? "-" : range.ToString(),
                status,
                string.IsNullOrEmpty(contentRange) ? "-" : contentRange.ToString(),
                length.HasValue ? length.Value.ToString() : "-",
                elapsedMs,
                aborted ? "ABORTED" : (error != null ? "ERROR:" + error : "ok"),
                rate,
                string.IsNullOrEmpty(ua) ? string.Empty : " ua=\"" + Shorten(ua, 90) + "\"");
        }

        // FLAC 轉好的 MP3 快取版（?mp3=true）：路徑還是 .flac，日誌要標出來才分得出播的是哪一種
        static bool IsMp3Request(HttpRequest req)
        {
            string v = req.Query["mp3"];
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        public static string DescribeDevice(string ua)
        {
            if (string.IsNullOrEmpty(ua))
            {
                return "unknown";
            }
            string os = ua.Contains("iPhone") ? "iPhone"
                : ua.Contains("iPad") ? "iPad"
                : ua.Contains("Android") ? "Android"
                : ua.Contains("Windows") ? "Windows"
                : ua.Contains("Macintosh") ? "Mac"
                : ua.Contains("Linux") ? "Linux" : "other";
            if (os == "iPhone" || os == "iPad")
            {
                Match m = IosVersionRegex.Match(ua);
                if (m.Success)
                {
                    os += " iOS" + m.Groups[1].Value + "." + m.Groups[2].Value;
                }
            }
            string browser = ua.Contains("Edg/") ? "Edge"
                : ua.Contains("CriOS") || ua.Contains("Chrome") ? "Chrome"
                : ua.Contains("FxiOS") || ua.Contains("Firefox") ? "Firefox"
                : ua.Contains("Safari") ? "Safari" : "other";
            return os + "/" + browser;
        }

        public static string Shorten(string text, int max)
        {
            if (text == null)
            {
                return string.Empty;
            }
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
