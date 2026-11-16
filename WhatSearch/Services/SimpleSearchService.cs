using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using WhatSearch.Core;
using System;
using System.Collections.Generic;
using System.IO;
using Document = Lucene.Net.Documents.Document;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using WhatSearch.Models;
using WhatSearch.Utilities;
using WhatSearch.Services.Interfaces;
using NLog;

namespace WhatSearch.Service
{

    public abstract class SearchServiceBase : ISearchSercice
    {

        private IChineseConverter cc = Ioc.Get<IChineseConverter>();

        protected Lucene.Net.Store.Directory _dir;

        public int DocCount
        {
            get
            {
                InitWriter();
                IndexSearcher searcher = searcherManager.Acquire();
                try
                {
                    return searcher.IndexReader.NumDocs;
                }
                finally
                {
                    searcherManager.Release(searcher);
                }
            }
        }
        public abstract LuceneVersion GetLuceneVersion();
        public abstract Analyzer GetAnalyzer();

        public abstract Lucene.Net.Store.Directory GetDirectory();

        static ILogger logger = LogManager.GetCurrentClassLogger();

        static object thisLock = new object();

        IndexWriter iwriter;
        // 查詢共用同一組 reader，commit 之後才換新的，不要每次查詢都 DirectoryReader.Open（沒 Dispose 會一直佔著記憶體）
        SearcherManager searcherManager;
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void InitWriter()
        {
            if (iwriter == null)
            {
                iwriter = new IndexWriter(GetDirectory(), new IndexWriterConfig(GetLuceneVersion(), GetAnalyzer()));
                searcherManager = new SearcherManager(iwriter, true, null);
            }
        }

        /// <summary>commit 之後呼叫，讓之後的查詢看到最新的索引</summary>
        private void CommitAndRefresh()
        {
            iwriter.Commit();
            searcherManager.MaybeRefreshBlocking();
        }



        // 寫入索引的累計時間（Stopwatch ticks），只用來寫日誌
        long convertTicks;
        long updateTicks;
        long commitTicks;
        long lastRefreshTs = Stopwatch.GetTimestamp();
        static readonly long RefreshIntervalTicks = Stopwatch.Frequency * 3;

        public void Build(IEnumerable<FileInfo> searchDocs)
        {
            BuildCore(searchDocs, true);
        }

        public void BuildBatch(IEnumerable<FileInfo> searchDocs)
        {
            BuildCore(searchDocs, false);
        }

