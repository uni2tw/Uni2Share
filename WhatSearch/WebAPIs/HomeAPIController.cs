using Markdig;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using WhatSearch.Core;
using WhatSearch.Middlewares;
using WhatSearch.Models;
using WhatSearch.Service;
using WhatSearch.Services;
using WhatSearch.Services.Interfaces;
using WhatSearch.Utilities;
using WhatSearch.Utility;
using WhatSearch.WebAPIs.Filters;

namespace WhatSearch.WebAPIs
{
    [ApiController]
    public class HomeAPIController : Controller
    {
        ISearchSercice searchService = Ioc.Get<ISearchSercice>();
        IFolderIdManager fimgr = Ioc.Get<IFolderIdManager>();
        IMainService mainService = Ioc.Get<IMainService>();
        IFavoriteService favoriteService = Ioc.Get<IFavoriteService>();
        ILoudnessService loudnessService = Ioc.Get<ILoudnessService>();
        ITranscodeService transcodeService = Ioc.Get<ITranscodeService>();
        IRatingService ratingService = Ioc.Get<IRatingService>();
        IUnplayableService unplayableService = Ioc.Get<IUnplayableService>();
        INewsService newsService = Ioc.Get<INewsService>();
        IAbnormalRequestService abnormalService = Ioc.Get<IAbnormalRequestService>();
        SystemConfig config = Ioc.GetConfig();
        static ILogger logger = LogManager.GetCurrentClassLogger();

        private static readonly MarkdownPipeline TextPreviewMarkdownPipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        [HttpPost]
        [Route("api/search")]
        public dynamic Search([FromBody] SearchModel model)
        {
            string q = model.q?.ToLower();
            List<FileInfoView> items = new List<FileInfoView>();
            List<IndexedFileDoc> docs = searchService.Query(q, config.MaxSearchResult);
            HashSet<string> importedDirs = new HashSet<string>();
            foreach (IndexedFileDoc doc in docs)
            {
                if (importedDirs.Contains(doc.DirectoryName) == false)
                {
                    importedDirs.Add(doc.DirectoryName);
                    FileInfoView fiv = mainService.GetFileInfoView(new DirectoryInfo(doc.DirectoryName));
                    // 資料夾已不存在（索引裡還有舊紀錄）時會是 null，不要放進結果，否則前端的結果畫面會出錯
                    if (fiv != null)
                    {
                        items.Add(fiv);
                    }
                }
            }
            foreach (IndexedFileDoc doc in docs)
            {
                string fileType = Helper.GetFileDocType(Path.GetExtension(doc.Name));
                string efid = fimgr.GetIdByFilePath(doc.FullName);

                string relPath;
                PathUtility.TryGetRelPath(doc.FullName, out relPath);
                items.Add(new FileInfoView
                {
                    Id = efid,
                    Size = Helper.GetReadableByteSize(doc.Length),
                    GetUrl = PathUtility.ToGetUrl(relPath),
                    Title = doc.Name,
                    Modify = doc.LastWriteTime.ToString(),
                    Type = fileType
                });
            }
            return new
            {
                message = "找到 " + items.Count + " 筆.",
                items
            };
        }

        [HttpPost]
        [Route("api/pathId")]
        public dynamic PathId([FromBody] PathIdInputModel model)
        {
            try
            {
                string pathname = Encoding.UTF8.GetString(HttpUtility.UrlDecodeToBytes(model.pathname));
                pathname = pathname.TrimStart("/page/", StringComparison.OrdinalIgnoreCase);

                string efid = fimgr.GetId(pathname);

                return Ok(new { PathId = efid });
            }
            catch (Exception ex)
            {
                return BadRequest("發生錯誤，請稍候再試");
            }
        }

