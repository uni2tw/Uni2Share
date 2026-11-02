using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WhatSearch.Utilities;
using WhatSearch.Utility;

namespace WhatSearch.Services
{
    public class FolderCover
    {
        public byte[] Data { get; set; }
        public string MimeType { get; set; }
        /// <summary>封面從哪來（給人看的說明），例如「資料夾裡的 folder.jpg」、「影片內嵌封面：01.mkv」</summary>
        public string Source { get; set; }
    }

    public interface IFolderCoverService
    {
        /// <summary>
        /// 找資料夾的封面圖（只看這一層）：資料夾裡的 cover／folder 圖片檔 → 前幾部影片內嵌的封面
        /// （MP4 等用 ID3／MP4 標籤，MKV 讀附件裡的圖片）。都沒有回傳 null。
        /// </summary>
        FolderCover Find(string folderPath);
    }

    public class FolderCoverService : IFolderCoverService
    {
        // 與前端 vm_findCoverUrl 一致，順序就是優先順序
        static readonly string[] CoverFileNames = { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.jpeg", "folder.png" };
        const int MaxImageBytes = 8 * 1024 * 1024;
        const int MaxVideosToTry = 3;

        public FolderCover Find(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || Directory.Exists(folderPath) == false)
            {
                return null;
            }
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folderPath).ToList();
            }
            catch (Exception)
            {
                return null;
            }

            foreach (string name in CoverFileNames)
            {
                string file = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
                if (file == null)
                {
                    continue;
                }
                byte[] data = ReadImageFile(file);
                if (data != null && SniffImageMime(data) != null)
                {
                    return new FolderCover { Data = data, MimeType = SniffImageMime(data), Source = "資料夾裡的 " + Path.GetFileName(file) };
                }
            }

            IEnumerable<string> videos = files
                .Where(f => Helper.GetFileDocType(Path.GetExtension(f)) == Helper.ConstStrings.Video)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Take(MaxVideosToTry);
            foreach (string video in videos)
            {
                FolderCover cover = ReadEmbeddedCover(video);
                if (cover != null)
                {
                    return cover;
                }
            }
            return null;
        }

        static byte[] ReadImageFile(string file)
        {
            try
            {
                FileInfo fi = new FileInfo(file);
                if (fi.Length == 0 || fi.Length > MaxImageBytes)
                {
                    return null;
                }
                return File.ReadAllBytes(file);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static FolderCover ReadEmbeddedCover(string videoFile)
        {
            try
            {
                using (TagLib.File tagFile = TagLib.File.Create(videoFile))
                {
                    // MKV 的附件（含字型檔）TagLib 都會當成「圖片」回傳，所以一定要看檔頭確認真的是圖片，再依檔名挑 cover
                    TagLib.IPicture pic = tagFile.Tag.Pictures
                        .Where(p => p.Data != null && p.Data.Count > 0 && p.Data.Count <= MaxImageBytes && SniffImageMime(p.Data.Data) != null)
                        .OrderBy(p => MatroskaSubtitleReader.CoverRank(string.IsNullOrEmpty(p.Filename) ? p.Description : p.Filename))
                        .FirstOrDefault();
                    if (pic != null)
                    {
                        byte[] data = pic.Data.Data;
                        string attach = string.IsNullOrEmpty(pic.Filename) ? string.Empty : "，附件 " + pic.Filename;
                        return new FolderCover { Data = data, MimeType = SniffImageMime(data), Source = "影片內嵌封面：" + Path.GetFileName(videoFile) + attach };
                    }
                }
            }
            catch (Exception)
            {
                // 讀不了標籤（或格式 TagLib 不認得）就試下一種
            }

            string ext = Path.GetExtension(videoFile);
            if (string.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".webm", StringComparison.OrdinalIgnoreCase))
            {
                string mime;
                byte[] data = MatroskaSubtitleReader.ReadCoverAttachment(videoFile, out mime);
                if (data != null && SniffImageMime(data) != null)
                {
                    return new FolderCover { Data = data, MimeType = SniffImageMime(data), Source = "影片內嵌封面（MKV 附件）：" + Path.GetFileName(videoFile) };
                }
            }
            return null;
        }

        // 以檔案開頭的位元組判斷是不是圖片（PNG、JPEG、GIF、WebP）：標籤裡寫的 MIME 常常是空的或不對，
        // MKV 的附件也可能是字型檔，所以一律看內容；不是圖片回傳 null
        static string SniffImageMime(byte[] data)
        {
            if (data == null)
            {
                return null;
            }
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return "image/png";
            }
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return "image/jpeg";
            }
            if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                return "image/gif";
            }
            if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
            {
                return "image/webp";
            }
            return null;
        }
    }
}