        void BuildCore(IEnumerable<FileInfo> searchDocs, bool commit)
        {
            lock (thisLock)
            {
                InitWriter();
                foreach (FileInfo fi in searchDocs)
                {
                    try
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        string tcFullName = cc.ToTraditionalChinese(fi.FullName);
                        long t1 = Stopwatch.GetTimestamp();
                        var oDocument = new Document
                        {
                            new StringField(IndexedFileDoc.Columns.Id, fi.FullName, Field.Store.YES),
                            new TextField(IndexedFileDoc.Columns.FullName, tcFullName, Field.Store.YES),
                            new StringField(IndexedFileDoc.Columns.DirectoryName, fi.DirectoryName, Field.Store.YES),
                            new StringField(IndexedFileDoc.Columns.Name, fi.Name, Field.Store.YES),
                            new Int64Field(IndexedFileDoc.Columns.Length, fi.Length, Field.Store.YES),
                            new StringField(IndexedFileDoc.Columns.CreationTime, fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), Field.Store.YES),
                            new StringField(IndexedFileDoc.Columns.LastWriteTime, fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), Field.Store.YES),
                            new StringField(IndexedFileDoc.Columns.Extension, fi.Extension, Field.Store.YES)
                        };
                        iwriter.UpdateDocument(new Term(IndexedFileDoc.Columns.Id, fi.FullName), oDocument);
                        long t2 = Stopwatch.GetTimestamp();
                        convertTicks += t1 - t0;
                        updateTicks += t2 - t1;
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "建立索引失敗 {0}", fi.FullName);
                        throw;
                    }
                }
                long c0 = Stopwatch.GetTimestamp();
                if (commit)
                {
                    CommitAndRefresh();
                    lastRefreshTs = Stopwatch.GetTimestamp();
                }
                else if (c0 - lastRefreshTs >= RefreshIntervalTicks)
                {
                    // 批次模式：不 commit，只每隔幾秒重新整理，讓查詢看到掃描進度
                    searcherManager.MaybeRefreshBlocking();
                    lastRefreshTs = Stopwatch.GetTimestamp();
                }
                commitTicks += Stopwatch.GetTimestamp() - c0;
            }
        }

        public void Flush()
        {
            lock (thisLock)
            {
                InitWriter();
                long c0 = Stopwatch.GetTimestamp();
                CommitAndRefresh();
                lastRefreshTs = Stopwatch.GetTimestamp();
                commitTicks += lastRefreshTs - c0;
            }
        }

        public void GetTimings(out double convertSec, out double updateSec, out double commitSec)
        {
            lock (thisLock)
            {
                double f = Stopwatch.Frequency;
                convertSec = convertTicks / f;
                updateSec = updateTicks / f;
                commitSec = commitTicks / f;
            }
        }

        public void Remove(params string[] docIds)
        {
            if (docIds == null || docIds.Length == 0)
            {
                return;
            }
            lock (thisLock)
            {
                InitWriter();
                foreach (string docId in docIds)
                {
                    iwriter.DeleteDocuments(new Term(IndexedFileDoc.Columns.Id, docId));
                }
                CommitAndRefresh();
            }
        }

        public void RemoveFolder(string dirPath)
        {
            if (string.IsNullOrEmpty(dirPath))
            {
                return;
            }
            lock (thisLock)
            {
                InitWriter();
                // 文件的 Id 是檔案完整路徑（StringField，不分詞），所以資料夾底下的所有檔案（含子資料夾）都是「資料夾路徑 + 分隔字元」開頭。
                // 一定要加分隔字元：刪除「A」不能連「AB」一起刪掉
                string prefix = dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                iwriter.DeleteDocuments(new PrefixQuery(new Term(IndexedFileDoc.Columns.Id, prefix)));
                CommitAndRefresh();
            }
        }

        public List<IndexedFileDoc> Query(string queryString, int maxDoc = 100)
        {
            //轉成繁體再搜尋
            queryString = cc.ToTraditionalChinese(queryString.Trim());
            List<IndexedFileDoc> items = new List<IndexedFileDoc>();

            if (string.IsNullOrEmpty(queryString) || maxDoc == 0)
            {
                return items;
            }
            InitWriter();
            IndexSearcher searcher = searcherManager.Acquire();
            try
            {
                Query qq = SearchHelper.GetLuceneQuery(IndexedFileDoc.Columns.FullName, queryString);
                TopDocs cc = searcher.Search(qq, maxDoc);
                
                foreach (var item in cc.ScoreDocs)
                {
                    Document doc = searcher.Doc(item.Doc);
                    items.Add(new IndexedFileDoc
                    {
                        FullName = doc.Get(IndexedFileDoc.Columns.Id),
                        Length = long.Parse(doc.Get(IndexedFileDoc.Columns.Length)),
                        LastWriteTime = DateTime.Parse(doc.Get(IndexedFileDoc.Columns.LastWriteTime)),
                        Name = doc.Get(IndexedFileDoc.Columns.Name),
                        DirectoryName = doc.Get(IndexedFileDoc.Columns.DirectoryName),
                        CreationTime = DateTime.Parse(doc.Get(IndexedFileDoc.Columns.CreationTime))
                    });
                }
                return items;
            }
            finally
            {
                searcherManager.Release(searcher);
            }
        }

        public abstract void Dispose();
    }

    public sealed class SimpleSearchService : SearchServiceBase
    {
        public SimpleSearchService()
        {
            _dir = new RAMDirectory();
        }


        public override LuceneVersion GetLuceneVersion()
        {
            return LuceneVersion.LUCENE_48;
        }

        public override Analyzer GetAnalyzer()
        {
            return new MicroAnalyzer(GetLuceneVersion());

        }

        public override void Dispose()
        {

        }

        public override Lucene.Net.Store.Directory GetDirectory()
        {
            return _dir;
        }
    }
}