        [HttpPost]
        [Route("api/folder")]
        public dynamic Folder([FromBody] FolderInputModel model)
        {
            List<FileInfoView> items = new ();
            List<FileInfoView> breadcrumbs = new();
            List<DiscGroup> discs = null;
            object currentRating = null;
            if (model == null || string.IsNullOrEmpty(model.p))
            {
                items.AddRange(mainService.GetRootShareFolders());
                breadcrumbs.AddRange(mainService.GetBreadcrumbs(string.Empty));
            }
            else
            {
                FolderNotFoundResult missing = mainService.ResolveMissingFolder(model.p);
                if (missing != null)
                {
                    // 資料夾不存在（例如改過名）：回傳 200 + notFound，由前端顯示「找不到」畫面與建議
                    return new
                    {
                        notFound = true,
                        id = model.p,
                        message = "找不到資料夾",
                        requested = missing.RequestedPath,
                        nearest = new { id = missing.NearestId, title = missing.NearestTitle },
                        suggestions = missing.Suggestions.Select(x => new { id = x.Id, title = x.Title, score = Math.Round(x.Score, 2) }),
                        items,
                        breadcrumbs = mainService.GetBreadcrumbs(missing.NearestId)
                            .Select(t => new { t.Id, Text = t.Title, Link = t.GetUrl, Type = t.Type })
                    };
                }
                items.AddRange(mainService.GetFileInfoViewsInTheFolder(model.p));
                breadcrumbs.AddRange(mainService.GetBreadcrumbs(model.p));
                discs = mainService.GetMultiDiscGroups(model.p);
                string folderPath = fimgr.GetPath(model.p);
                string me = User.Identity.IsAuthenticated ? User.Identity.Name : null;        // 評分是每人一份
                foreach (FileInfoView item in items)
                {
                    if (item.Type == Helper.ConstStrings.Folder)
                    {
                        string childPath = fimgr.GetPath(item.Id);
                        ratingService.Apply(item, childPath, me);
                        if (item.Rateable != true)
                        {
                            ratingService.ApplyAlbumListRating(item, childPath, me);        // 專輯：已評分的才在列表上顯示星星
                        }
                        else if (unplayableService.Get(ratingService.Resolve(Path.GetFileName(childPath.TrimEnd('\\', '/'))).Key) != null)
                        {
                            item.Unplayable = true;        // 動畫：管理者標註過無法播放，列表上顯示提醒
                        }
                    }
                }
                if (ratingService.IsRateable(folderPath))
                {
                    FileInfoView self = new FileInfoView();
                    ratingService.Apply(self, folderPath, me);
                    RatingInfo animeInfo = ratingService.Resolve(new DirectoryInfo(folderPath).Name);
                    UnplayableInfo mark = unplayableService.Get(animeInfo.Key);
                    currentRating = new { id = model.p, name = self.TitlePlain, rating = self.Rating, kind = "anime", unplayable = mark != null, unplayable_by = mark == null ? null : mark.By, unplayable_time = mark == null ? (long?)null : mark.Time };
                }
                else if (ratingService.IsAlbumShare(folderPath) && (discs != null || items.Count(t => t.Type == Helper.ConstStrings.Music) >= 2))
                {
                    // 專輯頁（2 首以上音樂，或多片裝的母資料夾）：最上方顯示「我的評分」
                    RatingInfo album = ratingService.ResolveAlbum(new DirectoryInfo(folderPath).Name, me);
                    currentRating = new { id = model.p, name = album.Plain, rating = album.Rating, kind = "album", unplayable = false, unplayable_by = (string)null, unplayable_time = (long?)null };
                }
            }
            return new
            {
                id = model.p,
                //pushState會用到url
                url = PathUtility.GetRelativePath(fimgr.GetPath(model.p)),
                message = "找到 " + items.Count + " 筆.",
                items,
                discs = discs == null ? null : discs.Select(d => new { id = d.Id, no = d.No, title = d.Title, items = d.Items }),
                current = currentRating,
                breadcrumbs = breadcrumbs.Select(t => new { t.Id, Text = t.Title, Link = t.GetUrl, Type = t.Type })
            };
        }

