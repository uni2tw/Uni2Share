using System;
using System.Security.Principal;
using System.Text.Json.Serialization;
using WhatSearch.Models.JsonConverters;

namespace WhatSearch.Models
{
    public class UserIdentity : IIdentity
    {
        public UserIdentity(string name)
        {
            this.Name = name;
        }

        public string AuthenticationType
        {
            get
            {
                return "Token";
            }
        }

        public bool IsAuthenticated
        {
            get
            {
                return string.IsNullOrEmpty(Name) == false;
            }
        }

        public string Name { get; set; }
    }

    public class Member
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
        [JsonPropertyName("display")]
        public string DisplayName { get; set; }
        [JsonPropertyName("pic")]
        public string Picture { get; set; }
        [JsonPropertyName("token")]
        public string AccessToken { get; set; }
        [JsonPropertyName("pwd")]
        public string PasswordHash { get; set; }
        [JsonPropertyName("pwdFmt")]
        public PasswordFormat PasswordFormat { get; set; }
        [JsonPropertyName("status")]
        public MemberStatus Status { get; set; }
        [JsonConverter(typeof(BoolConverter))]
        [JsonPropertyName("admin")]
        public bool IsAdmin { get; set; }
        [JsonPropertyName("create")]
        public DateTime CreateTime { get; set; }
        [JsonPropertyName("access")]
        public DateTime LastAccessTime { get; set; }
        /// <summary>連續輸入錯誤密碼的次數；密碼正確、管理者解除鎖定或重設密碼後歸零</summary>
        [JsonPropertyName("fails")]
        public int FailedLoginCount { get; set; }
        /// <summary>密碼連續錯誤太多次而被鎖住：鎖住期間連正確的密碼都不能登入，要由管理者解除（已經登入的裝置不受影響）</summary>
        [JsonPropertyName("locked")]
        public bool Locked { get; set; }
    }

    /// <summary>管理頁可以設定的身分：管理者（已啟用）、會員（已啟用）、待啟用（不能登入）</summary>
    public enum MemberRole
    {
        Admin = 0,
        Member = 1,
        Pending = 2
    }

    public enum MemberStatus
    {
        Invalice = 0,
        Active = 1,
        TryPasswrodTooMany = 2
    }

    public enum PasswordFormat
    {
        PlainText = 0,
        Hashed = 1
    }
}
