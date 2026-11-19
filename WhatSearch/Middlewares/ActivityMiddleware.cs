using Microsoft.AspNetCore.Http;
using System;
using System.Threading.Tasks;
using WhatSearch.Core;
using WhatSearch.Services;

namespace WhatSearch.Middlewares
{
    /// <summary>
    /// 記錄已登入會員的最後來訪（開頁面、瀏覽資料夾、搜尋、播放）與最後播放的檔案。
    /// 只記「有意義」的請求：背景輪詢（api/info、新上架、轉檔進度…）不算，否則開著分頁就會一直刷新「最後來訪」。
    /// 要放在 UserAuthenticationMiddleware 之後（需要登入身分）。每人只留最後一筆，不留歷程。
    /// </summary>
    public class ActivityMiddleware
    {
        private readonly RequestDelegate _next;
        IActivityService activity = Ioc.Get<IActivityService>();

        public ActivityMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                Record(context);
            }
            catch (Exception)
            {
                // 記錄失敗不能影響正常請求
            }
            await _next(context);
        }

        void Record(HttpContext context)
        {
            if (context.User?.Identity == null || context.User.Identity.IsAuthenticated == false)
            {
                return;
            }
            string member = context.User.Identity.Name;
            HttpRequest req = context.Request;
            PathString path = req.Path;
            bool isMedia = PlaybackLogMiddleware.IsMediaRequest(req);
            bool isPageOpen = HttpMethods.IsGet(req.Method) && path.StartsWithSegments("/page");
            bool isBrowse = HttpMethods.IsPost(req.Method) &&
                (path.Equals("/api/folder", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/search", StringComparison.OrdinalIgnoreCase));
            bool isPlayReport = HttpMethods.IsPost(req.Method) && path.Equals("/api/plays", StringComparison.OrdinalIgnoreCase);
            if (isMedia == false && isPageOpen == false && isBrowse == false && isPlayReport == false)
            {
                return;
            }
            activity.Seen(member, PlaybackLogMiddleware.DescribeDevice(req.Headers["User-Agent"]));
            if (isMedia)
            {
                // /get/分享名/資料夾/檔名 → /分享名/資料夾/檔名
                string rel = Uri.UnescapeDataString(path.Value ?? string.Empty);
                if (rel.StartsWith("/get/", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetPlayed(member, rel.Substring(4));
                }
            }
        }
    }
}
