using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using WhatSearch.Models;

namespace WhatSearch.Services.Interfaces
{
    public interface IUserService
    {
        void SetIdentityByToken(HttpContext context, string accessToken);
        bool SaveMember(Member mem, out string accessToken);
        void UpdateMember(string name);
        void UpdateMemberStatus(string name, MemberStatus status);
        /// <summary>設定身分（管理者／會員／待啟用）；找不到會員回傳 false</summary>
        bool UpdateMemberRole(string name, MemberRole role);
        /// <summary>管理者重設密碼：存成雜湊；不會更換存取權杖（對方已登入的裝置不受影響）。找不到會員回傳 false</summary>
        bool ResetPassword(string name, string newPassword);
        /// <summary>使用者自己改顯示名稱（去掉前後空白，1～20 個字，不能含控制字元）；失敗時 message 是原因</summary>
        bool UpdateDisplayName(string name, string displayName, out string displayNameSaved, out string message);
        /// <summary>
        /// 使用者自己改密碼：要先通過舊密碼驗證，成功後換新的存取權杖（其他裝置的登入會失效），
        /// 並發新的 cookie 給目前這台裝置（所以這台不會被登出）。
        /// </summary>
        bool ChangePassword(HttpResponse response, string name, string oldPassword, string newPassword, int cookieDays, out string message);
        void ForceLogin(HttpResponse response, string accessToken, int cookieDays);
        Member GetMember(string name);
        List<Member> GetMembers();
        bool Register(string name, string displayName, string password, out string message);
        /// <summary>管理者新增會員：身分由管理者指定，密碼存成雜湊；帳號重複、密碼空白或太長、顯示名稱不合規定時回傳 false，message 是原因</summary>
        bool AddMember(string name, string displayName, string password, MemberRole role, out string message);
        /// <summary>
        /// 登入。密碼連續錯誤 MaxLoginFailures 次就鎖住帳號（要管理者解除），失敗時 message 會說明已錯幾次、還剩幾次機會；
        /// 不存在的帳號也用同樣的訊息與次數（記在記憶體裡），不讓人從訊息分辨帳號存不存在。clientIp 只用來寫日誌。
        /// </summary>
        bool Login(HttpResponse response, string name, string password, int cookieDays, string clientIp, out string message);
        /// <summary>管理者解除鎖定並把失敗次數歸零；找不到會員回傳 false</summary>
        bool UnlockMember(string name);
    }
}
