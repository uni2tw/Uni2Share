using WhatSearch.Core;
using WhatSearch.Service;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using NLog;

namespace WhatSearch
{
    public abstract class DocumentServiceBase : IDocumentService
    {
        Action<string> taskQueuedCallback;
        Action<string> taskDoneCallback;
        Action<int> allDoneCallback;

        private int _progressMaxinum;
        private int _progressValue;
        public abstract string GetProgressPercent();

        protected ISearchSercice search = Ioc.Get<ISearchSercice>();        

        static ILogger scanLogger = LogManager.GetCurrentClassLogger();

        // 啟動掃描的統計（只用來寫日誌，評估索引要不要改成持久化／少 commit）。
        // 多個分享根目錄同時掃，所以「累計時間」是各執行緒時間加總，可能比總耗時大
        long _scanWalkTicks;   // 走訪檔案系統（GetDirectories／GetFiles）
        long _scanBuildTicks;  // 寫入索引（簡繁轉換、UpdateDocument、commit、refresh）
        long _scanDirs;
        long _scanFiles;

        protected void AddWalkTicks(long ticks)
        {
            Interlocked.Add(ref _scanWalkTicks, ticks);
        }

        protected void BuildSearchDoc(DirectoryInfo dirInfo)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FileInfo[] fileInfos = dirInfo.GetFiles()
                .Where(t => t.Attributes.HasFlag(FileAttributes.Hidden) == false).ToArray();
            AddWalkTicks(sw.ElapsedTicks);
            sw.Restart();
            search.Build(fileInfos);
            Interlocked.Add(ref _scanBuildTicks, sw.ElapsedTicks);
            Interlocked.Increment(ref _scanDirs);
            Interlocked.Add(ref _scanFiles, fileInfos.Length);
        }

        /// <summary>啟動掃描全部完成時寫一行摘要日誌</summary>
        protected void LogScanSummary(Stopwatch total)
        {
            double f = Stopwatch.Frequency;
            scanLogger.Info("索引掃描完成：{0} 個資料夾、{1} 個檔案、索引共 {2} 筆，總耗時 {3:F1} 秒（累計：走訪檔案系統 {4:F1} 秒、寫入索引 {5:F1} 秒；多個根目錄同時掃描時，累計可能大於總耗時）",
                Interlocked.Read(ref _scanDirs), Interlocked.Read(ref _scanFiles), search.DocCount,
                total.Elapsed.TotalSeconds,
                Interlocked.Read(ref _scanWalkTicks) / f, Interlocked.Read(ref _scanBuildTicks) / f);
        }

        protected void BuildIndex(FileInfo fileInfo)
        {            
            search.Build(new[] { fileInfo });
        }
        

        protected void RemoveSearchDoc(params string[] docIds)
        {
            search.Remove(docIds);
        }

        public void SetCallback(Action<string> taskQueuedCallback,
            Action<string> taskDoneCallback, Action<int> allDoneCallback)
        {
            this.taskQueuedCallback = taskQueuedCallback;
            this.taskDoneCallback = taskDoneCallback;
            this.allDoneCallback = allDoneCallback;
        }

        protected void TriggerSeekFolderStart(string folderPath)
        {
            Interlocked.Increment(ref _progressMaxinum);
            if (taskQueuedCallback != null)
            {
                taskQueuedCallback(folderPath);
            }
        }

        protected void TriggerSeekFolderDone(string folderPath)
        {
            Interlocked.Increment(ref _progressValue);
            if (taskDoneCallback != null)
            {
                taskDoneCallback(folderPath);
            }
            if (_progressValue == _progressMaxinum)
            {                
                allDoneCallback(search.DocCount);
            }
        }

        public abstract void Start(List<FolderConfig> startFolders);
        public abstract void RemoveFolderOrFile(string oldFullPath);
        public abstract void AppendFolderOrFile(string fullPath);
    }
}
