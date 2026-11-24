using System;
using System.IO;
using System.Linq;

namespace WhatSearch.Services
{
    /// <summary>
    /// 隱藏資料夾：名稱以 . 或 $ 開頭的資料夾（例如 .backup）。列表不列出、搜尋不建索引、監看與新上架不處理。
    /// 用途是放備份檔，例如轉檔前的原檔。只是「不列出」，知道網址的人仍然打得開。
    /// </summary>
    public static class HiddenFolder
    {
        /// <summary>系統自動備份（例如 MKV 轉檔前的原檔）固定放的資料夾名稱</summary>
        public const string BackupName = ".backup";

        public static bool IsHiddenName(string name)
        {
            return string.IsNullOrEmpty(name) == false && (name[0] == '.' || name[0] == '$');
        }

        /// <summary>
        /// 這個路徑在隱藏資料夾裡面，或它本身就是隱藏資料夾。
        /// 只看分享根底下的路徑（分享根本身的上層路徑有 . 或 $ 開頭的資料夾不算）；檔案本身的名稱不算，所以 .gitignore 這類檔案不受影響
        /// </summary>
        public static bool IsHiddenPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return false;
            }
            string rel;
            if (PathUtility.TryGetRelPath(fullPath, out rel) == false)
            {
                return false;
            }
            // segs[0] 是分享根的名稱；最後一段是檔案或資料夾本身，只有它是資料夾時才檢查
            string[] segs = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < segs.Length - 1; i++)
            {
                if (IsHiddenName(segs[i]))
                {
                    return true;
                }
            }
            return segs.Length > 1 && IsHiddenName(segs[segs.Length - 1]) && Directory.Exists(fullPath);
        }
    }
}
