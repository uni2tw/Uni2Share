using Microsoft.AspNetCore.Mvc.Routing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WhatSearch.Core;
using WhatSearch.Models;

namespace WhatSearch.Services
{
    public class PathUtility
    {
        static SystemConfig config = Ioc.GetConfig();

        public static string ToGetUrl(string relPath)
        {
            string[] segments = (relPath ?? string.Empty).Split('/');
            return "/get" + string.Join("/", segments.Select(Uri.EscapeDataString));
        }

        public static string GetRelativePath(List<FileInfoView> breadcrumbs)
        {
            if (breadcrumbs.Count <= 1)
            {
                return "/";
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                sb.Append('/');
                for (int i = 1; i < breadcrumbs.Count - 1; i++)
                {
                    sb.Append(breadcrumbs[i].Title);
                    sb.Append('/');
                }
                sb.Append(breadcrumbs[breadcrumbs.Count - 1]);
                return sb.ToString();
            }
        }

        public static string GetRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/page/";
            }
            string relPath;
            if (TryGetRelPath(path, out relPath))
            {                
                return "/page/" + relPath.TrimStart('/');
            }
            return "/page/";
        }

        public static bool TryGetRelPath(string absPath, out string relPath)
        {            
            relPath = string.Empty;
            FolderConfig targetFolder = null;
            foreach (var sf in config.Folders)
            {
                string sfPath = sf.Path;
                if (sf.Path == absPath)
                {
                    relPath = "/" + sf.Title;
                    return true;
                }
                if (absPath.StartsWith(sfPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetFolder = sf;
                    break;
                }
            }

            if (targetFolder == null)
            {
                return false;
            }
            relPath = absPath.Substring(targetFolder.Path.Length);
            if (relPath == string.Empty)
            {
                relPath = "/";
            }
            else
            {
                relPath = relPath.Replace(Path.DirectorySeparatorChar, '/');
                relPath = "/" + targetFolder.Title + relPath;
            }


            return true;
        }
        /// <summary>
        /// 跟 configFolder比對的部份有問題
        /// /H-Anime123 也會被判斷符合
        /// 要改成先拆會一個一個 folder 再針對第1個folder做比對
        /// </summary>
        /// <param name="relPath"></param>
        /// <returns></returns>
        public static bool TryGetAbsolutePath(string relPath, out string absolutePath)
        {
            //~/Anime/2018連載-3/高分少女
            absolutePath = string.Empty;
            if (string.IsNullOrEmpty(relPath))
            {
                return true;
            }
            FolderConfig targetFolder = null;
            string[] relPathParts = relPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            if (relPathParts.Length == 0)
            {
                return false;
            }
            string topRelPath = relPathParts[0];
            string[] bottomRelPaths = relPathParts.Skip(1).ToArray();
            foreach (var sf in config.Folders)
            {
                string shareRelPath = sf.Title;
                if (topRelPath.Equals(shareRelPath, StringComparison.OrdinalIgnoreCase))
                {
                    targetFolder = sf;
                    break;
                }
            }
            if (targetFolder == null)
            {
                return false;
            }
            absolutePath = Path.Combine(
                targetFolder.Path,
                string.Join(Path.DirectorySeparatorChar, bottomRelPaths));
            return true;
        }
    }
}
