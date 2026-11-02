using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WhatSearch.Utilities
{
    public class MkvSubtitleTrack
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public string Language { get; set; }
        public string CodecId { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        public byte[] CodecPrivate { get; set; }
        /// <summary>軌道類型：1 影片、2 音訊、17 字幕</summary>
        public int Type { get; set; }

        public bool IsSupported
        {
            get
            {
                return CodecId == "S_TEXT/UTF8" || CodecId == "S_TEXT/ASS" || CodecId == "S_TEXT/SSA";
            }
        }
    }

    /// <summary>
    /// 純 C# 讀取 MKV（Matroska/EBML）內嵌的「文字字幕」軌，並轉成 WebVTT。
    /// 只支援 S_TEXT/UTF8、S_TEXT/ASS、S_TEXT/SSA；圖片字幕（PGS、VobSub）不支援。
    /// </summary>
    public static class MatroskaSubtitleReader
    {
        const uint IdEbml = 0x1A45DFA3;
        const uint IdSegment = 0x18538067;
        const uint IdInfo = 0x1549A966;
        const uint IdTimecodeScale = 0x2AD7B1;
        const uint IdDuration = 0x4489;
        const uint IdTracks = 0x1654AE6B;
        const uint IdTrackEntry = 0xAE;
        const uint IdTrackNumber = 0xD7;
        const uint IdTrackType = 0x83;
        const uint IdCodecId = 0x86;
        const uint IdCodecPrivate = 0x63A2;
        const uint IdName = 0x536E;
        const uint IdLanguage = 0x22B59C;
        const uint IdLanguageIetf = 0x22B59D;
        const uint IdFlagDefault = 0x88;
        const uint IdFlagForced = 0x55AA;
        const uint IdCluster = 0x1F43B675;
        const uint IdTimecode = 0xE7;
        const uint IdSimpleBlock = 0xA3;
        const uint IdBlockGroup = 0xA0;
        const uint IdBlock = 0xA1;
        const uint IdBlockDuration = 0x9B;

        const int SubtitleTrackType = 17;

        const uint IdAttachments = 0x1941A469;
        const uint IdAttachedFile = 0x61A7;
        const uint IdFileName = 0x466E;
        const uint IdFileMimeType = 0x4660;
        const uint IdFileData = 0x465C;
        const uint IdSeekHead = 0x114D9B74;
        const uint IdSeek = 0x4DBB;
        const uint IdSeekId = 0x53AB;
        const uint IdSeekPosition = 0x53AC;
        const int MaxCoverBytes = 8 * 1024 * 1024;

        // Segment 的直接子元素；未知大小的 Cluster 遇到這些 ID 就代表結束
        static readonly HashSet<uint> SegmentLevelIds = new HashSet<uint>
        {
            IdCluster, IdInfo, IdTracks, 0x114D9B74 /*SeekHead*/, 0x1C53BB6B /*Cues*/, 0x1254C367 /*Tags*/,
            0x1043A770 /*Chapters*/, 0x1941A469 /*Attachments*/, 0xEC /*Void*/
        };

        class Cue
        {
            public long StartMs;
            public long DurationMs;
            public byte[] Data;
        }

        /// <summary>讀取 MKV 的總長度（秒）：只讀 Segment 的 Info 區塊，很快。讀不到（含直播式串流沒有長度）回傳 null。</summary>
        public static double? GetDurationSeconds(string path)
        {
            try
            {
                using (FileStream fs = OpenRead(path))
                {
                    uint id;
                    long size;
                    bool unknown;
                    if (ReadHeader(fs, out id, out size, out unknown) == false || id != IdEbml)
                    {
                        return null;
                    }
                    fs.Seek(size, SeekOrigin.Current);
                    while (fs.Position < fs.Length)
                    {
                        if (ReadHeader(fs, out id, out size, out unknown) == false)
                        {
                            break;
                        }
                        if (id != IdSegment)
                        {
                            if (unknown)
                            {
                                break;
                            }
                            fs.Seek(size, SeekOrigin.Current);
                            continue;
                        }
                        long segmentEnd = unknown ? fs.Length : Math.Min(fs.Length, fs.Position + size);
                        while (fs.Position < segmentEnd)
                        {
                            if (ReadHeader(fs, out id, out size, out unknown) == false)
                            {
                                return null;
                            }
                            if (id == IdInfo && unknown == false)
                            {
                                return ReadInfoDuration(fs, fs.Position + size);
                            }
                            if (id == IdCluster || unknown)
                            {
                                return null;
                            }
                            fs.Seek(fs.Position + size, SeekOrigin.Begin);
                        }
                        return null;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        static double? ReadInfoDuration(FileStream fs, long end)
        {
            long scale = 1000000;
            double ticks = 0;
            while (fs.Position < end)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false || unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                if (id == IdTimecodeScale)
                {
                    scale = (long)ReadUInt(fs, (int)size);
                }
                else if (id == IdDuration && (size == 4 || size == 8))
                {
                    byte[] b = ReadBytes(fs, (int)size);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(b);
                    }
                    ticks = size == 4 ? BitConverter.ToSingle(b, 0) : BitConverter.ToDouble(b, 0);
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            return ticks > 0 ? ticks * scale / 1000000000d : (double?)null;
        }

        /// <summary>列出字幕軌（只讀檔案前段，很快）。不是 MKV 或讀取失敗回傳空清單。</summary>
        public static List<MkvSubtitleTrack> ListTracks(string path)
        {
            List<MkvSubtitleTrack> tracks = new List<MkvSubtitleTrack>();
            long scale;
            try
            {
                using (FileStream fs = OpenRead(path))
                {
                    ReadTracksAndScale(fs, tracks, out scale);
                }
            }
            catch (Exception)
            {
                return new List<MkvSubtitleTrack>();
            }
            return tracks;
        }

        /// <summary>列出所有軌道（影片、音訊、字幕；只讀檔案前段，很快）。不是 MKV 或讀取失敗回傳空清單。</summary>
        public static List<MkvSubtitleTrack> ListAllTracks(string path)
        {
            List<MkvSubtitleTrack> all = new List<MkvSubtitleTrack>();
            try
            {
                using (FileStream fs = OpenRead(path))
                {
                    long scale;
                    ReadTracksAndScale(fs, new List<MkvSubtitleTrack>(), out scale, all);
                }
            }
            catch (Exception)
            {
                return new List<MkvSubtitleTrack>();
            }
            return all;
        }

        /// <summary>取出指定字幕軌並轉成 WebVTT；軌道不存在或不支援回傳 null。</summary>
        public static string ExtractAsWebVtt(string path, int trackNumber)
        {
            List<MkvSubtitleTrack> tracks = new List<MkvSubtitleTrack>();
            string result = null;
            using (FileStream fs = OpenRead(path))
            {
                long scale;
                long segmentDataStart;
                long segmentEnd;
                ReadTracksAndScale(fs, tracks, out scale, out segmentDataStart, out segmentEnd);
                MkvSubtitleTrack track = tracks.FirstOrDefault(t => t.Number == trackNumber);
                if (track != null && track.IsSupported)
                {
                    List<Cue> cues = ReadCues(fs, segmentDataStart, segmentEnd, trackNumber, scale);
                    cues = cues.OrderBy(c => c.StartMs).ToList();
                    FillMissingDurations(cues);
                    result = track.CodecId == "S_TEXT/UTF8" ? BuildFromUtf8(cues) : BuildFromAss(track, cues);
                }
            }
            return result;
        }

        /// <summary>
        /// 讀 MKV 內嵌的封面圖（Matroska 的「附件」裡 image/* 的檔案，規格建議命名 cover.jpg／cover.png）。
        /// 沒有附件、沒有圖片、檔案壞掉或圖片超過 8 MB 都回傳 null。同時有好幾張時優先 cover、folder、poster，
        /// 其次檔名以 cover 開頭的，最後才是其他圖片（small_cover、thumb 這類縮圖排最後）。
        /// </summary>
        public static byte[] ReadCoverAttachment(string path, out string mimeType)
        {
            mimeType = null;
            try
            {
                using (FileStream fs = OpenRead(path))
                {
                    return FindCoverAttachment(fs, out mimeType);
                }
            }
            catch (Exception)
            {
                mimeType = null;
                return null;
            }
        }

        static byte[] FindCoverAttachment(FileStream fs, out string mimeType)
        {
            mimeType = null;
            uint id;
            long size;
            bool unknown;
            if (ReadHeader(fs, out id, out size, out unknown) == false || id != IdEbml)
            {
                return null;
            }
            fs.Seek(size, SeekOrigin.Current);
            while (fs.Position < fs.Length)
            {
                if (ReadHeader(fs, out id, out size, out unknown) == false)
                {
                    return null;
                }
                if (id != IdSegment)
                {
                    if (unknown)
                    {
                        return null;
                    }
                    fs.Seek(size, SeekOrigin.Current);
                    continue;
                }
                long segmentDataStart = fs.Position;
                long segmentEnd = unknown ? fs.Length : Math.Min(fs.Length, fs.Position + size);
                long attachmentsPos = -1;
                while (fs.Position < segmentEnd)
                {
                    if (ReadHeader(fs, out id, out size, out unknown) == false)
                    {
                        return null;
                    }
                    long next = unknown ? segmentEnd : fs.Position + size;
                    if (id == IdAttachments)
                    {
                        return ReadAttachments(fs, next, out mimeType);
                    }
                    if (id == IdSeekHead)
                    {
                        long pos = ReadSeekHeadForAttachments(fs, next, segmentDataStart);
                        if (pos >= 0)
                        {
                            attachmentsPos = pos;
                        }
                    }
                    else if (id == IdCluster)
                    {
                        // 附件通常在第一個 Cluster 之前；不在的話靠 SeekHead 記的位置跳過去
                        if (attachmentsPos > 0 && attachmentsPos < segmentEnd)
                        {
                            fs.Seek(attachmentsPos, SeekOrigin.Begin);
                            if (ReadHeader(fs, out id, out size, out unknown) && id == IdAttachments && unknown == false)
                            {
                                return ReadAttachments(fs, fs.Position + size, out mimeType);
                            }
                        }
                        return null;
                    }
                    if (unknown)
                    {
                        return null;
                    }
                    fs.Seek(next, SeekOrigin.Begin);
                }
                return null;
            }
            return null;
        }

        // SeekHead 裡記著各個頂層元素的位置（相對於 Segment 資料開頭）；找 Attachments 的，沒有回傳 -1
        static long ReadSeekHeadForAttachments(FileStream fs, long end, long segmentDataStart)
        {
            long found = -1;
            uint id;
            long size;
            bool unknown;
            while (fs.Position < end && ReadHeader(fs, out id, out size, out unknown))
            {
                long next = fs.Position + size;
                if (id == IdSeek && unknown == false)
                {
                    uint targetId = 0;
                    long position = -1;
                    while (fs.Position < next && ReadHeader(fs, out id, out size, out unknown))
                    {
                        long inner = fs.Position + size;
                        if (id == IdSeekId && size > 0 && size <= 4)
                        {
                            foreach (byte b in ReadBytes(fs, (int)size))
                            {
                                targetId = (targetId << 8) | b;
                            }
                        }
                        else if (id == IdSeekPosition && size > 0 && size <= 8)
                        {
                            position = (long)ReadUInt(fs, (int)size);
                        }
                        fs.Seek(inner, SeekOrigin.Begin);
                    }
                    if (targetId == IdAttachments && position >= 0)
                    {
                        found = segmentDataStart + position;
                    }
                }
                if (unknown)
                {
                    break;
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            return found;
        }

        static byte[] ReadAttachments(FileStream fs, long end, out string mimeType)
        {
            mimeType = null;
            long bestPos = -1, bestSize = 0;
            int bestRank = int.MaxValue;
            string bestMime = null;
            uint id;
            long size;
            bool unknown;
            while (fs.Position < end && ReadHeader(fs, out id, out size, out unknown) && unknown == false)
            {
                long next = fs.Position + size;
                if (id == IdAttachedFile)
                {
                    string name = null, mime = null;
                    long dataPos = -1, dataSize = 0;
                    while (fs.Position < next && ReadHeader(fs, out id, out size, out unknown) && unknown == false)
                    {
                        long inner = fs.Position + size;
                        if (id == IdFileName)
                        {
                            name = ReadString(fs, (int)Math.Min(size, 1024));
                        }
                        else if (id == IdFileMimeType)
                        {
                            mime = ReadString(fs, (int)Math.Min(size, 256));
                        }
                        else if (id == IdFileData)
                        {
                            dataPos = fs.Position;
                            dataSize = size;
                        }
                        fs.Seek(inner, SeekOrigin.Begin);
                    }
                    if (dataPos >= 0 && dataSize > 0 && dataSize <= MaxCoverBytes && string.IsNullOrEmpty(mime) == false &&
                        mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        int rank = CoverRank(name);
                        if (rank < bestRank)
                        {
                            bestRank = rank;
                            bestPos = dataPos;
                            bestSize = dataSize;
                            bestMime = mime.ToLowerInvariant();
                        }
                    }
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            if (bestPos < 0)
            {
                return null;
            }
            fs.Seek(bestPos, SeekOrigin.Begin);
            byte[] data = ReadBytes(fs, (int)bestSize);
            if (data.Length != bestSize)
            {
                return null;
            }
            mimeType = bestMime;
            return data;
        }

        internal static int CoverRank(string fileName)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (baseName == "cover") { return 0; }
            if (baseName == "folder") { return 1; }
            if (baseName == "poster") { return 2; }
            if (baseName.StartsWith("cover")) { return 3; }
            if (baseName.StartsWith("small") || baseName.StartsWith("thumb")) { return 5; }
            return 4;
        }

        static FileStream OpenRead(string path)
        {
            // 緩衝區開小一點：掃描時大量跳過影像區塊，不要每次都多讀一大段
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        }

        static void ReadTracksAndScale(FileStream fs, List<MkvSubtitleTrack> tracks, out long scale, List<MkvSubtitleTrack> all = null)
        {
            long a, b;
            ReadTracksAndScale(fs, tracks, out scale, out a, out b, all);
        }

        static void ReadTracksAndScale(FileStream fs, List<MkvSubtitleTrack> tracks, out long scale,
            out long segmentDataStart, out long segmentEnd, List<MkvSubtitleTrack> all = null)
        {
            scale = 1000000;
            segmentDataStart = 0;
            segmentEnd = fs.Length;
            uint id;
            long size;
            bool unknown;

            if (ReadHeader(fs, out id, out size, out unknown) == false || id != IdEbml)
            {
                throw new InvalidDataException("not a matroska file");
            }
            fs.Seek(size, SeekOrigin.Current);

            while (fs.Position < fs.Length)
            {
                if (ReadHeader(fs, out id, out size, out unknown) == false)
                {
                    break;
                }
                if (id != IdSegment)
                {
                    if (unknown)
                    {
                        break;
                    }
                    fs.Seek(size, SeekOrigin.Current);
                    continue;
                }
                segmentDataStart = fs.Position;
                segmentEnd = unknown ? fs.Length : Math.Min(fs.Length, fs.Position + size);
                bool gotTracks = false;
                while (fs.Position < segmentEnd)
                {
                    if (ReadHeader(fs, out id, out size, out unknown) == false)
                    {
                        break;
                    }
                    long next = unknown ? segmentEnd : fs.Position + size;
                    if (id == IdInfo)
                    {
                        scale = ReadInfoScale(fs, next, scale);
                    }
                    else if (id == IdTracks)
                    {
                        ReadTracks(fs, next, tracks, all);
                        gotTracks = true;
                    }
                    else if (id == IdCluster && gotTracks)
                    {
                        return;
                    }
                    if (unknown)
                    {
                        // 未知大小的非 Cluster 元素無法跳過，直接結束
                        return;
                    }
                    fs.Seek(next, SeekOrigin.Begin);
                }
                return;
            }
        }

        static long ReadInfoScale(FileStream fs, long end, long defaultScale)
        {
            long scale = defaultScale;
            while (fs.Position < end)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false || unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                if (id == IdTimecodeScale)
                {
                    scale = (long)ReadUInt(fs, (int)size);
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            return scale;
        }

        static void ReadTracks(FileStream fs, long end, List<MkvSubtitleTrack> tracks, List<MkvSubtitleTrack> all = null)
        {
            while (fs.Position < end)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false || unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                if (id == IdTrackEntry)
                {
                    int type;
                    MkvSubtitleTrack track = ReadTrackEntry(fs, next, out type);
                    if (track != null && all != null)
                    {
                        all.Add(track);
                    }
                    if (type == SubtitleTrackType && track != null)
                    {
                        tracks.Add(track);
                    }
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
        }

        static MkvSubtitleTrack ReadTrackEntry(FileStream fs, long end, out int type)
        {
            type = 0;
            MkvSubtitleTrack t = new MkvSubtitleTrack { Language = "eng", IsDefault = true };
            string ietf = null;
            while (fs.Position < end)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false || unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                switch (id)
                {
                    case IdTrackNumber: t.Number = (int)ReadUInt(fs, (int)size); break;
                    case IdTrackType: type = (int)ReadUInt(fs, (int)size); break;
                    case IdCodecId: t.CodecId = ReadString(fs, (int)size); break;
                    case IdName: t.Name = ReadString(fs, (int)size); break;
                    case IdLanguage: t.Language = ReadString(fs, (int)size); break;
                    case IdLanguageIetf: ietf = ReadString(fs, (int)size); break;
                    case IdFlagDefault: t.IsDefault = ReadUInt(fs, (int)size) != 0; break;
                    case IdFlagForced: t.IsForced = ReadUInt(fs, (int)size) != 0; break;
                    case IdCodecPrivate:
                        if (size <= 1024 * 1024)
                        {
                            t.CodecPrivate = ReadBytes(fs, (int)size);
                        }
                        break;
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            if (string.IsNullOrEmpty(ietf) == false)
            {
                t.Language = ietf;
            }
            t.Type = type;
            return t;
        }

        static List<Cue> ReadCues(FileStream fs, long segmentDataStart, long segmentEnd, int trackNumber, long scale)
        {
            List<Cue> cues = new List<Cue>();
            fs.Seek(segmentDataStart, SeekOrigin.Begin);
            while (fs.Position < segmentEnd)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false)
                {
                    break;
                }
                if (id == IdCluster)
                {
                    long clusterEnd = unknown ? segmentEnd : fs.Position + size;
                    ReadCluster(fs, clusterEnd, unknown, trackNumber, scale, cues);
                    if (unknown == false)
                    {
                        fs.Seek(clusterEnd, SeekOrigin.Begin);
                    }
                    continue;
                }
                if (unknown)
                {
                    break;
                }
                fs.Seek(fs.Position + size, SeekOrigin.Begin);
            }
            return cues;
        }

        static void ReadCluster(FileStream fs, long end, bool unknownSize, int trackNumber, long scale, List<Cue> cues)
        {
            long clusterTime = 0;
            while (fs.Position < end)
            {
                long childStart = fs.Position;
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false)
                {
                    break;
                }
                if (unknownSize && SegmentLevelIds.Contains(id))
                {
                    fs.Seek(childStart, SeekOrigin.Begin);
                    return;
                }
                if (unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                if (id == IdTimecode)
                {
                    clusterTime = (long)ReadUInt(fs, (int)size);
                }
                else if (id == IdSimpleBlock)
                {
                    ReadBlock(fs, next, trackNumber, clusterTime, scale, 0, cues);
                }
                else if (id == IdBlockGroup)
                {
                    ReadBlockGroup(fs, next, trackNumber, clusterTime, scale, cues);
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
        }

        static void ReadBlockGroup(FileStream fs, long end, int trackNumber, long clusterTime, long scale, List<Cue> cues)
        {
            long blockPos = -1, blockEnd = -1;
            long duration = 0;
            while (fs.Position < end)
            {
                uint id;
                long size;
                bool unknown;
                if (ReadHeader(fs, out id, out size, out unknown) == false || unknown)
                {
                    break;
                }
                long next = fs.Position + size;
                if (id == IdBlock)
                {
                    blockPos = fs.Position;
                    blockEnd = next;
                }
                else if (id == IdBlockDuration)
                {
                    duration = (long)ReadUInt(fs, (int)size);
                }
                fs.Seek(next, SeekOrigin.Begin);
            }
            if (blockPos >= 0)
            {
                fs.Seek(blockPos, SeekOrigin.Begin);
                ReadBlock(fs, blockEnd, trackNumber, clusterTime, scale, duration, cues);
            }
        }

        // Block/SimpleBlock 內容：軌道編號(vint) + 相對時間(int16) + 旗標(1 byte) + 資料
        static void ReadBlock(FileStream fs, long end, int trackNumber, long clusterTime, long scale, long duration, List<Cue> cues)
        {
            long track;
            if (ReadVint(fs, false, out track) == false || track != trackNumber)
            {
                return;
            }
            byte[] head = ReadBytes(fs, 3);
            if (head.Length < 3)
            {
                return;
            }
            short rel = (short)((head[0] << 8) | head[1]);
            byte flags = head[2];
            if ((flags & 0x06) != 0)
            {
                return; // 字幕不使用 lacing，遇到就略過
            }
            long dataLen = end - fs.Position;
            if (dataLen <= 0 || dataLen > 1024 * 1024)
            {
                return;
            }
            byte[] data = ReadBytes(fs, (int)dataLen);
            cues.Add(new Cue
            {
                StartMs = (clusterTime + rel) * scale / 1000000,
                DurationMs = duration * scale / 1000000,
                Data = data
            });
        }

        // 沒有 BlockDuration 的字幕：用到下一句開始為止，最長 8 秒、沒有下一句就 3 秒
        static void FillMissingDurations(List<Cue> cues)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                if (cues[i].DurationMs > 0)
                {
                    continue;
                }
                long gap = i + 1 < cues.Count ? cues[i + 1].StartMs - cues[i].StartMs : 3000;
                cues[i].DurationMs = Math.Max(500, Math.Min(8000, gap));
            }
        }

        static string BuildFromUtf8(List<Cue> cues)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (Cue c in cues)
            {
                string text = Encoding.UTF8.GetString(c.Data).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ');
                if (text.Length == 0)
                {
                    continue;
                }
                n++;
                sb.Append(n).Append('\n');
                sb.Append(FormatSrtTime(c.StartMs)).Append(" --> ").Append(FormatSrtTime(c.StartMs + c.DurationMs)).Append('\n');
                sb.Append(text).Append("\n\n");
            }
            return SubtitleConverter.ToWebVtt(Encoding.UTF8.GetBytes(sb.ToString()), ".srt");
        }

        static string BuildFromAss(MkvSubtitleTrack track, List<Cue> cues)
        {
            bool isSsa = track.CodecId == "S_TEXT/SSA";
            string header = track.CodecPrivate == null ? string.Empty : Encoding.UTF8.GetString(track.CodecPrivate);
            header = header.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
            int eventsIdx = header.IndexOf("[Events]", StringComparison.OrdinalIgnoreCase);
            if (eventsIdx >= 0)
            {
                header = header.Substring(0, eventsIdx);
            }
            if (header.Length > 0 && header.EndsWith("\n") == false)
            {
                header += "\n";
            }
            StringBuilder sb = new StringBuilder(header);
            sb.Append("[Events]\n");
            // 區塊內容固定是：ReadOrder, Layer(或 Marked), Style, Name, MarginL, MarginR, MarginV, Effect, Text
            sb.Append(isSsa
                ? "Format: Marked, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
                : "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
            foreach (Cue c in cues)
            {
                string line = Encoding.UTF8.GetString(c.Data).Replace("\r\n", "\n").Replace('\r', '\n');
                string[] p = line.Split(new[] { ',' }, 9);
                if (p.Length < 9)
                {
                    continue;
                }
                sb.Append("Dialogue: ")
                  .Append(p[1].Trim()).Append(',')
                  .Append(FormatAssTime(c.StartMs)).Append(',')
                  .Append(FormatAssTime(c.StartMs + c.DurationMs)).Append(',')
                  .Append(p[2]).Append(',')
                  .Append(p[3]).Append(',')
                  .Append(p[4].Trim()).Append(',')
                  .Append(p[5].Trim()).Append(',')
                  .Append(p[6].Trim()).Append(',')
                  .Append(p[7]).Append(',')
                  .Append(p[8].Replace("\n", "\\N")).Append('\n');
            }
            return SubtitleConverter.ToWebVtt(Encoding.UTF8.GetBytes(sb.ToString()), isSsa ? ".ssa" : ".ass");
        }

        static string FormatSrtTime(long ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }

        static string FormatAssTime(long ms)
        {
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0}:{1:00}:{2:00}.{3:00}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds / 10);
        }

        // ---- EBML 低階讀取 ----

        // 讀元素標頭（ID + 大小）。ID 保留標記位元（與規格寫法一致，如 0x1A45DFA3）
        static bool ReadHeader(FileStream fs, out uint id, out long size, out bool unknownSize)
        {
            id = 0;
            size = 0;
            unknownSize = false;
            int first = fs.ReadByte();
            if (first < 0)
            {
                return false;
            }
            int idLen = LeadingLength(first);
            if (idLen == 0 || idLen > 4)
            {
                return false;
            }
            uint v = (uint)first;
            for (int i = 1; i < idLen; i++)
            {
                int b = fs.ReadByte();
                if (b < 0)
                {
                    return false;
                }
                v = (v << 8) | (uint)b;
            }
            id = v;
            long sz;
            if (ReadVint(fs, true, out sz) == false)
            {
                return false;
            }
            if (sz == -1)
            {
                unknownSize = true;
                size = 0;
            }
            else
            {
                size = sz;
            }
            return true;
        }

        // 讀 vint（會去掉長度標記位元）；sizeField 為 true 時，全 1 的值代表「未知大小」，回傳 -1
        static bool ReadVint(FileStream fs, bool sizeField, out long value)
        {
            value = 0;
            int first = fs.ReadByte();
            if (first < 0)
            {
                return false;
            }
            int len = LeadingLength(first);
            if (len == 0)
            {
                return false;
            }
            long v = first & (0xFF >> len);
            bool allOnes = v == (0xFF >> len);
            for (int i = 1; i < len; i++)
            {
                int b = fs.ReadByte();
                if (b < 0)
                {
                    return false;
                }
                v = (v << 8) | (long)b;
                if (b != 0xFF)
                {
                    allOnes = false;
                }
            }
            value = (allOnes && sizeField) ? -1 : v;
            return true;
        }

        static int LeadingLength(int firstByte)
        {
            for (int i = 0; i < 8; i++)
            {
                if ((firstByte & (0x80 >> i)) != 0)
                {
                    return i + 1;
                }
            }
            return 0;
        }

        static ulong ReadUInt(FileStream fs, int size)
        {
            ulong v = 0;
            for (int i = 0; i < size && i < 8; i++)
            {
                int b = fs.ReadByte();
                if (b < 0)
                {
                    break;
                }
                v = (v << 8) | (uint)b;
            }
            return v;
        }

        static byte[] ReadBytes(FileStream fs, int size)
        {
            byte[] buf = new byte[size];
            int read = 0;
            while (read < size)
            {
                int n = fs.Read(buf, read, size - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            if (read < size)
            {
                Array.Resize(ref buf, read);
            }
            return buf;
        }

        static string ReadString(FileStream fs, int size)
        {
            return Encoding.UTF8.GetString(ReadBytes(fs, size)).TrimEnd('\0');
        }
    }
}
