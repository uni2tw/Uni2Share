using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using WhatSearch.Core;
using WhatSearch.Models;
using WhatSearch.Services.Interfaces;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public class MainService : IMainService
    {        
        IFolderIdManager fimgr = Ioc.Get<IFolderIdManager>();
        ITranscodeService transcodeService = Ioc.Get<ITranscodeService>();
        INewsService newsService = Ioc.Get<INewsService>();
        SystemConfig config = Ioc.GetConfig();

        public List<FileInfoView> GetFileInfoViewsInTheFolder(string folderId)
        {
            string path = fimgr.GetPath(folderId);

            List<FileInfoView> result = new List<FileInfoView>();
            if (string.IsNullOrEmpty(path))
            {
                return result;
            }
            DirectoryInfo dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists == false)
            {
                return result;
            }

            foreach (var subDirInfo in dirInfo.GetDirectories())
            {
                string efid = fimgr.GetIdByFilePath(subDirInfo.FullName);
                result.Add(new FileInfoView
                {
                    Id = efid,
                    Title = subDirInfo.Name,
                    Modify = subDirInfo.LastWriteTime.ToString(),
                    ModifyTs = new DateTimeOffset(subDirInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Type = Helper.ConstStrings.Folder,
                    Size = string.Empty
                });
            }
            List<Tuple<FileInfoView, FileInfo>> videoJobs = new List<Tuple<FileInfoView, FileInfo>>();
            List<string> flacPaths = new List<string>();
            foreach (var subFileInfo in dirInfo.GetFiles())
            {
                if (subFileInfo.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }
                string subEfid = fimgr.GetIdByFilePath(subFileInfo.FullName);
                string relPath;
                if (PathUtility.TryGetRelPath(subFileInfo.FullName, out relPath) == false)
                {
                    throw new Exception("不預期的意外，" + subFileInfo.FullName);
                }
                FileInfoView fileView = new FileInfoView
                {
                    Id = subEfid,
                    GetUrl = PathUtility.ToGetUrl(relPath),
                    Title = subFileInfo.Name,
                    Modify = subFileInfo.LastWriteTime.ToString(),
                    ModifyTs = new DateTimeOffset(subFileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    Type = Helper.GetFileDocType(subFileInfo.Extension),
                    Size = Helper.GetReadableByteSize(subFileInfo.Length),
                    Bytes = subFileInfo.Length
                };
                ApplyMusicTags(fileView, subFileInfo.FullName);
                if (fileView.Type != Helper.ConstStrings.Video && fileView.Type != Helper.ConstStrings.Music &&
                    fileView.Type != Helper.ConstStrings.Image && fileView.Type != Helper.ConstStrings.Text &&
                    config.PlayTypes.Contains(subFileInfo.Extension) == false)
                {
                    fileView.Unsupported = true;
                }
                if (fileView.Type == Helper.ConstStrings.Video)
                {
                    videoJobs.Add(Tuple.Create(fileView, subFileInfo));
                }
                if (transcodeService.IsSource(subFileInfo.FullName))
                {
                    flacPaths.Add(subFileInfo.FullName);
                }
                result.Add(fileView);
            }
            ApplyVideoDurations(videoJobs);
            ApplyNewsBadges(result, dirInfo);
            // 進入含 FLAC 的資料夾：整個資料夾的 FLAC 依檔名順序排進背景轉檔，並把目前狀態填進每一首
            if (flacPaths.Count > 0 && transcodeService.Enabled)
            {
                transcodeService.EnqueueFolder(flacPaths);
                foreach (FileInfoView v in result)
                {
                    string full = v.Type == Helper.ConstStrings.Music && v.GetUrl != null ? flacPaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), v.Title, StringComparison.OrdinalIgnoreCase)) : null;
                    if (full != null)
                    {
                        v.TranscodeState = transcodeService.GetState(full);
                    }
                }
            }

            return result;
        }

        // 新上架的「新」標記：子資料夾（有通知的作品／專輯）與新增的集數檔案；只標 badgeDays 內的
        void ApplyNewsBadges(List<FileInfoView> views, DirectoryInfo dirInfo)
        {
            string folderRel;
            if (newsService.Enabled == false || PathUtility.TryGetRelPath(dirInfo.FullName, out folderRel) == false)
            {
                return;
            }
            NewsBadges badges = newsService.GetBadges(folderRel);
            if (badges.Folders.Count == 0 && badges.Files.Count == 0)
            {
                return;
            }
            foreach (FileInfoView v in views)
            {
                if (v.Type == Helper.ConstStrings.Folder)
                {
                    NewsBadge b;
                    if (badges.Folders.TryGetValue(v.Title, out b))
                    {
                        v.NewsKind = b.Kind;
                        v.NewsCount = b.Count;
                    }
                }
                else if (badges.Files.Contains(v.Title))
                {
                    v.NewsKind = "file";
                }
            }
        }

        const int MaxDurationReadsPerListing = 50;
        const int DurationReadParallelism = 4;

        class DurationEntry
        {
            public double? Seconds { get; set; }
        }

        /// <summary>
        /// 讀取影片長度（秒）：同一次列表最多讀前 50 部、平行 4 個；結果依「路徑＋大小＋修改時間」快取。
        /// 讀不到（格式不支援、檔案損毀或正在寫入）就是 null，不影響列表。
        /// </summary>
        private static void ApplyVideoDurations(List<Tuple<FileInfoView, FileInfo>> jobs)
        {
            if (jobs.Count == 0)
            {
                return;
            }
            Parallel.ForEach(jobs.Take(MaxDurationReadsPerListing),
                new ParallelOptions { MaxDegreeOfParallelism = DurationReadParallelism },
                job => { job.Item1.Duration = GetVideoDuration(job.Item2); });
        }

        private static double? GetVideoDuration(FileInfo fi)
        {
            MemoryCache cache = Ioc.GetCache();
            string key = string.Format("duration://{0}|{1}|{2}", fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks);
            DurationEntry entry = cache.Get<DurationEntry>(key);
            if (entry != null)
            {
                return entry.Seconds;
            }
            double? seconds = null;
            try
            {
                if (fi.Extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    seconds = MatroskaSubtitleReader.GetDurationSeconds(fi.FullName);
                }
                if (seconds == null)
                {
                    using (TagLib.File tagFile = TagLib.File.Create(fi.FullName))
                    {
                        double s = tagFile.Properties.Duration.TotalSeconds;
                        if (s > 0)
                        {
                            seconds = s;
                        }
                    }
                }
            }
            catch
            {
            }
            cache.Set(key, new DurationEntry { Seconds = seconds },
                seconds.HasValue ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(2));
            return seconds;
        }

        /// <summary>
        /// 讀取 mp3 的 ID3 tag（歌手/專輯），讀不到就略過不影響列表顯示
        /// </summary>
        class MusicTagEntry
        {
            public string Artist { get; set; }
            public string Album { get; set; }
            public string TrackTitle { get; set; }
            public string AlbumArtist { get; set; }
            public uint? Track { get; set; }
            public uint? Year { get; set; }
            public double? Duration { get; set; }
            public bool IsEmpty { get; set; }
        }

        /// <summary>讀音樂標籤並套用到項目；標籤依「路徑＋大小＋修改時間」快取，重複進入同一資料夾不用再開檔</summary>
        private static void ApplyMusicTags(FileInfoView view, string fullPath)
        {
            if (view.Type != Helper.ConstStrings.Music)
            {
                return;
            }
            MusicTagEntry entry;
            try
            {
                FileInfo fi = new FileInfo(fullPath);
                MemoryCache cache = Ioc.GetCache();
                string key = string.Format("tags://{0}|{1}|{2}", fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks);
                entry = cache.Get<MusicTagEntry>(key);
                if (entry == null)
                {
                    entry = ReadMusicTags(fullPath);
                    cache.Set(key, entry, entry.IsEmpty ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(12));
                }
            }
            catch
            {
                return;
            }
            view.Artist = entry.Artist;
            view.Album = entry.Album;
            view.TrackTitle = entry.TrackTitle;
            view.AlbumArtist = entry.AlbumArtist;
            view.Track = entry.Track;
            view.Year = entry.Year;
            if (entry.Duration.HasValue)
            {
                view.Duration = entry.Duration;
            }
        }

        private static MusicTagEntry ReadMusicTags(string fullPath)
        {
            MusicTagEntry e = new MusicTagEntry { IsEmpty = true };
            try
            {
                using (TagLib.File tagFile = TagLib.File.Create(fullPath))
                {
                    TagLib.Tag tag = tagFile.Tag;
                    // 很多中文 MP3 把 Big5 / GBK 的位元組放在標示為 Latin-1 的標籤欄位，TagLib 解出來是亂碼；
                    // 這裡把同一首歌的文字欄位一起偵測編碼並修正（本來就正確的不會動）
                    string[] text = TagTextFixer.Fix(tag.JoinedPerformers, tag.Album, tag.Title, tag.FirstAlbumArtist);
                    if (string.IsNullOrEmpty(text[0]) == false)
                    {
                        e.Artist = text[0];
                    }
                    if (string.IsNullOrEmpty(text[1]) == false)
                    {
                        e.Album = text[1];
                    }
                    if (string.IsNullOrEmpty(text[2]) == false)
                    {
                        e.TrackTitle = text[2];
                    }
                    if (tag.Track > 0)
                    {
                        e.Track = tag.Track;
                    }
                    if (tag.Year > 0)
                    {
                        e.Year = tag.Year;
                    }
                    if (string.IsNullOrEmpty(text[3]) == false)
                    {
                        e.AlbumArtist = text[3];
                    }
                    double seconds = tagFile.Properties.Duration.TotalSeconds;
                    if (seconds > 0)
                    {
                        e.Duration = seconds;
                    }
                    e.IsEmpty = false;
                }
            }
            catch
            {
            }
            return e;
        }

        // 光碟資料夾名稱：Disc 1 / Disk2 / CD1 / CD 2 / DVD1 / 碟1 / 第2碟，後面可接說明（如 "Disc 1 - 白天"）
        internal static readonly Regex DiscNameRegex = new Regex(
            @"^\s*(?:(?:disc|disk|cd|dvd)\s*[-_.]?\s*(\d{1,2})|碟\s*(\d{1,2})|第\s*(\d{1,2})\s*[碟盤片])(?![0-9A-Za-z])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // 多片裝專輯常見的附帶資料夾，不影響判斷
        internal static readonly Regex AuxFolderRegex = new Regex(@"^(?:scans?|artworks?|booklets?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const int MaxDiscFolders = 30;

        /// <summary>
        /// 多片裝專輯：子資料夾（扣掉 Scans/Artwork/Booklet）全部是光碟資料夾且至少 2 個、
        /// 母資料夾沒有影片或音樂檔、各光碟合計 >= 3 首，就回傳各光碟的歌曲；否則 null。
        /// </summary>
        public List<DiscGroup> GetMultiDiscGroups(string folderId)
        {
            string path = fimgr.GetPath(folderId);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            DirectoryInfo dir = new DirectoryInfo(path);
            if (dir.Exists == false)
            {
                return null;
            }

            List<Tuple<int, DirectoryInfo>> discs = new List<Tuple<int, DirectoryInfo>>();
            foreach (DirectoryInfo sub in dir.GetDirectories())
            {
                if (sub.Attributes.HasFlag(FileAttributes.Hidden) || AuxFolderRegex.IsMatch(sub.Name))
                {
                    continue;
                }
                Match m = DiscNameRegex.Match(sub.Name);
                if (m.Success == false)
                {
                    return null;
                }
                string num = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                discs.Add(Tuple.Create(int.Parse(num), sub));
            }
            if (discs.Count < 2 || discs.Count > MaxDiscFolders)
            {
                return null;
            }
            foreach (FileInfo f in dir.GetFiles())
            {
                if (f.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }
                string type = Helper.GetFileDocType(f.Extension);
                if (type == Helper.ConstStrings.Video || type == Helper.ConstStrings.Music)
                {
                    return null;
                }
            }

            List<DiscGroup> result = new List<DiscGroup>();
            foreach (var d in discs.OrderBy(x => x.Item1).ThenBy(x => x.Item2.Name, StringComparer.OrdinalIgnoreCase))
            {
                string discId = fimgr.GetIdByFilePath(d.Item2.FullName);
                List<FileInfoView> tracks = GetFileInfoViewsInTheFolder(discId)
                    .Where(t => t.Type == Helper.ConstStrings.Music).ToList();
                result.Add(new DiscGroup { Id = discId, No = d.Item1, Title = d.Item2.Name, Items = tracks });
            }
            return result.Sum(g => g.Items.Count) >= 3 ? result : null;
        }

        public List<FileInfoView> GetRootShareFolders()
        {
            List<FileInfoView> result = new List<FileInfoView>();
            foreach (var folder in config.Folders)
            {
                DirectoryInfo di = new DirectoryInfo(folder.Path);
                if (di.Exists == false)
                {
                    continue;
                }                
                result.Add(GetFileInfoView(di, folder.Title));
            }
            return result;
        }

        public List<FileInfoView> GetBreadcrumbs(string efid)
        {
            List<FileInfoView> result = new List<FileInfoView>();

            if (efid == string.Empty)
            {
                result.Add(new FileInfoView
                {
                    Id = string.Empty,
                    Title = "我的分享",
                    Type = "檔案資料夾",
                });
                return result;
            }

            string dirPath = fimgr.GetPath(efid);
            if (string.IsNullOrEmpty(dirPath))
            {
                return result;
            }
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            do
            {
                if (dirInfo == null)
                {
                    break;
                }
                FileInfoView crumb = GetFileInfoView(dirInfo);
                if (crumb != null)
                {
                    result.Add(crumb);
                }
                bool atTop = config.Folders.Any(t => t.Path.Equals(dirInfo.FullName, StringComparison.OrdinalIgnoreCase));
                if (atTop)
                {
                    break;
                }
                dirInfo = dirInfo.Parent;
            } while (true);
            result.Add(new FileInfoView
            {
                Id = string.Empty,
                Title = "我的分享",
                Type = "檔案資料夾",
            });
            result.Reverse();
            return result;
        }

        const int MaxSuggestions = 5;

        /// <summary>
        /// 資料夾不存在（例如改過名）時：找出最近一層還存在的上層，並在該層的兄弟資料夾裡依名稱相似度建議。
        /// 建議的資料夾若後面還有原本路徑的子層且名稱完全對得上，會一併帶過去。資料夾存在則回傳 null。
        /// </summary>
        public FolderNotFoundResult ResolveMissingFolder(string folderId)
        {
            string relPath = Helper.DecodeUrlBase64(folderId);
            string[] segs = (relPath ?? string.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0)
            {
                return null;
            }
            FolderNotFoundResult result = new FolderNotFoundResult
            {
                RequestedPath = string.Join("/", segs),
                NearestId = string.Empty,
                NearestTitle = "我的分享",
                Suggestions = new List<FolderSuggestion>()
            };

            FolderConfig share = config.Folders.FirstOrDefault(f => f.Title.Equals(segs[0], StringComparison.OrdinalIgnoreCase));
            if (share == null)
            {
                // 連分享根名稱都不存在：在所有分享根之間找相近的
                foreach (FolderConfig f in config.Folders)
                {
                    double score = NameSimilarity.Score(segs[0], f.Title);
                    if (score >= NameSimilarity.SuggestThreshold)
                    {
                        result.Suggestions.Add(new FolderSuggestion
                        {
                            Id = fimgr.GetIdByFilePath(f.Path),
                            Title = f.Title,
                            Score = score
                        });
                    }
                }
                result.Suggestions = result.Suggestions.OrderByDescending(x => x.Score).Take(MaxSuggestions).ToList();
                return result;
            }

            DirectoryInfo current = new DirectoryInfo(share.Path);
            if (current.Exists == false)
            {
                return result;
            }
            for (int i = 1; i < segs.Length; i++)
            {
                string next = Path.Combine(current.FullName, segs[i]);
                if (Directory.Exists(next))
                {
                    current = new DirectoryInfo(next);
                    continue;
                }

                // 缺的是第 i 層：最近存在的上層就是 current，只比同一層的兄弟資料夾
                result.NearestId = fimgr.GetIdByFilePath(current.FullName);
                result.NearestTitle = current.FullName.Equals(share.Path, StringComparison.OrdinalIgnoreCase)
                    ? share.Title : current.Name;
                string[] remaining = segs.Skip(i + 1).ToArray();
                foreach (DirectoryInfo sibling in current.GetDirectories())
                {
                    if (sibling.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }
                    double score = NameSimilarity.Score(segs[i], sibling.Name);
                    if (score < NameSimilarity.SuggestThreshold)
                    {
                        continue;
                    }
                    // 原本路徑後面的子層若在建議的資料夾底下一樣存在，就一併帶過去
                    DirectoryInfo target = sibling;
                    List<string> names = new List<string> { sibling.Name };
                    foreach (string rest in remaining)
                    {
                        string deeper = Path.Combine(target.FullName, rest);
                        if (Directory.Exists(deeper) == false)
                        {
                            break;
                        }
                        target = new DirectoryInfo(deeper);
                        names.Add(target.Name);
                    }
                    result.Suggestions.Add(new FolderSuggestion
                    {
                        Id = fimgr.GetIdByFilePath(target.FullName),
                        Title = string.Join(" / ", names),
                        Score = score
                    });
                }
                result.Suggestions = result.Suggestions.OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).Take(MaxSuggestions).ToList();
                return result;
            }
            return null;
        }

        public FileInfoView GetFileInfoView(DirectoryInfo di, string preferenceTitle = null)
        {
            if (di == null || di.Exists == false)
            {
                return null;
            }
            string efid = fimgr.GetIdByFilePath(di.FullName);

            return new FileInfoView
            {
                Id = efid,
                Title = preferenceTitle ?? di.Name,
                Modify = di.CreationTime.ToString(),
                Type = Helper.ConstStrings.Folder,
                Size = string.Empty
            };
        }


        public FileInfoView GetFolderView(string folderId)
        {
            string path = fimgr.GetPath(folderId);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            DirectoryInfo di = new DirectoryInfo(path);
            FileInfoView view = GetFileInfoView(di);
            if (view == null)
            {
                return null;
            }
            string parentRel = string.Empty;
            if (di.Parent != null)
            {
                PathUtility.TryGetRelPath(di.Parent.FullName, out parentRel);
            }
            view.Path = parentRel;
            return view;
        }

        public FileInfoView GetMusicFileView(string fileId)
        {
            string path = fimgr.GetPath(fileId);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            FileInfo fi = new FileInfo(path);
            if (fi.Exists == false || Helper.GetFileDocType(fi.Extension) != Helper.ConstStrings.Music)
            {
                return null;
            }
            string relPath;
            if (PathUtility.TryGetRelPath(fi.FullName, out relPath) == false)
            {
                return null;
            }
            string dirRel;
            PathUtility.TryGetRelPath(fi.DirectoryName, out dirRel);
            FileInfoView view = new FileInfoView
            {
                Id = fileId,
                GetUrl = PathUtility.ToGetUrl(relPath),
                Title = fi.Name,
                Modify = fi.LastWriteTime.ToString(),
                Type = Helper.ConstStrings.Music,
                Size = Helper.GetReadableByteSize(fi.Length),
                Bytes = fi.Length,
                Path = dirRel
            };
            ApplyMusicTags(view, fi.FullName);
            return view;
        }

        public List<FileInfoView> GetMusicItems(string folderId)
        {
            string path = fimgr.GetPath(folderId);

            List<FileInfoView> result = new List<FileInfoView>();
            if (string.IsNullOrEmpty(path))
            {
                return result;
            }
            DirectoryInfo dirInfo = new DirectoryInfo(path);
            if (dirInfo.Exists == false)
            {
                return result;
            }

            foreach (var fi in dirInfo.GetFiles())
            {
                if (fi.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    continue;
                }
                if (Helper.GetFileDocType(fi.Extension) != Helper.ConstStrings.Music)
                {
                    continue;
                }
                string subEfid = fimgr.GetIdByFilePath(fi.FullName);
                string relPath;
                if (PathUtility.TryGetRelPath(fi.FullName, out relPath) == false)
                {
                    throw new Exception("不預期的意外，" + fi.FullName);
                }
                FileInfoView fileView = new FileInfoView
                {
                    Id = subEfid,
                    GetUrl = PathUtility.ToGetUrl(relPath),
                    Title = fi.Name,
                    Modify = fi.LastWriteTime.ToString(),
                    Type = Helper.GetFileDocType(fi.Extension),
                    Size = Helper.GetReadableByteSize(fi.Length)
                };
                ApplyMusicTags(fileView, fi.FullName);
                result.Add(fileView);
            }

            return result;
        }
    }
}
