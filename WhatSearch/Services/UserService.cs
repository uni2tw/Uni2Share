using Microsoft.AspNetCore.Http;
using System;
using System.Globalization;
using System.Security.Claims;
using WhatSearch.Core;
using WhatSearch.Services.Interfaces;
using WhatSearch.DataProviders.Interfaces;
using WhatSearch.Models;
using WhatSearch.Middlewares;
using System.Collections.Generic;
using NLog;
using WhatSearch.Utilities;

namespace WhatSearch.Services
{
    public class UserService : IUserService
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        IMemberProvider mp = Ioc.Get<IMemberProvider>();
        public void SetIdentityByToken(HttpContext context, string accessToken)
        {
            Member mem = mp.GetMemberByToken(accessToken);
            if (mem != null && mem.Status == MemberStatus.Active)
            {
                var claimIdentity = new ClaimsIdentity(new UserIdentity(mem.Name))
                {
                    Label = mem.DisplayName
                };
                claimIdentity.AddClaim(new Claim("Source", "Local"));
                if (mem.IsAdmin)
                {
                    claimIdentity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
                }
                context.User = new ClaimsPrincipal();
                context.User.AddIdentity(claimIdentity);
            }
        }

        public bool SaveMember(Member mem, out string accessToken)
        {
            accessToken = string.Empty;
            if (mem == null || string.IsNullOrEmpty(mem.Name))
            {
                return false;
            }

            Member oldMem = mp.GetMember(mem.Name);
            if (oldMem != null)
            {
                accessToken = oldMem.AccessToken;
                return false;
            }
            try
            {
                mem.CreateTime = DateTime.Now;
                mem.LastAccessTime = DateTime.Now;
                mem.AccessToken = Guid.NewGuid().ToString("N");
                accessToken = mem.AccessToken;
                mp.SaveMember(mem);
                return true;
            }
            catch (Exception ex)
            {
                logger.Error("SaveMember fail.", ex);
                return false;
            }
        }

        public void UpdateMember(string name)
        {
            mp.Update(name, m => m.LastAccessTime = DateTime.Now);
        }

        // 管理者改狀態不該動到對方的「上次登入」時間，所以只改狀態
        public void UpdateMemberStatus(string name, MemberStatus status)
        {
            mp.Update(name, m => m.Status = status);
        }

        public bool ResetPassword(string name, string newPassword)
        {
            string hashed = PasswordHasher.Hash(newPassword);
            return mp.Update(name, m =>
            {
                m.PasswordHash = hashed;
                m.PasswordFormat = PasswordFormat.Hashed;
            });
        }

        public const int DisplayNameMaxLength = 20;

        /// <summary>去掉前後空白後要有內容、最多 DisplayNameMaxLength 個字（emoji 之類算 1 個字），不能含控制字元、換行、不可見的格式字元</summary>
        public static bool TryNormalizeDisplayName(string input, out string result, out string message)
        {
            result = (input ?? string.Empty).Trim();
            message = string.Empty;
            if (result.Length == 0)
            {
                message = "顯示名稱不可空白";
                return false;
            }
            foreach (char c in result)
            {
                UnicodeCategory category = char.GetUnicodeCategory(c);
                bool invisible = category == UnicodeCategory.Control || category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator ||
                    (category == UnicodeCategory.Format && c != '\u200D');   // 零寬連接符號（ZWJ）是組合 emoji 用的，放行
                if (invisible)
                {
                    message = "顯示名稱含有不能使用的字元";
                    return false;
                }
            }
            if (new StringInfo(result).LengthInTextElements > DisplayNameMaxLength)
            {
                message = string.Format("顯示名稱最多 {0} 個字", DisplayNameMaxLength);
                return false;
            }
            return true;
        }

        public bool UpdateDisplayName(string name, string displayName, out string displayNameSaved, out string message)
        {
            displayNameSaved = null;
            string normalized;
            if (TryNormalizeDisplayName(displayName, out normalized, out message) == false)
            {
                return false;
            }
            if (mp.Update(name, m => m.DisplayName = normalized) == false)
            {
                message = "找不到這個會員";
                return false;
            }
            displayNameSaved = normalized;
            message = "顯示名稱已更新";
            return true;
        }