        /// <summary>
        /// 動畫資料夾的封面圖（匯出評分圖用）：資料夾裡的 cover／folder 圖片檔，沒有就取前幾部影片（專輯是音樂）內嵌的封面。
        /// 只服務可評分的動畫資料夾與專輯頁；需要登入才能看的分享根，沒登入一律 404。沒有封面也是 404（前端改畫名稱色塊）。
        /// </summary>
        [HttpGet]
        [Route("api/foldercover")]
        public dynamic FolderCover([FromQuery] string id)
        {
            string folderPath = string.IsNullOrEmpty(id) ? string.Empty : fimgr.GetPath(id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false || IsCoverFolder(id, folderPath) == false)
            {
                return NotFound();
            }
            // 和 /get/ 一樣，沒登入不給封面
            if (User.Identity.IsAuthenticated == false)
            {
                return NotFound();
            }
            WhatSearch.Services.FolderCover cover = Ioc.Get<IFolderCoverService>().Find(folderPath);
            if (cover == null)
            {
                return NotFound();
            }
            Response.Headers["Cache-Control"] = "no-store";
            return this.File(cover.Data, cover.MimeType);
        }

        [HttpGet]
        [Route("get/{*pathInfo}")]
        [UserAuthorize]
        public dynamic GetFile(string pathInfo, [FromQuery] bool preview = false, [FromQuery] bool cover = false,
            [FromQuery] bool subtitle = false, [FromQuery] bool subtitleTracks = false, [FromQuery] int track = 0,
            [FromQuery] bool mp3 = false)
        {
            string targetPath;
            if (PathUtility.TryGetAbsolutePath("/" + pathInfo, out targetPath) == false)
            {
                return NotFound();
            }
            string fileExt = Path.GetExtension(targetPath);
            if (subtitleTracks || (subtitle && track > 0))
            {
                // MKV 內嵌文字字幕：只處理已開放播放的 .mkv
                if (fileExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase) == false ||
                    config.PlayTypes.Contains(fileExt) == false ||
                    System.IO.File.Exists(targetPath) == false)
                {
                    return NotFound();
                }
                if (subtitleTracks)
                {
                    var list = MatroskaSubtitleReader.ListTracks(targetPath)
                        .Where(t => t.IsSupported)
                        .Select(t => new
                        {
                            number = t.Number,
                            name = t.Name,
                            language = t.Language,
                            codec = t.CodecId,
                            isDefault = t.IsDefault,
                            isForced = t.IsForced
                        }).ToList();
                    return new { tracks = list };
                }
                FileInfo mkv = new FileInfo(targetPath);
                string cacheKey = string.Format("mkvsub://{0}|{1}|{2}|{3}", targetPath, mkv.Length, mkv.LastWriteTimeUtc.Ticks, track);
                string cachedVtt = Ioc.GetCache().Get<string>(cacheKey);
                if (cachedVtt == null)
                {
                    try
                    {
                        cachedVtt = MatroskaSubtitleReader.ExtractAsWebVtt(targetPath, track);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "extract mkv subtitle failed: " + targetPath);
                        return NotFound();
                    }
                    if (cachedVtt == null)
                    {
                        return NotFound();
                    }
                    Ioc.GetCache().Set(cacheKey, cachedVtt, TimeSpan.FromMinutes(30));
                }
                return this.Content(cachedVtt, "text/vtt; charset=utf-8");
            }
            if (subtitle)
            {
                // 字幕不受 playtypes 白名單限制；統一轉成 UTF-8 的 WebVTT 回傳
                if (SubtitleConverter.IsSubtitleExtension(fileExt) == false || System.IO.File.Exists(targetPath) == false)
                {
                    return NotFound();
                }
                if (new FileInfo(targetPath).Length > 2 * 1024 * 1024)
                {
                    return BadRequest("字幕檔過大");
                }
                string vtt = SubtitleConverter.ToWebVtt(System.IO.File.ReadAllBytes(targetPath), fileExt);
                return this.Content(vtt, "text/vtt; charset=utf-8");
            }
            if (config.PlayTypes.Contains(fileExt) == false)
            {
                return this.NotFound(string.Format("{0}　格式不支援{1}目前支援格式為: {2}",
                    fileExt,
                    Environment.NewLine,
                    string.Join(",", config.PlayTypes)));
                //return this.Forbid();
            }
            if (mp3)
            {
                // FLAC 轉好的 MP3 快取（沒轉好就 404，前端會等到狀態變成 ready 才用這個網址）
                string cached = transcodeService.GetReadyFile(targetPath);
                if (cached == null)
                {
                    return NotFound("尚未轉檔完成");
                }
                return this.PhysicalFile(cached, "audio/mpeg", true);
            }
            if (cover)
            {
                try
                {
                    using (TagLib.File tagFile = TagLib.File.Create(targetPath))
                    {
                        TagLib.IPicture pic = tagFile.Tag.Pictures.FirstOrDefault();
                        if (pic != null && pic.Data.Count > 0)
                        {
                            Response.Headers["Cache-Control"] = "private, max-age=86400";
                            return this.File(pic.Data.Data, string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType);
                        }
                    }
                }
                catch
                {
                }
                return NotFound();
            }
            if (preview && (fileExt.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                            fileExt.Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                string text = TextDecoder.Decode(System.IO.File.ReadAllBytes(targetPath));
                if (fileExt.Equals(".md", StringComparison.OrdinalIgnoreCase))
                {
                    return new { type = "md", html = Markdown.ToHtml(text, TextPreviewMarkdownPipeline) };
                }
                return new { type = "txt", text };
            }
            return this.PhysicalFile(targetPath, MimeTypeMap.GetMimeType(fileExt), true);
        }

