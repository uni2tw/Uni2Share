using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using WhatSearch.Core;
using WhatSearch.Service;
using WhatSearch.Models;
using WhatSearch.Services;
using WhatSearch.Services.Interfaces;
using WhatSearch.WebAPIs.Filters;

namespace WhatSearch.Controllers
{
    //[Route("[controller]")]
    public class PageController : Controller
    {        
        //[Route("")]
        //public IActionResult Index()
        //{
        //    return Redirect("/page");
        //}
        SystemConfig config = Ioc.GetConfig();

        [Route("")]
        public IActionResult Index()
        {
            return Redirect("/page/");
        }
        [Route("page/{*pathInfo}")]
        public IActionResult List(string pathInfo)
        {
            return View();
        }
        [Route("/page/login")]
        public IActionResult Login()
        {
            return View();
        }

        [Route("admin")]
        [RoleAuthorize("Admin")]
        public IActionResult Members()
        {
            IUserService srv = Ioc.Get<IUserService>();
            string me = User.Identity.Name;
            // 只給頁面需要的欄位（不含密碼雜湊與存取權杖）
            var model = srv.GetMembers().Select(m => new MemberView
            {
                name = m.Name,
                display = m.DisplayName,
                status = (int)m.Status,
                admin = m.IsAdmin,
                create = m.CreateTime,
                access = m.LastAccessTime,
                self = string.Equals(m.Name, me, StringComparison.Ordinal)
            }).ToList();
            return View(model);
        }

        [HttpPost]
        [Route("admin/updateMember")]
        [RoleAuthorize("Admin")]
        public dynamic UpdateMember([FromBody] UpdateMemberModel model)
        {
            IUserService srv = Ioc.Get<IUserService>();
            Member mem = model == null || string.IsNullOrEmpty(model.name) ? null : srv.GetMember(model.name);
            if (mem == null)
            {
                return new { Success = 0, Message = "找不到這個會員" };
            }
            MemberRole role;
            switch ((model.role ?? string.Empty).ToLowerInvariant())
            {
                case "admin": role = MemberRole.Admin; break;
                case "member": role = MemberRole.Member; break;
                case "pending": role = MemberRole.Pending; break;
                default: return new { Success = 0, Message = "不支援的身分" };
            }
            // 不能改自己的身分，避免把自己降級或停用後沒有人能再進管理頁
            if (string.Equals(mem.Name, User.Identity.Name, StringComparison.Ordinal))
            {
                return new { Success = 0, Message = "不能修改自己的身分" };
            }
            // 至少要保留一位管理者
            if (mem.IsAdmin && mem.Status == MemberStatus.Active && role != MemberRole.Admin
                && srv.GetMembers().Count(m => m.IsAdmin && m.Status == MemberStatus.Active) <= 1)
            {
                return new { Success = 0, Message = "至少要保留一位管理者" };
            }
            srv.UpdateMemberRole(mem.Name, role);
            string label = role == MemberRole.Admin ? "管理者" : (role == MemberRole.Member ? "會員" : "待啟用");
            return new { Success = 1, Message = string.Format("{0} 已設為{1}", mem.DisplayName, label), Role = model.role.ToLowerInvariant() };
        }

        /// <summary>管理者重設會員密碼（也可以重設自己的）；密碼不會寫進日誌</summary>
        [HttpPost]
        [Route("admin/resetPassword")]
        [RoleAuthorize("Admin")]
        public dynamic ResetPassword([FromBody] ResetPasswordModel model)
        {
            IUserService srv = Ioc.Get<IUserService>();
            Member mem = model == null || string.IsNullOrEmpty(model.name) ? null : srv.GetMember(model.name);
            if (mem == null)
            {
                return new { Success = 0, Message = "找不到這個會員" };
            }
            if (string.IsNullOrWhiteSpace(model.password))
            {
                return new { Success = 0, Message = "密碼不可空白" };
            }
            if (model.password.Length > 128)
            {
                return new { Success = 0, Message = "密碼太長（最多 128 個字元）" };
            }
            srv.ResetPassword(mem.Name, model.password);
            return new { Success = 1, Message = string.Format("已重設 {0} 的密碼", mem.DisplayName) };
        }

