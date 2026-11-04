using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;

namespace WhatSearch.WebAPIs.Filters
{
    public class UserAuthorizeAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.HttpContext.User.Identity.IsAuthenticated == false)
            {
                var returnUrl = context.HttpContext.Request.Path.ToString();
                // 瀏覽與搜尋公開；打開任何檔案（影音、圖片、文字、字幕）都要登入，不再由分享資料夾各自決定
                if (returnUrl.StartsWith("/get/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Result = new RedirectResult(
                        "/page/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
                }
                return;
            }
            base.OnActionExecuting(context);
        }
    }
}