        public bool ChangePassword(HttpResponse response, string name, string oldPassword, string newPassword, int cookieDays, out string message)
        {
            message = string.Empty;
            Member mem = mp.GetMember(name);
            if (mem == null || string.IsNullOrEmpty(mem.PasswordHash))
            {
                message = "找不到這個會員";
                return false;
            }
            if (string.IsNullOrEmpty(oldPassword))
            {
                message = "請輸入目前的密碼";
                return false;
            }
            bool oldOk = mem.PasswordFormat == PasswordFormat.PlainText
                ? mem.PasswordHash == oldPassword
                : PasswordHasher.Verify(oldPassword, mem.PasswordHash);
            if (oldOk == false)
            {
                message = "目前的密碼不正確";
                return false;
            }
            if (string.IsNullOrWhiteSpace(newPassword))
            {
                message = "新密碼不可空白";
                return false;
            }
            if (newPassword.Length > 128)
            {
                message = "新密碼太長（最多 128 個字元）";
                return false;
            }
            if (newPassword == oldPassword)
            {
                message = "新密碼不能和目前的密碼相同";
                return false;
            }
            string hashed = PasswordHasher.Hash(newPassword);
            string newToken = Guid.NewGuid().ToString("N");
            bool updated = mp.Update(name, m =>
            {
                m.PasswordHash = hashed;
                m.PasswordFormat = PasswordFormat.Hashed;
                m.AccessToken = newToken;
            });
            if (updated == false)
            {
                message = "找不到這個會員";
                return false;
            }
            ForceLogin(response, newToken, cookieDays);
            message = "密碼已變更，其他裝置已登出";
            return true;
        }

        public bool UpdateMemberRole(string name, MemberRole role)
        {
            return mp.Update(name, m =>
            {
                m.IsAdmin = role == MemberRole.Admin;
                m.Status = role == MemberRole.Pending ? MemberStatus.Invalice : MemberStatus.Active;
            });
        }

        public void ForceLogin(HttpResponse response, string accessToken, int cookieDays)
        {
            response.Cookies.Delete(UserAuthenticationMiddleware._AUTH_COOKIE_NAME);
            response.Cookies.Append(UserAuthenticationMiddleware._AUTH_COOKIE_NAME, accessToken,
                new CookieOptions
                {
                    Expires = new DateTimeOffset(DateTime.Now.AddDays(cookieDays))
                });
        }

        public Member GetMember(string name)
        {
            return mp.GetMember(name);
        }

        public List<Member> GetMembers()
        {
            return mp.GetMembers();
        }

        public bool Register(string name, string displayName, string password, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(password))
            {
                message = "帳號與密碼不可空白";
                return false;
            }
            if (mp.GetMember(name) != null)
            {
                message = "此帳號已被使用";
                return false;
            }
            string normalizedDisplayName = name;
            if (string.IsNullOrWhiteSpace(displayName) == false &&
                TryNormalizeDisplayName(displayName, out normalizedDisplayName, out message) == false)
            {
                return false;
            }
            bool isFirstMember = mp.GetMembers().Count == 0;
            Member mem = new Member
            {
                Name = name,
                DisplayName = normalizedDisplayName,
                PasswordHash = PasswordHasher.Hash(password),
                PasswordFormat = PasswordFormat.Hashed,
                Status = isFirstMember ? MemberStatus.Active : MemberStatus.Invalice,
                IsAdmin = isFirstMember,
                CreateTime = DateTime.Now,
                LastAccessTime = DateTime.Now,
                AccessToken = Guid.NewGuid().ToString("N")
            };
            mp.SaveMember(mem);
            message = isFirstMember ? "註冊成功，已自動設為管理者" : "註冊成功，請等待管理者啟用帳號";
            return true;
        }

        public bool Login(HttpResponse response, string name, string password, int cookieDays, out string message)
        {
            message = string.Empty;
            Member mem = mp.GetMember(name);
            if (mem == null || string.IsNullOrEmpty(mem.PasswordHash))
            {
                message = "帳號或密碼錯誤";
                return false;
            }

            bool passwordOk;
            if (mem.PasswordFormat == PasswordFormat.PlainText)
            {
                // 管理者直接改 users.json 明碼密碼時走這裡，驗證成功後立刻轉存為雜湊（不管帳號有沒有啟用），明碼不再留存
                passwordOk = mem.PasswordHash == password;
                if (passwordOk)
                {
                    string hashed = PasswordHasher.Hash(password);
                    mp.Update(name, m =>
                    {
                        m.PasswordHash = hashed;
                        m.PasswordFormat = PasswordFormat.Hashed;
                    });
                    mem.PasswordHash = hashed;
                    mem.PasswordFormat = PasswordFormat.Hashed;
                }
            }
            else
            {
                passwordOk = PasswordHasher.Verify(password, mem.PasswordHash);
            }
            if (passwordOk == false)
            {
                message = "帳號或密碼錯誤";
                return false;
            }
            if (mem.Status != MemberStatus.Active)
            {
                message = "帳號尚未啟用，請等待管理者啟用";
                return false;
            }
            mp.Update(name, m => m.LastAccessTime = DateTime.Now);
            ForceLogin(response, mem.AccessToken, cookieDays);
            return true;
        }
    }
}
