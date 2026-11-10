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

        /// <summary>密碼連續錯誤幾次就鎖住帳號</summary>
        public const int MaxLoginFailures = 5;
        // 不存在的帳號也要有同樣的「錯幾次、鎖住」反應，否則看訊息就知道帳號存不存在；這份記錄只放記憶體，過一段時間沒再試就忘掉
        const int UnknownNameMinutes = 30;
        const int UnknownNameMax = 1000;
        class UnknownTry
        {
            public int Count;
            public DateTime Last;
            public bool Locked;
        }
        static readonly Dictionary<string, UnknownTry> unknownTries = new Dictionary<string, UnknownTry>(StringComparer.Ordinal);
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
                // 管理者給了新密碼，鎖住的帳號也一併解除
                m.Locked = false;
                m.FailedLoginCount = 0;
            });
        }

        public bool UnlockMember(string name)
        {
            return mp.Update(name, m =>
            {
                m.Locked = false;
                m.FailedLoginCount = 0;
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

        public bool DeleteMember(string name)
        {
            bool ok = mp.Remove(name);
            if (ok)
            {
                logger.Info("管理者刪除會員：{0}", name);
            }
            return ok;
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
            bool isFirstMember = mp.GetMembers().Count == 0;
            if (TryCreateMember(name, displayName, password, isFirstMember, isFirstMember ? MemberStatus.Active : MemberStatus.Invalice, out message) == false)
            {
                return false;
            }
            message = isFirstMember ? "註冊成功，已自動設為管理者" : "註冊成功，請等待管理者啟用帳號";
            return true;
        }

        /// <summary>管理者新增會員：身分由管理者指定（管理者、會員直接啟用；待啟用不能登入）</summary>
        public bool AddMember(string name, string displayName, string password, MemberRole role, out string message)
        {
            if (string.IsNullOrWhiteSpace(password) == false && password.Length > 128)
            {
                message = "密碼太長（最多 128 個字元）";
                return false;
            }
            return TryCreateMember(name, displayName, password, role == MemberRole.Admin,
                role == MemberRole.Pending ? MemberStatus.Invalice : MemberStatus.Active, out message);
        }

        // 註冊與管理者新增共用：檢查帳號、密碼、顯示名稱後建立會員（密碼存成雜湊）
        bool TryCreateMember(string name, string displayName, string password, bool isAdmin, MemberStatus status, out string message)
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
            // 建立時間與上次登入時間用同一個時間點，管理頁才能判斷「從未登入」
            DateTime now = DateTime.Now;
            Member mem = new Member
            {
                Name = name,
                DisplayName = normalizedDisplayName,
                PasswordHash = PasswordHasher.Hash(password),
                PasswordFormat = PasswordFormat.Hashed,
                Status = status,
                IsAdmin = isAdmin,
                CreateTime = now,
                LastAccessTime = now,
                AccessToken = Guid.NewGuid().ToString("N")
            };
            mp.SaveMember(mem);
            return true;
        }

        static string FailMessage(int count)
        {
            return string.Format("帳號或密碼錯誤（已失敗 {0}/{1} 次，再錯 {2} 次帳號會被鎖住）", count, MaxLoginFailures, MaxLoginFailures - count);
        }

        static string LockedNowMessage()
        {
            return string.Format("密碼連續錯誤 {0} 次，帳號已鎖住，請聯絡管理者解除鎖定", MaxLoginFailures);
        }

        static string LockedMessage()
        {
            return string.Format("帳號已被鎖住（密碼曾連續錯誤 {0} 次），請聯絡管理者解除鎖定", MaxLoginFailures);
        }

        // 寫進日誌的文字：去掉控制字元（避免偽造日誌換行）並限制長度
        static string LogText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (sb.Length >= 50)
                {
                    break;
                }
                if (c >= 0x20 && c != 0x7f)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // 不存在的帳號：和真的帳號一樣計次、一樣會「鎖住」，只是記在記憶體
        static string UnknownNameMessage(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "帳號或密碼錯誤";
            }
            DateTime now = DateTime.Now;
            lock (unknownTries)
            {
                UnknownTry t;
                if (unknownTries.TryGetValue(name, out t) == false || now - t.Last > TimeSpan.FromMinutes(UnknownNameMinutes))
                {
                    if (unknownTries.Count >= UnknownNameMax)
                    {
                        foreach (string key in new List<string>(unknownTries.Keys))
                        {
                            if (now - unknownTries[key].Last > TimeSpan.FromMinutes(UnknownNameMinutes))
                            {
                                unknownTries.Remove(key);
                            }
                        }
                        if (unknownTries.Count >= UnknownNameMax)
                        {
                            // 被大量亂猜的帳號塞滿了：不再追蹤新的，只回一般訊息，避免吃光記憶體
                            return "帳號或密碼錯誤";
                        }
                    }
                    t = new UnknownTry { Last = now };
                    unknownTries[name] = t;
                }
                if (t.Locked)
                {
                    return LockedMessage();
                }
                t.Count++;
                t.Last = now;
                if (t.Count >= MaxLoginFailures)
                {
                    t.Locked = true;
                    return LockedNowMessage();
                }
                return FailMessage(t.Count);
            }
        }

        public bool Login(HttpResponse response, string name, string password, int cookieDays, string clientIp, out string message)
        {
            message = string.Empty;
            password = password ?? string.Empty;
            Member mem = mp.GetMember(name);
            if (mem == null || string.IsNullOrEmpty(mem.PasswordHash))
            {
                message = UnknownNameMessage(name);
                logger.Info("登入失敗：找不到帳號 {0}（來源 {1}）", LogText(name), LogText(clientIp));
                return false;
            }
            if (mem.Locked)
            {
                // 鎖住期間連正確的密碼都不收，否則鎖住就擋不了猜密碼
                message = LockedMessage();
                logger.Info("登入被拒：帳號 {0} 已鎖住（來源 {1}）", LogText(name), LogText(clientIp));
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
                // 在 Update 裡累加（會鎖住整份資料），同時有多個請求猜密碼時次數才不會算少
                int count = 0;
                bool locked = false;
                mp.Update(name, m =>
                {
                    if (m.Locked == false)
                    {
                        m.FailedLoginCount++;
                        if (m.FailedLoginCount >= MaxLoginFailures)
                        {
                            m.Locked = true;
                        }
                    }
                    count = m.FailedLoginCount;
                    locked = m.Locked;
                });
                if (locked)
                {
                    message = LockedNowMessage();
                    logger.Warn("帳號 {0} 密碼連續錯誤 {1} 次，已鎖住（來源 {2}）", LogText(name), count, LogText(clientIp));
                }
                else
                {
                    message = FailMessage(count);
                    logger.Info("登入失敗：帳號 {0} 密碼錯誤，第 {1} 次（來源 {2}）", LogText(name), count, LogText(clientIp));
                }
                return false;
            }
            if (mem.Status != MemberStatus.Active)
            {
                if (mem.FailedLoginCount > 0)
                {
                    mp.Update(name, m => m.FailedLoginCount = 0);
                }
                message = "帳號尚未啟用，請等待管理者啟用";
                return false;
            }
            mp.Update(name, m =>
            {
                m.LastAccessTime = DateTime.Now;
                m.FailedLoginCount = 0;
            });
            ForceLogin(response, mem.AccessToken, cookieDays);
            return true;
        }
    }
}
