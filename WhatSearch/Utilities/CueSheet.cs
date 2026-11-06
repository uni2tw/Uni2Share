using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WhatSearch.Utilities
{
    public class CueTrack
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string Performer { get; set; }
        /// <summary>這一軌所屬的音檔（CUE 裡的 FILE，不含路徑處理）</summary>
        public string File { get; set; }
        /// <summary>INDEX 01 的時間（秒）</summary>
        public double Start { get; set; }
        /// <summary>結束時間（秒）：同一個音檔裡下一軌的 INDEX 01；最後一軌為 null（播到檔案結尾）</summary>
        public double? End { get; set; }
    }

    /// <summary>CUE 檔解析結果；只取拆軌需要的欄位（FILE／TRACK／TITLE／PERFORMER／INDEX 01／DATE／GENRE）</summary>
    public class CueSheet
    {
        public string Title { get; set; }
        public string Performer { get; set; }
        public string Date { get; set; }
        public string Genre { get; set; }
        public List<CueTrack> Tracks { get; set; } = new List<CueTrack>();

        static readonly Regex IndexRegex = new Regex(@"^(\d{1,3}):(\d{1,2}):(\d{1,2})$", RegexOptions.Compiled);

        public static CueSheet Parse(byte[] data)
        {
            return ParseText(TextDecoder.Decode(data));
        }

        public static CueSheet ParseText(string text)
        {
            CueSheet sheet = new CueSheet();
            string currentFile = null;
            CueTrack track = null;
            foreach (string raw in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0)
                {
                    continue;
                }
                string cmd = FirstWord(line, out string rest);
                switch (cmd.ToUpperInvariant())
                {
                    case "REM":
                        string key = FirstWord(rest, out string val);
                        if (track == null)
                        {
                            if (key.Equals("DATE", StringComparison.OrdinalIgnoreCase)) { sheet.Date = Unquote(val); }
                            else if (key.Equals("GENRE", StringComparison.OrdinalIgnoreCase)) { sheet.Genre = Unquote(val); }
                        }
                        break;
                    case "FILE":
                        currentFile = ParseFileName(rest);
                        break;
                    case "TRACK":
                        int number;
                        string num = FirstWord(rest, out string kind);
                        if (int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) == false)
                        {
                            number = sheet.Tracks.Count + 1;
                        }
                        track = null;
                        if (kind.Trim().StartsWith("AUDIO", StringComparison.OrdinalIgnoreCase))
                        {
                            track = new CueTrack { Number = number, File = currentFile, Start = double.NaN };
                            sheet.Tracks.Add(track);
                        }
                        break;
                    case "TITLE":
                        if (track != null) { track.Title = Unquote(rest); }
                        else { sheet.Title = Unquote(rest); }
                        break;
                    case "PERFORMER":
                        if (track != null) { track.Performer = Unquote(rest); }
                        else { sheet.Performer = Unquote(rest); }
                        break;
                    case "INDEX":
                        string idx = FirstWord(rest, out string time);
                        double seconds;
                        if (track != null && idx == "01" && TryParseTime(time.Trim(), out seconds))
                        {
                            track.Start = seconds;
                        }
                        break;
                }
            }
            sheet.Tracks = sheet.Tracks.Where(t => double.IsNaN(t.Start) == false).ToList();
            // 結束時間：同一個音檔裡的下一軌
            for (int i = 0; i < sheet.Tracks.Count; i++)
            {
                CueTrack next = sheet.Tracks.Skip(i + 1).FirstOrDefault();
                if (next != null && string.Equals(next.File, sheet.Tracks[i].File, StringComparison.OrdinalIgnoreCase) && next.Start > sheet.Tracks[i].Start)
                {
                    sheet.Tracks[i].End = next.Start;
                }
            }
            return sheet;
        }

        /// <summary>mm:ss:ff（ff 是 1/75 秒，mm 可以超過 59）</summary>
        public static bool TryParseTime(string text, out double seconds)
        {
            seconds = 0;
            Match m = IndexRegex.Match(text ?? string.Empty);
            if (m.Success == false)
            {
                return false;
            }
            int mm = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int ss = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int ff = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (ss > 59 || ff > 74)
            {
                return false;
            }
            seconds = mm * 60 + ss + ff / 75.0;
            return true;
        }

        static string FirstWord(string line, out string rest)
        {
            line = line.TrimStart();
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i]) == false)
            {
                i++;
            }
            rest = line.Substring(i).Trim();
            return line.Substring(0, i);
        }

        static string Unquote(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        // FILE "xxx.flac" WAVE：檔名可能含空白，用引號包起來；沒有引號就取到最後一個空白之前
        static string ParseFileName(string rest)
        {
            rest = rest.Trim();
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = rest.LastIndexOf('"');
                if (end > 0)
                {
                    return rest.Substring(1, end - 1);
                }
                return rest.Substring(1);
            }
            int sp = rest.LastIndexOf(' ');
            return sp > 0 ? rest.Substring(0, sp) : rest;
        }

        // ---------- 輸出用的名稱 ----------

        static readonly Regex DiscRegex = new Regex(@"(?:\bdisc|\bcd|\bdisk)[\s_.\-]*0*(\d{1,2})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>輸出資料夾名稱：優先看 TITLE 的 Disc N，其次是音檔／CUE 檔名裡的 Disc N／CD N，最後用 CUE 檔名（不含副檔名）</summary>
        public string FolderName(string cueFileName, string audioFileName)
        {
            foreach (string source in new[] { Title, audioFileName, cueFileName })
            {
                Match m = DiscRegex.Match(source ?? string.Empty);
                if (m.Success)
                {
                    return "Disc " + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                }
            }
            return SafeName(Path.GetFileNameWithoutExtension(cueFileName), "CUE");
        }

        /// <summary>專輯名稱：去掉 TITLE 結尾的 (Disc N)</summary>
        public string AlbumName()
        {
            string t = (Title ?? string.Empty).Trim();
            t = Regex.Replace(t, @"[\s\-_]*[\(\[（]\s*(?:disc|cd|disk)[\s_.\-]*\d{1,2}\s*[\)\]）]\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return t;
        }

        public static string SafeName(string name, string fallback)
        {
            StringBuilder sb = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name ?? string.Empty)
            {
                sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            }
            string s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length > 100)
            {
                s = s.Substring(0, 100).TrimEnd('.', ' ');
            }
            return s.Length == 0 ? fallback : s;
        }

        /// <summary>輸出檔名：01 - 曲名.mp3</summary>
        public string TrackFileName(CueTrack t, int width)
        {
            string title = SafeName(t.Title, "Track " + t.Number.ToString(CultureInfo.InvariantCulture));
            return t.Number.ToString("D" + width, CultureInfo.InvariantCulture) + " - " + title + ".mp3";
        }
    }
}
