using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using WhatSearch.Core;
using WhatSearch.Services;

namespace WhatSearch.Middlewares
{
    /// <summary>
    /// 給後台「播放監控」用：把 /get/ 影音檔案的回應包一層計數，記錄誰、從哪個裝置、在播哪個檔案、實際送出多少位元組。
    /// 只旁路記錄，不改回應內容；第一次寫出內容（而且狀態碼是 200／206）才算一次播放，所以 401、404、轉址不會被算進去。
    /// </summary>
    public class PlaybackMonitorMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IPlaybackMonitorService monitor = Ioc.Get<IPlaybackMonitorService>();

        public PlaybackMonitorMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            HttpRequest request = context.Request;
            if (HttpMethods.IsGet(request.Method) == false || PlaybackLogMiddleware.IsMediaRequest(request) == false)
            {
                await _next(context);
                return;
            }

            Stream original = context.Response.Body;
            IPlaybackStream playback = null;
            bool skip = false;
            context.Response.Body = new CountingStream(original, count =>
            {
                if (skip)
                {
                    return;
                }
                try
                {
                    if (playback == null)
                    {
                        int status = context.Response.StatusCode;
                        if (status != StatusCodes.Status200OK && status != StatusCodes.Status206PartialContent)
                        {
                            skip = true;
                            return;
                        }
                        playback = monitor.Begin(Describe(context), request.Path.Value, IsMp3Request(request));
                    }
                    playback.Add(count);
                }
                catch
                {
                    // 監控出問題不能影響播放
                    skip = true;
                }
            });
            try
            {
                await _next(context);
            }
            finally
            {
                context.Response.Body = original;
                try
                {
                    if (playback != null)
                    {
                        playback.End();
                    }
                }
                catch
                {
                }
            }
        }

        static bool IsMp3Request(HttpRequest request)
        {
            return string.Equals(request.Query["mp3"], "true", StringComparison.OrdinalIgnoreCase);
        }

        static PlaybackWho Describe(HttpContext context)
        {
            ClaimsIdentity identity = context.User == null ? null : context.User.Identity as ClaimsIdentity;
            string name = identity == null ? null : identity.Name;
            string display = identity == null ? null : identity.Label;

            string ip = null;
            string forwarded = context.Request.Headers["X-Forwarded-For"];
            if (string.IsNullOrEmpty(forwarded) == false)
            {
                ip = forwarded.Split(',')[0].Trim();
            }
            if (string.IsNullOrEmpty(ip))
            {
                IPAddress remote = context.Connection.RemoteIpAddress;
                ip = remote == null ? "?" : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString());
            }
            return new PlaybackWho
            {
                User = name,
                Display = display,
                Ip = ip,
                Device = PlaybackLogMiddleware.DescribeDevice(context.Request.Headers["User-Agent"])
            };
        }

        /// <summary>只計數、不改內容的寫入包裝；寫進底層成功之後才通知</summary>
        sealed class CountingStream : Stream
        {
            readonly Stream inner;
            readonly Action<int> onWrite;

            public CountingStream(Stream inner, Action<int> onWrite)
            {
                this.inner = inner;
                this.onWrite = onWrite;
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
                inner.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return inner.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                inner.Write(buffer, offset, count);
                onWrite(count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                inner.Write(buffer);
                onWrite(buffer.Length);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await inner.WriteAsync(buffer, offset, count, cancellationToken);
                onWrite(count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await inner.WriteAsync(buffer, cancellationToken);
                onWrite(buffer.Length);
            }
        }
    }
}