        /// <summary>
        /// 前端偵測到播放卡住/失敗時回報目前狀態（readyState、緩衝、估算速度…），寫進伺服端日誌，
        /// 這樣不用拿著手機看畫面，也能在伺服器的 log 看到手機端的實際情況。
        /// </summary>
        [HttpPost]
        [Route("api/playdiag")]
        [UserAuthorize]
        public dynamic PlayDiag([FromBody] PlayDiagModel model)
        {
            if (model == null)
            {
                return BadRequest();
            }
            string ua = Request.Headers["User-Agent"];
            string ip = HttpContext.Connection.RemoteIpAddress == null ? "?" : HttpContext.Connection.RemoteIpAddress.ToString();
            PlaybackLogMiddleware.PlaybackLogger.Info("clientdiag {0} {1} [{2}] {3} :: {4}",
                ip,
                PlaybackLogMiddleware.DescribeDevice(ua),
                PlaybackLogMiddleware.Shorten(model.evt, 30),
                PlaybackLogMiddleware.Shorten(model.path, 200),
                PlaybackLogMiddleware.Shorten(model.detail, 1500));
            return new { ok = true };
        }

        /// <summary>
        /// 查詢 FLAC 轉 MP3 的狀態（前端在有「轉檔中」的歌曲時每幾秒呼叫一次）；first 是使用者點了還沒轉好的那首，移到佇列最前面。
        /// </summary>
        [HttpPost]
        [Route("api/transcode/status")]
        [UserAuthorize]
        public dynamic TranscodeStatus([FromBody] TranscodeStatusModel model)
        {
            if (model == null || model.ids == null)
            {
                return BadRequest();
            }
            if (string.IsNullOrEmpty(model.first) == false)
            {
                string firstPath = fimgr.GetPath(model.first);
                if (string.IsNullOrEmpty(firstPath) == false)
                {
                    transcodeService.Prioritize(firstPath);
                }
            }
            Dictionary<string, string> states = new Dictionary<string, string>();
            foreach (string id in model.ids.Take(500))
            {
                string path = fimgr.GetPath(id);
                if (string.IsNullOrEmpty(path) == false && transcodeService.IsSource(path))
                {
                    states[id] = transcodeService.GetState(path);
                }
            }
            return new { states };
        }

