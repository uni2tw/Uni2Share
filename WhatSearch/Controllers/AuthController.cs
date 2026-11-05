using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using WhatSearch.Core;
using WhatSearch.Middlewares;
using WhatSearch.Services.Interfaces;

namespace WhatSearch.Controllers
{
    public class AuthController : Controller
    {
        static SystemConfig config = Ioc.GetConfig();

        [HttpPost]
        [Route("login")]
        public dynamic Login([FromBody] LoginModel model)
        {
            IUserService userSrv = Ioc.Get<IUserService>();
            string message;
            string clientIp = ClientIpResolver.Resolve(HttpContext, config.Abnormal?.TrustedProxies);
            bool success = userSrv.Login(Response, model?.name, model?.password, config.Login.CookieDays, clientIp, out message);
            return new
            {
                success,
                message,
                returnUrl = success ? GetSafeReturnUrl(model?.returnUrl, "/page") : null
            };
        }

        [HttpPost]
        [Route("register")]
        public dynamic Register([FromBody] RegisterModel model)
        {
            IUserService userSrv = Ioc.Get<IUserService>();
            string message;
            bool success = userSrv.Register(model?.name, model?.displayName, model?.password, out message);
            return new
            {
                success,
                message
            };
        }

        [HttpGet]
        [Route("logout")]
        public IActionResult Logout(string returnUrl)
        {
            Response.Cookies.Delete(UserAuthenticationMiddleware._AUTH_COOKIE_NAME);
            return Redirect(GetSafeReturnUrl(returnUrl, "/page/login"));
        }

        /// <summary>
        /// 只允許站內相對路徑，避免 returnUrl 被用來做開放重導攻擊
        /// </summary>
        private static string GetSafeReturnUrl(string returnUrl, string fallback)
        {
            if (string.IsNullOrEmpty(returnUrl))
            {
                return fallback;
            }
            // 開頭不能是 // 或 /\（瀏覽器會把反斜線當斜線，變成別的網站），也不能含反斜線與控制字元
            if (returnUrl.StartsWith("/") == false || returnUrl.StartsWith("//") || returnUrl.Contains('\\') ||
                returnUrl.Any(char.IsControl))
            {
                return fallback;
            }
            return returnUrl;
        }

        public class LoginModel
        {
            public string name { get; set; }
            public string password { get; set; }
            public string returnUrl { get; set; }
        }

        public class RegisterModel
        {
            public string name { get; set; }
            public string displayName { get; set; }
            public string password { get; set; }
        }
    }
}
