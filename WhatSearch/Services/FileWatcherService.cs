using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using WhatSearch.Core;

namespace WhatSearch
{
    public class FileWatcherService : IFileWatcherService
    {
        static IDocumentService seekService = Ioc.Get<IDocumentService>();
        static ILogger logger = LogManager.GetCurrentClassLogger();
        static List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        public void Start(List<FolderConfig> shareFolders)
        {
            foreach (var shareFolder in shareFolders)
            {
                try
                {
                    FileSystemWatcher watcher = new FileSystemWatcher(shareFolder.Path);
                    watcher.IncludeSubdirectories = true;
                    // 事件在背景執行緒觸發，沒接住的例外會讓整個程式結束（例如新檔案在建索引前就被改名或刪除）
                    watcher.Renamed += delegate (object sender, RenamedEventArgs e)
                    {
                        try
                        {
                            seekService.RemoveFolderOrFile(e.OldFullPath);
                            seekService.AppendFolderOrFile(e.FullPath);
                            logger.Info("{0}: {1}, {2}", e.ChangeType, e.Name, e.FullPath);
                        }
                        catch (Exception ex)
                        {
                            logger.Warn(ex, "處理檔案異動失敗：{0}", e.FullPath);
                        }
                    };
                    watcher.Created += delegate (object sender, FileSystemEventArgs e)
                    {
                        try
                        {
                            seekService.AppendFolderOrFile(e.FullPath);
                            logger.Info("{0}: {1}", e.ChangeType, e.FullPath);
                        }
                        catch (Exception ex)
                        {
                            logger.Warn(ex, "處理檔案異動失敗：{0}", e.FullPath);
                        }
                    };
                    watcher.Deleted += delegate (object sender, FileSystemEventArgs e)
                    {
                        try
                        {
                            seekService.RemoveFolderOrFile(e.FullPath);
                            logger.Info("{0}: {1}", e.ChangeType, e.FullPath);
                        }
                        catch (Exception ex)
                        {
                            logger.Warn(ex, "處理檔案異動失敗：{0}", e.FullPath);
                        }
                    };
                    watcher.EnableRaisingEvents = true;
                    watchers.Add(watcher);
                }
                catch (Exception)
                {
                    Console.WriteLine($"Path {shareFolder} not found");
                }
            }
        }

        public void Stop()
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }
}