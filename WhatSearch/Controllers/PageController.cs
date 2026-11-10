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
                locked = m.Locked,
                fails = m.FailedLoginCount,
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

        /// <summary>異常請求頁：同一個 IP 短時間內一直打到不存在的網址（常是網路上的掃描器亂試）會在這裡留下記錄</summary>
        [Route("admin/security")]
        [RoleAuthorize("Admin")]
        public IActionResult SecurityPage()
        {
            return View("Security");
        }

        [HttpGet]
        [Route("admin/security/data")]
        [RoleAuthorize("Admin")]
        public dynamic SecurityData()
        {
            AbnormalSnapshot snap = Ioc.Get<IAbnormalRequestService>().Get();
            AbnormalRequestConfig cfg = Ioc.GetConfig().Abnormal;
            return new
            {
                events = snap.Events,
                unread = snap.Unread,
                lastRead = snap.LastRead,
                enabled = cfg.Enabled,
                threshold = cfg.Threshold,
                windowSeconds = cfg.WindowSeconds,
                cooldownMinutes = cfg.CooldownMinutes
            };
        }

        [HttpPost]
        [Route("admin/security/read")]
        [RoleAuthorize("Admin")]
        public dynamic SecurityRead()
        {
            Ioc.Get<IAbnormalRequestService>().MarkAllRead();
            return new { Success = 1 };
        }

        [HttpPost]
        [Route("admin/security/clear")]
        [RoleAuthorize("Admin")]
        public dynamic SecurityClear()
        {
            Ioc.Get<IAbnormalRequestService>().Clear();
            return new { Success = 1, Message = "已清除記錄" };
        }

        /// <summary>管理者新增會員：身分預設會員（直接啟用）；密碼由管理者設定並自己轉交對方，不會寫進日誌</summary>
        [HttpPost]
        [Route("admin/addMember")]
        [RoleAuthorize("Admin")]
        public dynamic AddMember([FromBody] AddMemberModel model)
        {
            MemberRole role;
            switch ((model?.role ?? "member").ToLowerInvariant())
            {
                case "admin": role = MemberRole.Admin; break;
                case "member": role = MemberRole.Member; break;
                case "pending": role = MemberRole.Pending; break;
                default: return new { Success = 0, Message = "不支援的身分" };
            }
            IUserService srv = Ioc.Get<IUserService>();
            string message;
            if (srv.AddMember(model?.name, model?.displayName, model?.password, role, out message) == false)
            {
                return new { Success = 0, Message = message };
            }
            Member mem = srv.GetMember(model.name);
            return new
            {
                Success = 1,
                Message = string.Format("已新增 {0}", mem.DisplayName),
                Member = new MemberView
                {
                    name = mem.Name,
                    display = mem.DisplayName,
                    status = (int)mem.Status,
                    admin = mem.IsAdmin,
                    create = mem.CreateTime,
                    access = mem.LastAccessTime,
                    locked = mem.Locked,
                    fails = mem.FailedLoginCount,
                    self = false
                }
            };
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
            return new { Success = 1, Message = string.Format("已重設 {0} 的密碼", mem.DisplayName) + (mem.Locked ? "，並解除鎖定" : string.Empty) };
        }

        /// <summary>刪除會員前先看他有多少資料（評分、最愛、置頂），給確認視窗顯示</summary>
        [HttpGet]
        [Route("admin/memberUsage")]
        [RoleAuthorize("Admin")]
        public dynamic MemberUsage(string name)
        {
            Member mem = string.IsNullOrEmpty(name) ? null : Ioc.Get<IUserService>().GetMember(name);
            if (mem == null)
            {
                return new { Success = 0, Message = "找不到這個會員" };
            }
            var rating = Ioc.Get<IRatingService>().GetStats().Users.FirstOrDefault(u => string.Equals(u.Name, mem.Name, StringComparison.OrdinalIgnoreCase));
            var fav = Ioc.Get<IFavoriteService>().Get(mem.Name);
            return new
            {
                Success = 1,
                Ratings = rating == null ? 0 : rating.Count,
                FavAlbums = fav.Albums.Count,
                FavTracks = fav.Tracks.Count,
                Pins = Ioc.Get<IPinService>().Get(mem.Name).Count
            };
        }

        /// <summary>
        /// 刪除會員：不能刪自己，也不能直接刪管理者（要先改成會員或待啟用）。
        /// 先刪帳號（他立刻不能再寫資料），再清他的評分（先備份）、最愛、置頂、新上架已讀記錄；
        /// 萬一清資料中途失敗，留下的評分在「評分管理」會被當成已刪除會員的評分，可以再清。
        /// </summary>
        [HttpPost]
        [Route("admin/deleteMember")]
        [RoleAuthorize("Admin")]
        public dynamic DeleteMember([FromBody] DeleteMemberModel model)
        {
            IUserService srv = Ioc.Get<IUserService>();
            Member mem = model == null || string.IsNullOrEmpty(model.name) ? null : srv.GetMember(model.name);
            if (mem == null)
            {
                return new { Success = 0, Message = "找不到這個會員" };
            }
            if (string.Equals(mem.Name, User.Identity.Name, StringComparison.Ordinal))
            {
                return new { Success = 0, Message = "不能刪除自己" };
            }
            if (mem.IsAdmin)
            {
                return new { Success = 0, Message = "不能直接刪除管理者，請先把身分改成「會員」或「待啟用」再刪除" };
            }
            if (srv.DeleteMember(mem.Name) == false)
            {
                return new { Success = 0, Message = "刪除失敗，會員可能已經被刪除" };
            }
            int removedUsers;
            string backup;
            int ratings = Ioc.Get<IRatingService>().RemoveUsers(new List<string> { mem.Name }, out removedUsers, out backup);
            int favs = Ioc.Get<IFavoriteService>().RemoveMember(mem.Name);
            int pins = Ioc.Get<IPinService>().RemoveMember(mem.Name);
            Ioc.Get<INewsService>().RemoveMember(mem.Name);
            return new
            {
                Success = 1,
                Message = string.Format("已刪除 {0}，並清除他的評分 {1} 筆、最愛 {2} 筆、置頂 {3} 筆", mem.DisplayName, ratings, favs, pins)
                    + (backup != null ? "；評分原檔備份在 data\\" + backup : string.Empty)
            };
        }

        /// <summary>管理者解除會員的鎖定（密碼連續錯誤太多次被鎖住），失敗次數一併歸零</summary>
        [HttpPost]
        [Route("admin/unlockMember")]
        [RoleAuthorize("Admin")]
        public dynamic UnlockMember([FromBody] UnlockMemberModel model)
        {
            IUserService srv = Ioc.Get<IUserService>();
            Member mem = model == null || string.IsNullOrEmpty(model.name) ? null : srv.GetMember(model.name);
            if (mem == null)
            {
                return new { Success = 0, Message = "找不到這個會員" };
            }
            srv.UnlockMember(mem.Name);
            return new { Success = 1, Message = string.Format("已解除 {0} 的鎖定", mem.DisplayName) };
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
                    id = j.Id, fileId = j.FileId, name = j.Name, folder = j.Folder, kind = j.Kind, state = j.State, progress = j.Progress,
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

        /// <summary>點 CUE 檔時先預覽：解析出的專輯、曲目、輸出資料夾與已存在幾首</summary>
        [HttpPost]
        [Route("admin/convert/cue/preview")]
        [RoleAuthorize("Admin")]
        public dynamic CuePreview([FromBody] CueModel model)
        {
            string path = string.IsNullOrEmpty(model?.id) ? null : Ioc.Get<IFolderIdManager>().GetPath(model.id);
            string error;
            CuePlan plan = Ioc.Get<IVideoConvertService>().PreviewCue(path, out error);
            if (plan == null)
            {
                return new { Success = 0, Message = error };
            }
            return new { Success = 1, Plan = plan };
        }

        /// <summary>把 CUE 排進佇列，依曲目切成 MP3 放到旁邊的 Disc N 資料夾</summary>
        [HttpPost]
        [Route("admin/convert/cue/enqueue")]
        [RoleAuthorize("Admin")]
        public dynamic CueEnqueue([FromBody] CueModel model)
        {
            string path = string.IsNullOrEmpty(model?.id) ? null : Ioc.Get<IFolderIdManager>().GetPath(model.id);
            string by = (User.Identity as System.Security.Claims.ClaimsIdentity)?.Label;
            if (string.IsNullOrEmpty(by))
            {
                by = User.Identity.Name;
            }
            string error;
            ConvertJob job = Ioc.Get<IVideoConvertService>().EnqueueCue(path, by, out error);
            if (job == null)
            {
                return new { Success = 0, Message = error };
            }
            return new { Success = 1, JobId = job.Id, Message = "已排入佇列" };
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

        /// <summary>評分管理頁：找出對不到資料夾的評分（資料夾改名、刪除後留下的），讓管理者刪除</summary>
        [Route("admin/ratings")]
        [RoleAuthorize("Admin")]
        public IActionResult RatingsPage()
        {
            return View("Ratings");
        }

        [HttpGet]
        [Route("admin/ratings/data")]
        [RoleAuthorize("Admin")]
        public dynamic RatingsData()
        {
            var rc = config.Rating;
            IRatingService rating = Ioc.Get<IRatingService>();
            RatingStats stats = rating.GetStats();
            RatingInsights insights = rating.GetInsights();
            Dictionary<string, int> counts = stats.Users.ToDictionary(t => t.Name, t => t.Count, StringComparer.OrdinalIgnoreCase);
            return new
            {
                total = rating.Count(),
                legacy = stats.Legacy,
                // 每位會員各評了幾筆（沒評過的是 0）；已經刪除的會員但還留有評分的也列出來，管理者才看得到
                users = Ioc.Get<IUserService>().GetMembers()
                    .Select(m => new { name = m.Name, display = m.DisplayName, count = counts.ContainsKey(m.Name) ? counts[m.Name] : 0, exists = true })
                    .Concat(stats.Users.Where(u => Ioc.Get<IUserService>().GetMember(u.Name) == null).Select(u => new { name = u.Name, display = u.Name, count = u.Count, exists = false }))
                    .OrderBy(t => t.name, StringComparer.CurrentCultureIgnoreCase),
                // 統計：每位使用者（含已刪除會員）的排名資料，以及最多人評過、分歧最大的作品
                insights = new
                {
                    users = insights.Users.Select(u => new
                    {
                        name = u.Name,
                        exists = Ioc.Get<IUserService>().GetMember(u.Name) != null,
                        count = u.Count,
                        anime = u.Anime,
                        album = u.Album,
                        avg = u.Avg,
                        dist = u.Dist
                    }),
                    mostRated = insights.MostRated,
                    divergent = insights.Divergent
                },
                animeShares = rc != null && rc.Shares != null ? rc.Shares : new List<string>(),
                albumShares = rc != null && rc.Albums != null && rc.Albums.Shares != null ? rc.Albums.Shares : new List<string>()
            };
        }

        /// <summary>要走訪分享根的資料夾，網路磁碟上可能要一段時間，所以只在管理者按「檢查」時才做</summary>
        [HttpPost]
        [Route("admin/ratings/scan")]
        [RoleAuthorize("Admin")]
        public dynamic RatingsScan()
        {
            return Ioc.Get<IRatingService>().Scan();
        }

        [HttpPost]
        [Route("admin/ratings/delete")]
        [RoleAuthorize("Admin")]
        public dynamic RatingsDelete([FromBody] RatingsDeleteModel model)
        {
            if (model == null || model.Keys == null || model.Keys.Count == 0)
            {
                return new { Success = 0, Message = "沒有選取要刪除的項目" };
            }
            string backup;
            int n = Ioc.Get<IRatingService>().DeleteOrphans(model.Keys, out backup);
            if (n == 0)
            {
                return new { Success = 1, Deleted = 0, Message = "沒有刪除任何項目（它們可能已經找得到資料夾，或已被刪除）" };
            }
            return new { Success = 1, Deleted = n, Backup = backup, Message = "已刪除 " + n + " 筆" + (backup != null ? "，原檔備份在 data\\" + backup : "") };
        }

        /// <summary>清掉已經刪除的會員留下的評分（以目前的會員清單為準，由伺服器判斷，不信任前端傳來的名單）；清之前會備份</summary>
        [HttpPost]
        [Route("admin/ratings/purge-deleted")]
        [RoleAuthorize("Admin")]
        public dynamic RatingsPurgeDeleted()
        {
            IRatingService rating = Ioc.Get<IRatingService>();
            IUserService users = Ioc.Get<IUserService>();
            var gone = rating.GetStats().Users.Where(u => users.GetMember(u.Name) == null).Select(u => u.Name).ToList();
            if (gone.Count == 0)
            {
                return new { Success = 0, Message = "沒有已刪除會員留下的評分" };
            }
            int removedUsers;
            string backup;
            int removed = rating.RemoveUsers(gone, out removedUsers, out backup);
            return new
            {
                Success = 1,
                Removed = removed,
                Users = removedUsers,
                Backup = backup,
                Message = "已清除 " + removedUsers + " 位已刪除會員的評分，共 " + removed + " 筆" + (backup != null ? "；原檔備份在 data\\" + backup : string.Empty)
            };
        }

        /// <summary>把舊的共用評分（1.38.0 以前全站一份的 ratings.json）整份轉給某位會員，轉完清空；轉之前會備份</summary>
        [HttpPost]
        [Route("admin/ratings/transfer")]
        [RoleAuthorize("Admin")]
        public dynamic RatingsTransfer([FromBody] RatingsTransferModel model)
        {
            if (model == null || string.IsNullOrEmpty(model.To) || Ioc.Get<IUserService>().GetMember(model.To) == null)
            {
                return new { Success = 0, Message = "找不到這位會員" };
            }
            IRatingService rating = Ioc.Get<IRatingService>();
            if (rating.GetStats().Legacy == 0)
            {
                return new { Success = 0, Message = "沒有舊的共用評分可以轉" };
            }
            int skipped;
            string backup;
            int moved = rating.TransferLegacy(Ioc.Get<IUserService>().GetMember(model.To).Name, model.Overwrite, out skipped, out backup);
            return new
            {
                Success = 1,
                Moved = moved,
                Skipped = skipped,
                Backup = backup,
                Message = "已轉入 " + moved + " 筆" + (skipped > 0 ? "，略過 " + skipped + " 筆（對方已有自己的評分）" : string.Empty) + (backup != null ? "；舊檔備份在 data\\" + backup : string.Empty)
            };
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

        public class CueModel
        {
            public string id { get; set; }
        }

        public class ConvertCancelModel
        {
            public string id { get; set; }
        }

        public class DeleteMemberModel
        {
            public string name { get; set; }
        }

        public class UnlockMemberModel
        {
            public string name { get; set; }
        }

        public class ResetPasswordModel
        {
            public string name { get; set; }
            public string password { get; set; }
        }

        public class AddMemberModel
        {
            public string name { get; set; }
            public string displayName { get; set; }
            public string password { get; set; }
            /// <summary>admin／member／pending；沒給就是 member</summary>
            public string role { get; set; }
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
            public bool locked { get; set; }
            public int fails { get; set; }
            public bool self { get; set; }
        }
    }
    public class RatingsTransferModel
    {
        [System.Text.Json.Serialization.JsonPropertyName("to")]
        public string To { get; set; }
        /// <summary>true：舊評分覆蓋對方已有的；false（預設）：對方已經評過的保留他自己的</summary>
        [System.Text.Json.Serialization.JsonPropertyName("overwrite")]
        public bool Overwrite { get; set; }
    }

    public class RatingsDeleteModel
    {
        [System.Text.Json.Serialization.JsonPropertyName("keys")]
        public List<string> Keys { get; set; }
    }
}
