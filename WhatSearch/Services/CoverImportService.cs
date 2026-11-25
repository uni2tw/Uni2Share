using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace WhatSearch.Services
{
    public interface ICoverImportService
    {
        /// <summary>
        /// 把使用者提供的圖片（上傳或貼上）轉成可存成 cover.jpg 的 JPEG：支援 JPEG、PNG、WebP、GIF、BMP；
        /// 太大的圖會縮小，帶透明的圖墊白底，照片的拍攝方向會轉正。不是圖片或太大會丟出 InvalidOperationException（訊息可直接顯示）
        /// </summary>
        Task<byte[]> ToJpegAsync(byte[] data);

        /// <summary>
        /// 從使用者貼上的網址下載圖片（原始位元組，還沒轉檔）。只收 http／https 預設埠；
        /// 不收 Google 的網域（不由伺服器去抓 Google 的資源）；不連內網位址；轉址最多 3 次且每一跳都重新檢查；最大 12 MB。
        /// 失敗會丟出 InvalidOperationException（訊息可直接顯示）
        /// </summary>
        Task<byte[]> DownloadAsync(string url);

        /// <summary>網址是不是 Google 的網域（含 gstatic、googleusercontent 等圖片網域）</summary>
        bool IsGoogleHost(string host);
    }

    public class CoverImportService : ICoverImportService
    {
        /// <summary>貼上網址下載的大小上限</summary>
        public const int MaxDownloadBytes = 12 * 1024 * 1024;
        // 圖片寬 x 高超過這個像素數就不處理，避免解壓縮炸彈把記憶體吃光
        const long MaxPixels = 36L * 1000 * 1000;
        // 封面頁上最大只顯示一百多像素；留大一點給之後放大檢視，輸出長邊最多這麼大
        const int MaxEdge = 1600;
        const int JpegQuality = 90;
        const int MaxRedirects = 3;
        const int TimeoutSeconds = 15;

        static readonly string[] GoogleLabels = { "google", "gstatic", "googleusercontent", "ggpht", "googleapis", "googlevideo", "googlesyndication" };

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            // 連線時才檢查對方 IP（不是只檢查一次 DNS），避免主機名稱一開始指到外網、連線時換成內網位址
            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
                ConnectCallback = async (context, token) =>
                {
                    IPAddress[] all = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                    IPAddress[] open = all.Where(IsPublicAddress).ToArray();
                    if (open.Length == 0)
                    {
                        throw new HttpRequestException("不能連到內部網路位址");
                    }
                    Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(open, context.DnsEndPoint.Port, token);
                        return new NetworkStream(socket, true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Uni2Share-CoverImport/1.0; +https://github.com/uni2tw/Uni2Share)");
            client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
            return client;
        }

        /// <summary>只有公開的網際網路位址才放行（擋掉本機、內網、連結本地、保留、多點傳播位址）</summary>
        public static bool IsPublicAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            if (IPAddress.IsLoopback(ip))
            {
                return false;
            }
            byte[] b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                if (b[0] == 0 || b[0] == 10 || b[0] >= 224)
                {
                    return false;
                }
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) { return false; }       // 100.64.0.0/10 共享位址
                if (b[0] == 169 && b[1] == 254) { return false; }                      // 連結本地（含雲端中繼資料 169.254.169.254）
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { return false; }
                if (b[0] == 192 && b[1] == 168) { return false; }
                if (b[0] == 192 && b[1] == 0 && b[2] == 0) { return false; }
                if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) { return false; }
                return true;
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any))
                {
                    return false;
                }
                if ((b[0] & 0xFE) == 0xFC) { return false; }                           // fc00::/7 唯一本地位址
                return true;
            }
            return false;
        }

        public bool IsGoogleHost(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }
            return host.Split('.').Any(label => GoogleLabels.Contains(label, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>網址通過基本檢查回傳 Uri，否則丟出可顯示的訊息</summary>
        Uri CheckUrl(string url)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) == false)
            {
                throw new InvalidOperationException("這不是有效的圖片網址");
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("只支援 http 或 https 開頭的圖片網址");
            }
            if (uri.IsDefaultPort == false || string.IsNullOrEmpty(uri.UserInfo) == false)
            {
                throw new InvalidOperationException("不支援這種圖片網址");
            }
            if (IsGoogleHost(uri.Host))
            {
                throw new InvalidOperationException("不能直接用 Google 的圖片網址。請在 Google 圖片點進去看到原網站，改用「複製圖片」貼上，或從原網站複製圖片網址");
            }
            return uri;
        }

        public async Task<byte[]> DownloadAsync(string url)
        {
            Uri uri = CheckUrl(url);
            try
            {
                for (int hop = 0; ; hop++)
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, uri))
                    using (HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        int status = (int)res.StatusCode;
                        if (status >= 300 && status < 400)
                        {
                            if (hop >= MaxRedirects || res.Headers.Location == null)
                            {
                                throw new InvalidOperationException("圖片網址轉址太多次");
                            }
                            Uri next = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(uri, res.Headers.Location);
                            uri = CheckUrl(next.AbsoluteUri);
                            continue;
                        }
                        if (res.IsSuccessStatusCode == false)
                        {
                            throw new InvalidOperationException("下載圖片失敗（" + status + "），網站可能不讓外部下載，請改用「複製圖片」貼上或選照片");
                        }
                        if (res.Content.Headers.ContentLength > MaxDownloadBytes)
                        {
                            throw new InvalidOperationException("圖片太大（上限 12 MB）");
                        }
                        using (Stream stream = await res.Content.ReadAsStreamAsync())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] buffer = new byte[81920];
                            int n;
                            while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                ms.Write(buffer, 0, n);
                                if (ms.Length > MaxDownloadBytes)
                                {
                                    throw new InvalidOperationException("圖片太大（上限 12 MB）");
                                }
                            }
                            return ms.ToArray();
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                throw new InvalidOperationException("下載圖片逾時，請稍後再試");
            }
            catch (Exception ex)
            {
                Exception inner = ex;
                while (inner.InnerException != null)
                {
                    inner = inner.InnerException;
                }
                throw new InvalidOperationException("下載圖片失敗（" + inner.Message + "）");
            }
        }

        public Task<byte[]> ToJpegAsync(byte[] data)
        {
            // 解碼與縮圖是 CPU 工作，不佔住要求執行緒
            return Task.Run(() => ToJpeg(data));
        }

        static byte[] ToJpeg(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidOperationException("沒有收到圖片");
            }
            SKEncodedImageFormat format;
            SKEncodedOrigin origin;
            int width, height;
            using (SKCodec codec = SKCodec.Create(new MemoryStream(data)))
            {
                if (codec == null)
                {
                    throw new InvalidOperationException("這不是圖片，或格式不支援（支援 JPEG、PNG、WebP、GIF、BMP）");
                }
                format = codec.EncodedFormat;
                origin = codec.EncodedOrigin;
                width = codec.Info.Width;
                height = codec.Info.Height;
            }
            if (format != SKEncodedImageFormat.Jpeg && format != SKEncodedImageFormat.Png && format != SKEncodedImageFormat.Webp
                && format != SKEncodedImageFormat.Gif && format != SKEncodedImageFormat.Bmp)
            {
                throw new InvalidOperationException("這種圖片格式不支援（支援 JPEG、PNG、WebP、GIF、BMP）");
            }
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                throw new InvalidOperationException("圖片的尺寸太大，無法處理");
            }
            // 已經是不超過上限、不需要轉向的 JPEG：原樣保留，不重新壓縮（畫質不變）
            if (format == SKEncodedImageFormat.Jpeg && origin == SKEncodedOrigin.TopLeft && Math.Max(width, height) <= MaxEdge)
            {
                return data;
            }
            using (SKBitmap source = SKBitmap.Decode(data))
            {
                if (source == null)
                {
                    throw new InvalidOperationException("圖片內容損壞，無法讀取");
                }
                bool swap = origin == SKEncodedOrigin.RightTop || origin == SKEncodedOrigin.LeftBottom
                    || origin == SKEncodedOrigin.LeftTop || origin == SKEncodedOrigin.RightBottom;
                int srcW = swap ? source.Height : source.Width;
                int srcH = swap ? source.Width : source.Height;
                double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(srcW, srcH));
                int outW = Math.Max(1, (int)Math.Round(srcW * scale));
                int outH = Math.Max(1, (int)Math.Round(srcH * scale));
                using (SKSurface surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul)))
                {
                    SKCanvas canvas = surface.Canvas;
                    canvas.Clear(SKColors.White);
                    canvas.Scale((float)scale);
                    ApplyOrigin(canvas, origin, source.Width, source.Height);
                    using (SKPaint paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                    {
                        canvas.DrawBitmap(source, 0, 0, paint);
                    }
                    using (SKImage image = surface.Snapshot())
                    using (SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality))
                    {
                        if (encoded == null)
                        {
                            throw new InvalidOperationException("圖片轉檔失敗");
                        }
                        return encoded.ToArray();
                    }
                }
            }
        }

        /// <summary>把照片 EXIF 的拍攝方向轉成畫布變換（w、h 是原圖尺寸）；畫上去之後影像就是正的</summary>
        static void ApplyOrigin(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
        {
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:      // 水平翻轉
                    canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
                case SKEncodedOrigin.BottomRight:   // 轉 180
                    canvas.Translate(w, h); canvas.RotateDegrees(180); break;
                case SKEncodedOrigin.BottomLeft:    // 垂直翻轉
                    canvas.Translate(0, h); canvas.Scale(1, -1); break;
                case SKEncodedOrigin.LeftTop:       // 轉置：(x, y) -> (y, x)
                {
                    SKMatrix m = new SKMatrix { ScaleX = 0, SkewX = 1, TransX = 0, SkewY = 1, ScaleY = 0, TransY = 0, Persp0 = 0, Persp1 = 0, Persp2 = 1 };
                    canvas.Concat(ref m);
                    break;
                }
                case SKEncodedOrigin.RightTop:      // 順時針轉 90
                    canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom:   // 反轉置：(x, y) -> (h - y, w - x)
                {
                    SKMatrix m = new SKMatrix { ScaleX = 0, SkewX = -1, TransX = h, SkewY = -1, ScaleY = 0, TransY = w, Persp0 = 0, Persp1 = 0, Persp2 = 1 };
                    canvas.Concat(ref m);
                    break;
                }
                case SKEncodedOrigin.LeftBottom:    // 逆時針轉 90
                    canvas.Translate(0, w); canvas.RotateDegrees(-90); break;
                default: break;
            }
        }
    }
}