        /// <summary>MKV 轉檔管理頁：被標註「無法播放」的動畫，把影片轉成 H.264 8-bit（原檔改名 .bak 保留）</summary>
        [Route("admin/convert")]
        [RoleAuthorize("Admin")]
        public IActionResult ConvertPage()
        {
            return View("Convert");
        }

        /// <summary>被標註「無法播放」的資料夾與裡面的 MKV（要讀檔案編碼，所以只在載入與重新整理時呼叫，進度另外輪詢 convert/jobs）</summary>
        [HttpGet]
        [Route("admin/convert/folders")]
        [RoleAuthorize("Admin")]
        public dynamic ConvertFolders()
        {
            IVideoConvertService srv = Ioc.Get<IVideoConvertService>();
            return new { enabled = srv.Enabled, folders = srv.Enabled ? srv.ListFolders() : new List<ConvertFolder>() };
        }

        [HttpGet]
        [Route("admin/convert/jobs")]
        [RoleAuthorize("Admin")]
        public dynamic ConvertJobs()
        {
            return new
            {
                jobs = Ioc.Get<IVideoConvertService>().GetJobs().Select(j => new
                {
                    id = j.Id, fileId = j.FileId, name = j.Name, folder = j.Folder, state = j.State, progress = j.Progress,
                    message = j.Message, by = j.By, queuedAt = j.QueuedAt, finishedAt = j.FinishedAt
                })
            };
        }

        [HttpPost]
        [Route("admin/convert/enqueue")]
        [RoleAuthorize("Admin")]
        public dynamic ConvertEnqueue([FromBody] ConvertEnqueueModel model)
        {
            if (model == null || model.ids == null || model.ids.Count == 0)
            {
                return new { Success = 0, Message = "沒有選擇檔案" };
            }
            IVideoConvertService srv = Ioc.Get<IVideoConvertService>();
            IFolderIdManager fimgr = Ioc.Get<IFolderIdManager>();
            string by = (User.Identity as System.Security.Claims.ClaimsIdentity)?.Label;
            if (string.IsNullOrEmpty(by))
            {
                by = User.Identity.Name;
            }
            int queued = 0;
            List<string> errors = new List<string>();
            foreach (string id in model.ids.Take(200))
            {
                string path = string.IsNullOrEmpty(id) ? null : fimgr.GetPath(id);
                string error;
                if (srv.Enqueue(path, by, out error) != null)
                {
                    queued++;
                }
                else
                {
                    errors.Add((string.IsNullOrEmpty(path) ? id : System.IO.Path.GetFileName(path)) + "：" + error);
                }
            }
            return new { Success = queued > 0 ? 1 : 0, Queued = queued, Errors = errors, Message = queued > 0 ? string.Format("已排入 {0} 個檔案", queued) : "沒有排入任何檔案" };
        }

        [HttpPost]
        [Route("admin/convert/cancel")]
        [RoleAuthorize("Admin")]
        public dynamic ConvertCancel([FromBody] ConvertCancelModel model)
        {
            bool ok = model != null && Ioc.Get<IVideoConvertService>().Cancel(model.id);
            return new { Success = ok ? 1 : 0, Message = ok ? "已取消" : "找不到這個工作，或已經結束" };
        }

        /// <summary>清掉已結束的工作紀錄（只在記憶體，檔案不受影響）</summary>
        [HttpPost]
        [Route("admin/convert/clear")]
        [RoleAuthorize("Admin")]
        public dynamic ConvertClear()
        {
            int n = Ioc.Get<IVideoConvertService>().ClearFinished();
            return new { Success = 1, Cleared = n, Message = n > 0 ? string.Format("已清除 {0} 筆紀錄", n) : "沒有可清除的紀錄" };
        }

        /// <summary>播放監控頁：誰正在播什麼、目前速率、今日流量、熱門內容、伺服器狀態</summary>
        [Route("admin/monitor")]
        [RoleAuthorize("Admin")]
        public IActionResult MonitorPage()
        {
            return View("Monitor");
        }

