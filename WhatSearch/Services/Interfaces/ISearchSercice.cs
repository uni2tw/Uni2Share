using System.Collections.Generic;
using System.IO;
using WhatSearch.Models;

namespace WhatSearch.Service
{
    public interface ISearchSercice
    {
        void Build(IEnumerable<FileInfo> deals);
        /// <summary>
        /// 啟動掃描用：只寫入、不 commit；最多每 3 秒重新整理一次，讓查詢看得到掃描進度。掃完要呼叫 Flush。
        /// 索引放在記憶體，commit 沒有「存檔」的意義，所以掃描途中不 commit 不會影響資料安全
        /// </summary>
        void BuildBatch(IEnumerable<FileInfo> deals);
        /// <summary>commit 並立刻重新整理（批次寫入之後呼叫）</summary>
        void Flush();
        /// <summary>寫入索引的累計時間（秒），只用來寫日誌：簡繁轉換、寫入文件、commit／重新整理</summary>
        void GetTimings(out double convertSec, out double updateSec, out double commitSec);
        /// <summary>移除指定的檔案索引；傳多個時只 commit 一次</summary>
        void Remove(params string[] docIds);
        /// <summary>移除這個資料夾底下所有檔案（含子資料夾）的索引；資料夾本身已不存在（刪除、改名之後）也可以用，不需要讀檔案系統</summary>
        void RemoveFolder(string dirPath);
        List<IndexedFileDoc> Query(string queryString, int maxDoc = 100);
        int DocCount { get; }
    }
}
