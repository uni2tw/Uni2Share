using WhatSearch.Core;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WhatSearch.Jobs;
using System;
using NLog;
using System.Diagnostics;
using System.Threading;

namespace WhatSearch.Services
{
    public class SimpleDocumentService : DocumentServiceBase
    {
        static ILogger logger = LogManager.GetCurrentClassLogger();
        public override string GetProgressPercent()
        {
            return string.Empty;
        }

        public override void Start(List<FolderConfig> startFolders)
        {
            Stopwatch total = Stopwatch.StartNew();
            int remaining = startFolders.Count;
            foreach (var folder in startFolders)
            {
                string folderPath = folder.Path;
                Task.Run(() =>
                {
                    Stopwatch root = Stopwatch.StartNew();
                    try
                    {
                        StartSeek(folderPath);
                    }
                    finally
                    {
                        logger.Info("索引掃描：根目錄 {0} 掃完，耗時 {1:F1} 秒", folderPath, root.Elapsed.TotalSeconds);
                        if (Interlocked.Decrement(ref remaining) == 0)
                        {
                            LogScanSummary(total);
                        }
                    }
                });
            }
        }

        private void SeekFile(string filePath)
        {
            BuildIndex(new FileInfo(filePath));
        }

        private void StartSeek(string folder)
        {
            TriggerSeekFolderStart(folder);
            var dirInfo = new DirectoryInfo(folder);
            if (dirInfo.Exists == false)
            {
                return;
            }
            Stopwatch walk = Stopwatch.StartNew();
            DirectoryInfo[] rootSubs = dirInfo.GetDirectories();
            AddWalkTicks(walk.ElapsedTicks);
            foreach (var subDirInfo in rootSubs)
            {
                SeekFolder(subDirInfo.FullName);
            }
            BuildSearchDoc(dirInfo);
            TriggerSeekFolderDone(folder);
        }

        private void SeekFolder(string folderPath)
        {
            TriggerSeekFolderStart(folderPath);
            DirectoryInfo dirInfo = null;
            DirectoryInfo[] subDirInfos = new DirectoryInfo[0];
            try
            {
                dirInfo = new DirectoryInfo(folderPath);
                if (dirInfo.Exists == false)
                {
                    return;
                }
                Stopwatch walk = Stopwatch.StartNew();
                subDirInfos = dirInfo.GetDirectories();
                AddWalkTicks(walk.ElapsedTicks);
            }
            catch (Exception ex)
            {
                logger.Info("SeekFolder遇到錯誤，重新排訂:" + folderPath, ex);
                Ioc.Get<IReseekFolderJob>().Queue(folderPath);
                return;
            }
                
            foreach (var subDirInfo in subDirInfos)
            {
                SeekFolder(subDirInfo.FullName);
            }
            BuildSearchDoc(dirInfo);

            TriggerSeekFolderDone(folderPath);
        }

        private void RemoveFile(string filePath)
        {
            RemoveSearchDoc(filePath);
        }

        private void RemoveFolder(string dirPath)
        {
            search.RemoveFolder(dirPath);
        }


        public override void AppendFolderOrFile(string fullPath)
        {
            if (Directory.Exists(fullPath))
            {
                SeekFolder(fullPath);
            } else
            {
                SeekFile(fullPath);
            }
        }

        public override void RemoveFolderOrFile(string fullPath)
        {
            // 刪除、改名事件送來的路徑「已經不存在」，所以沒辦法用 Directory.Exists 判斷它原本是檔案還是資料夾
            // （以前一律當成檔案，結果資料夾底下檔案的索引都留著，改名後同一個檔案會出現新舊兩份）。
            // 兩種都清：檔案精確比對，資料夾依路徑前綴整批刪除（含子資料夾）；對檔案路徑，前綴那一步不會比對到任何東西
            RemoveFile(fullPath);
            RemoveFolder(fullPath);
        }
    }
}