        /// <summary>
        /// 動畫資料夾裡第一個 MKV 的編碼資訊（影片 H.264 profile／位元深度、H.265、音訊 AC3／DTS…），前端用它加上瀏覽器的 canPlayType 判斷，
        /// 提示「你的瀏覽器可能無法播放」。只讀檔案前段，結果依「路徑＋大小＋修改時間」快取；資料夾裡沒有 MKV 回傳 file = null。
        /// </summary>
        [HttpGet]
        [Route("api/mediainfo")]
        [UserAuthorize]
        public dynamic MediaInfo([FromQuery] string id)
        {
            string folderPath = string.IsNullOrEmpty(id) ? null : fimgr.GetPath(id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return NotFound();
            }
            FileInfo mkv = new DirectoryInfo(folderPath).EnumerateFiles("*.mkv")
                .Where(f => f.Attributes.HasFlag(FileAttributes.Hidden) == false)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (mkv == null)
            {
                return new { file = (string)null, video = new object[0], audio = new object[0] };
            }
            string cacheKey = string.Format("mkvinfo://{0}|{1}|{2}", mkv.FullName, mkv.Length, mkv.LastWriteTimeUtc.Ticks);
            MkvMediaInfo info = Ioc.GetCache().Get<MkvMediaInfo>(cacheKey);
            if (info == null)
            {
                info = MkvCodecInspector.Inspect(mkv.FullName);
                Ioc.GetCache().Set(cacheKey, info, TimeSpan.FromHours(6));
            }
            return new
            {
                file = mkv.Name,
                video = info.Video.Select(v => new { label = v.Label, mime = v.Mime, always_unsupported = v.AlwaysUnsupported }),
                audio = info.Audio.Select(a => new { label = a.Label, mime = a.Mime, is_default = a.IsDefault })
            };
        }

        /// <summary>
        /// 標註（或取消）動畫資料夾「無法播放」：只有管理者可以，而且只限可評分的動畫資料夾。這只是提醒，不會擋人點進去試播。
        /// </summary>
        [HttpPost]
        [Route("api/unplayable")]
        [UserAuthorize]
        public dynamic SetUnplayable([FromBody] UnplayableInputModel model)
        {
            if (User.IsInRole("Admin") == false)
            {
                return StatusCode(403);
            }
            if (model == null || string.IsNullOrEmpty(model.id))
            {
                return BadRequest("參數錯誤");
            }
            string folderPath = fimgr.GetPath(model.id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return NotFound("找不到這個資料夾");
            }
            if (ratingService.IsRateable(folderPath) == false)
            {
                return BadRequest("這個資料夾不支援標註");
            }
            string key = ratingService.Resolve(new DirectoryInfo(folderPath).Name).Key;
            if (model.value)
            {
                string by = (User.Identity as System.Security.Claims.ClaimsIdentity)?.Label;
                unplayableService.Set(key, string.IsNullOrEmpty(by) ? User.Identity.Name : by);
            }
            else
            {
                unplayableService.Clear(key);
            }
            UnplayableInfo mark = unplayableService.Get(key);
            return new { ok = true, unplayable = mark != null, unplayable_by = mark == null ? null : mark.By, unplayable_time = mark == null ? (long?)null : mark.Time };
        }

        /// <summary>
        /// 管理者看「目前的封面從哪來」（資料夾裡的圖檔，或影片內嵌）；沒有封面檔時 exists 是 false。只限可評分的動畫資料夾。
        /// </summary>
        [HttpGet]
        [Route("api/coverinfo")]
        [UserAuthorize]
        public dynamic CoverInfo([FromQuery] string id)
        {
            if (User.IsInRole("Admin") == false)
            {
                return StatusCode(403);
            }
            string folderPath = string.IsNullOrEmpty(id) ? string.Empty : fimgr.GetPath(id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false || IsCoverFolder(id, folderPath) == false)
            {
                return NotFound();
            }
            WhatSearch.Services.FolderCover cover = Ioc.Get<IFolderCoverService>().Find(folderPath);
            Response.Headers["Cache-Control"] = "no-store";
            return new { exists = cover != null, source = cover == null ? null : cover.Source };
        }