        [HttpGet]
        [Route("admin/monitor/data")]
        [RoleAuthorize("Admin")]
        public dynamic MonitorData()
        {
            PlaybackSnapshot snap = Ioc.Get<IPlaybackMonitorService>().GetSnapshot();
            List<ConvertJob> jobs = Ioc.Get<IVideoConvertService>().GetJobs();
            ITranscodeService transcode = Ioc.Get<ITranscodeService>();
            return new
            {
                sessions = snap.Sessions,
                mbps = snap.Mbps,
                todayBytes = snap.TodayBytes,
                todayPlays = snap.TodayPlays,
                slowCount = snap.SlowCount,
                hours = snap.Hours,
                users = snap.Users,
                folders = snap.Folders,
                devices = snap.Devices,
                weekPlays = snap.WeekPlays,
                server = new
                {
                    startedAt = snap.StartedAtMs,
                    convertRunning = jobs.Count(j => j.State == ConvertJob.StateRunning),
                    convertPending = jobs.Count(j => j.State == ConvertJob.StatePending),
                    transcodeEnabled = transcode.Enabled,
                    cacheBytes = transcode.CacheBytes,
                    cacheLimitBytes = transcode.CacheLimitBytes,
                    indexedFiles = Ioc.Get<ISearchSercice>().DocCount
                }
            };
        }

        /// <summary>我的帳號：看自己的資料、改顯示名稱、改密碼（任何已登入的會員都可以用，未登入轉去登入）</summary>
        [Route("account")]
        public IActionResult AccountPage()
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Redirect("/page/login?returnUrl=" + Uri.EscapeDataString("/account" + Request.QueryString));
            }
            return View("Account");
        }

        [HttpGet]
        [Route("account/data")]
        public dynamic AccountData()
        {
            Member mem = GetCurrentMember();
            if (mem == null)
            {
                return StatusCode(401);
            }
            return new
            {
                name = mem.Name,
                display = mem.DisplayName,
                admin = mem.IsAdmin,
                create = mem.CreateTime,
                access = mem.LastAccessTime,
                displayNameMaxLength = UserService.DisplayNameMaxLength
            };
        }

        [HttpPost]
        [Route("account/displayName")]
        public dynamic AccountUpdateDisplayName([FromBody] DisplayNameModel model)
        {
            Member mem = GetCurrentMember();
            if (mem == null)
            {
                return StatusCode(401);
            }
            string saved, message;
            bool success = Ioc.Get<IUserService>().UpdateDisplayName(mem.Name, model?.displayName, out saved, out message);
            return new { success, message, display = saved };
        }

        [HttpPost]
        [Route("account/password")]
        public dynamic AccountChangePassword([FromBody] ChangePasswordModel model)
        {
            Member mem = GetCurrentMember();
            if (mem == null)
            {
                return StatusCode(401);
            }
            string message;
            bool success = Ioc.Get<IUserService>().ChangePassword(Response, mem.Name, model?.oldPassword, model?.newPassword,
                config.Login.CookieDays, out message);
            return new { success, message };
        }

        Member GetCurrentMember()
        {
            return User.Identity.IsAuthenticated ? Ioc.Get<IUserService>().GetMember(User.Identity.Name) : null;
        }

        public class DisplayNameModel
        {
            public string displayName { get; set; }
        }

        public class ChangePasswordModel
        {
            public string oldPassword { get; set; }
            public string newPassword { get; set; }
        }

        public class ConvertEnqueueModel
        {
            public List<string> ids { get; set; }
        }

        public class ConvertCancelModel
        {
            public string id { get; set; }
        }

        public class ResetPasswordModel
        {
            public string name { get; set; }
            public string password { get; set; }
        }

        public class UpdateMemberModel
        {
            public string name { get; set; }
            /// <summary>admin／member／pending</summary>
            public string role { get; set; }
        }

        /// <summary>管理頁用的會員資料（不含密碼雜湊與存取權杖）</summary>
        public class MemberView
        {
            public string name { get; set; }
            public string display { get; set; }
            public int status { get; set; }
            public bool admin { get; set; }
            public DateTime create { get; set; }
            public DateTime access { get; set; }
            public bool self { get; set; }
        }
    }
}
