using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using WhatSearch.Core;
using WhatSearch.Services;

namespace WhatSearch.Middlewares
{
    /// <summary>
    /// 偵測亂試網址的請求：只看「回應 404 而且沒有對應到任何路由」的請求（網路上的掃描器常試 /wp-login.php、/.env）。
    /// 程式自己回的 404（例如資料夾沒有封面）有對應路由，不算。只旁路記錄，任何錯誤都不影響請求本身。
    /// </summary>
    public class AbnormalRequestMiddleware
    {
        // 瀏覽器與爬蟲會自己去要這些，沒有也不算異常
        static readonly string[] IgnorePrefixes = { "/favicon.ico", "/robots.txt", "/apple-touch-icon", "/sitemap.xml" };

        private readonly RequestDelegate _next;
        private readonly IAbnormalRequestService service = Ioc.Get<IAbnormalRequestService>();

        public AbnormalRequestMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            await _next(context);
            try
            {
                if (context.Response.StatusCode != StatusCodes.Status404NotFound || context.GetEndpoint() != null)
                {
                    return;
                }
                string path = context.Request.Path.Value ?? string.Empty;
                foreach (string prefix in IgnorePrefixes)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                AbnormalRequestConfig cfg = Ioc.GetConfig().Abnormal;
                string ip = ClientIpResolver.Resolve(context, cfg == null ? null : cfg.TrustedProxies);
                service.Record(ip, path, context.Request.Headers["User-Agent"].ToString());
            }
            catch (Exception)
            {
                // 監控失敗不能拖累請求
            }
        }
    }
}