        /// <summary>
        /// 管理者幫動畫資料夾或專輯頁找封面：動畫用名稱到 AniList、Bangumi、Kitsu 搜尋，專輯用專輯名稱與演出者到 iTunes、MusicBrainz 搜尋，
        /// 回傳候選封面（縮圖與大圖網址）。只限可評分的動畫資料夾與專輯頁。
        /// </summary>
        [HttpGet]
        [Route("api/covercandidates")]
        [UserAuthorize]
        public async Task<IActionResult> CoverCandidates([FromQuery] string id, [FromQuery] string q, [FromQuery] string artist)
        {
            if (User.IsInRole("Admin") == false)
            {
                return StatusCode(403);
            }
            string keyword = (q ?? string.Empty).Trim();
            string artistName = (artist ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(id) || keyword.Length == 0 || keyword.Length > 100 || artistName.Length > 100)
            {
                return BadRequest("參數錯誤");
            }
            string folderPath = fimgr.GetPath(id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return NotFound("找不到這個資料夾");
            }
            if (IsCoverFolder(id, folderPath) == false)
            {
                return BadRequest("這個資料夾不支援更換封面");
            }
            try
            {
                // 動畫資料夾搜動畫來源；專輯頁搜音樂來源（專輯名稱＋演出者）
                CoverSearchResult result = ratingService.IsRateable(folderPath)
                    ? await Ioc.Get<ICoverSearchService>().SearchAsync(keyword)
                    : await Ioc.Get<ICoverSearchService>().SearchAlbumAsync(keyword, artistName);
                return Ok(new
                {
                    items = result.Items.Select(t => new { title = t.Title, sub_title = t.SubTitle, year = t.Year, format = t.Format, source = t.Source, thumb = t.Thumb, image = t.Image }),
                    warnings = result.Warnings
                });
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, ex.Message);
            }
        }

        /// <summary>
        /// 管理者把選定的候選封面存成資料夾裡的 cover.jpg（已有就覆蓋；先寫到系統暫存目錄再取代，失敗不會留下半張圖）。
        /// 圖片網址只收各搜尋來源的圖片網域，且內容必須是 JPEG。
        /// </summary>
        [HttpPost]
        [Route("api/coverset")]
        [UserAuthorize]
        public async Task<IActionResult> SetCover([FromBody] CoverSetInputModel model)
        {
            if (User.IsInRole("Admin") == false)
            {
                return StatusCode(403);
            }
            if (model == null || string.IsNullOrEmpty(model.id) || string.IsNullOrEmpty(model.url))
            {
                return BadRequest("參數錯誤");
            }
            string folderPath = fimgr.GetPath(model.id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return NotFound("找不到這個資料夾");
            }
            if (IsCoverFolder(model.id, folderPath) == false)
            {
                return BadRequest("這個資料夾不支援更換封面");
            }
            ICoverSearchService coverSearch = Ioc.Get<ICoverSearchService>();
            if (coverSearch.IsAllowedImageUrl(model.url) == false)
            {
                return BadRequest("不支援的圖片來源");
            }
            byte[] data;
            try
            {
                data = await coverSearch.DownloadJpegAsync(model.url);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(502, ex.Message);
            }
            string target = Path.Combine(folderPath, "cover.jpg");
            // 暫存檔放在系統暫存目錄：放在分享資料夾裡，檔案監看器會先看到它再被改名，白白建一筆索引
            string temp = Path.Combine(Path.GetTempPath(), "uni2share-cover-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                System.IO.File.WriteAllBytes(temp, data);
                System.IO.File.Move(temp, target, true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "寫入 cover.jpg 失敗：" + target);
                try { System.IO.File.Delete(temp); } catch (Exception) { }
                return StatusCode(500, "寫入失敗，請確認資料夾可以寫入");
            }
            logger.Info("更換封面：" + target + "（" + User.Identity.Name + "）");
            return Ok(new { ok = true });
        }

        /// <summary>設定動畫的評分（2.5～5、0.5 為一級，0 = 未評分）；id 是資料夾 id，需登入，且該資料夾必須是可評分的</summary>
        [HttpPost]
        [Route("api/rating")]
        public dynamic SetRating([FromBody] RatingInputModel model)
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            // 0 = 未評分；其餘只收 2.5～5（2 星以下的作品直接刪掉，不評分），以 0.5 為一級
            if (model == null || string.IsNullOrEmpty(model.id) || model.rating < 0 || model.rating > 5 ||
                (model.rating > 0 && model.rating < 2.5) ||
                Math.Abs(model.rating * 2 - Math.Round(model.rating * 2)) > 1e-9)
            {
                return BadRequest("參數錯誤");
            }
            string folderPath = fimgr.GetPath(model.id);
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return NotFound("找不到這個資料夾");
            }
            RatingInfo info;
            if (ratingService.IsRateable(folderPath))
            {
                info = ratingService.Resolve(new DirectoryInfo(folderPath).Name);
            }
            else if (IsAlbumFolder(model.id, folderPath))
            {
                info = ratingService.ResolveAlbum(new DirectoryInfo(folderPath).Name, User.Identity.Name);
            }
            else
            {
                return BadRequest("這個資料夾不支援評分");
            }
            ratingService.Set(info.Key, model.rating, User.Identity.Name);
            return new { ok = true, rating = model.rating };
        }

        /// <summary>目前會員的最愛 id（前端用來標示愛心；不解析檔案，很便宜）</summary>
        [HttpGet]
        [Route("api/favorites")]
        public dynamic GetFavorites()
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            FavoriteData data = favoriteService.Get(User.Identity.Name);
            return new { albums = data.Albums, tracks = data.Tracks };
        }

        /// <summary>加入/移除最愛。kind: album（資料夾 id）或 track（音樂檔 id）</summary>
        [HttpPost]
        [Route("api/favorites")]
        public dynamic SetFavorite([FromBody] FavoriteInputModel model)
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            if (model == null || string.IsNullOrEmpty(model.id) ||
                (model.kind != FavoriteService.KindAlbum && model.kind != FavoriteService.KindTrack))
            {
                return BadRequest("參數錯誤");
            }
            if (model.fav)
            {
                // 只接受存在的資料夾/音樂檔，避免塞進任意字串
                bool ok = model.kind == FavoriteService.KindAlbum
                    ? mainService.GetFolderView(model.id) != null
                    : mainService.GetMusicFileView(model.id) != null;
                if (ok == false)
                {
                    return NotFound("找不到這個項目");
                }
            }
            favoriteService.Set(User.Identity.Name, model.kind, model.id, model.fav);
            if (model.fav && model.kind == FavoriteService.KindTrack)
            {
                // 收藏單曲時就先在背景分析響度，進「我的最愛」時通常已經算好
                loudnessService.Enqueue(fimgr.GetPath(model.id));
            }
            return new { ok = true };
        }

        /// <summary>
        /// 「我的最愛」清單：先列專輯（最近加入的在前），再列歌曲（依加入順序，等於一份虛擬精選歌單）。
        /// 已被改名/刪除而找不到的項目不列出（資料保留，檔案回來就會再出現），只在訊息裡提示筆數。
        /// </summary>
        [HttpGet]
        [Route("api/favorites/items")]
        public dynamic GetFavoriteItems()
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            FavoriteData data = favoriteService.Get(User.Identity.Name);
            List<FileInfoView> items = new List<FileInfoView>();
            int missing = 0;
            foreach (string id in Enumerable.Reverse(data.Albums))
            {
                FileInfoView view = mainService.GetFolderView(id);
                if (view == null) { missing++; } else { items.Add(view); }
            }
            int pending = 0;
            foreach (string id in data.Tracks)
            {
                FileInfoView view = mainService.GetMusicFileView(id);
                if (view == null)
                {
                    missing++;
                    continue;
                }
                string trackPath = fimgr.GetPath(id);
                if (loudnessService.Apply(view, trackPath))
                {
                    pending++;
                }
                if (transcodeService.IsSource(trackPath))
                {
                    view.TranscodeState = transcodeService.GetState(trackPath);
                }
                items.Add(view);
            }
            string message = "我的最愛 " + items.Count + " 筆.";
            if (missing > 0)
            {
                message += "（另有 " + missing + " 筆找不到，可能已改名或刪除）";
            }
            // pending：還在背景分析響度的單曲數；前端據此定時更新。target：目標響度（LUFS）
            return new { message, items, pending, target = loudnessService.TargetLufs };
        }

        [Route("api/info")]
        public dynamic Info()
        {
            return new
            {
                Name = User.Identity.Name,
                Ip = WebHelper.GetClientIp(),
                Host = Environment.MachineName,
                // 是否啟用 FLAC 轉 MP3（前端據此顯示轉檔狀態與「播原檔」選項）
                Transcode = transcodeService.Enabled,
                // 是否為管理者（前端據此顯示「無法播放」註記按鈕）
                Admin = User.Identity.IsAuthenticated && User.IsInRole("Admin"),
                // 管理者還沒看的異常請求事件數（非管理者一律 0）
                Alerts = (User.Identity.IsAuthenticated && User.IsInRole("Admin")) ? abnormalService.GetUnread() : 0,
                // 新上架的未讀數；沒登入或沒有啟用新上架為 null（前端據此決定要不要顯示入口）
                News = (newsService.Enabled && User.Identity.IsAuthenticated) ? (int?)newsService.GetUnread(User.Identity.Name) : null
            };
        }

        /// <summary>
        /// 新上架清單（新到舊，最近 100 筆）。不會標示已讀；unread 是這個會員「上次看之後」才出現的。
        /// folderId 是動畫/專輯資料夾的 id，前端用它直接進入該資料夾。
        /// </summary>
        [HttpGet]
        [Route("api/news")]
        public dynamic GetNews()
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            if (newsService.Enabled == false)
            {
                return new { enabled = false, items = new object[0], unread = 0 };
            }
            long last = newsService.GetLastRead(User.Identity.Name);
            var items = newsService.GetEvents(300).Select(e => new
            {
                id = e.Id,
                share = e.Share,
                kind = e.Kind,
                title = e.Title,
                count = e.Count,
                time = e.Time,
                folderId = fimgr.GetId(e.Path),
                unread = e.Time > last
            }).ToList();
            return new { enabled = true, items, unread = items.Count(t => t.unread) };
        }

        /// <summary>把「新上架」標示成已讀（打開新上架頁時呼叫）</summary>
        [HttpPost]
        [Route("api/news/read")]
        public dynamic ReadNews()
        {
            if (User.Identity.IsAuthenticated == false)
            {
                return Unauthorized();
            }
            newsService.MarkRead(User.Identity.Name);
            return new { ok = true };
        }


        #region input models

        public class PathIdInputModel
        {
            public string pathname { get; set; }
        }

        public class FolderInputModel
        {
            public string p { get; set; }
        }

        public class SearchModel
        {
            public string q { get; set; }
        }

        public class RatingInputModel
        {
            public string id { get; set; }
            public double rating { get; set; }
        }

        public class FavoriteInputModel
        {
            public string kind { get; set; }
            public string id { get; set; }
            public bool fav { get; set; }
        }

        // 專輯頁：位在啟用專輯評分的分享根底下，而且資料夾裡有 2 首以上音樂（或是多片裝的母資料夾）
        /// <summary>可以搜尋／更換封面的資料夾：動畫（可評分）或專輯頁</summary>
        bool IsCoverFolder(string folderId, string folderPath)
        {
            return ratingService.IsRateable(folderPath) || IsAlbumFolder(folderId, folderPath);
        }

        bool IsAlbumFolder(string folderId, string folderPath)
        {
            if (ratingService.IsAlbumShare(folderPath) == false || Directory.Exists(folderPath) == false)
            {
                return false;
            }
            int music = new DirectoryInfo(folderPath).EnumerateFiles()
                .Count(f => f.Attributes.HasFlag(FileAttributes.Hidden) == false && Helper.GetFileDocType(f.Extension) == Helper.ConstStrings.Music);
            return music >= 2 || mainService.GetMultiDiscGroups(folderId) != null;
        }

        public class CoverSetInputModel
        {
            public string id { get; set; }
            public string url { get; set; }
        }

        public class UnplayableInputModel
        {
            public string id { get; set; }
            public bool value { get; set; }
        }

        public class TranscodeStatusModel
        {
            public List<string> ids { get; set; }
            public string first { get; set; }
        }

        public class PlayDiagModel
        {
            public string path { get; set; }
            public string evt { get; set; }
            public string detail { get; set; }
        }

        #endregion
    }
}
