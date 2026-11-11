using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Net;

namespace WhatSearch.Core
{
    /// <summary>
    /// 找出請求真正的來源 IP。站台放在反向代理後面時，連線的對象是代理，真正的來源在 X-Forwarded-For；
    /// 但這個標頭任何人都能自己填，所以只有「連進來的是受信任的代理」才採用，而且從右邊往左跳過代理，取第一個不是代理的位址。
    /// 受信任的代理：設定了 abnormal.trustedProxies 就只信那份清單（IP 或 a.b.c.d/nn）；沒設定時信任本機與內網位址。
    /// </summary>
    public static class ClientIpResolver
    {
        public static string Resolve(HttpContext context, IList<string> trustedProxies)
        {
            IPAddress remote = Normalize(context.Connection.RemoteIpAddress);
            if (remote == null)
            {
                return "?";
            }
            string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
            if (string.IsNullOrWhiteSpace(forwarded) || IsTrusted(remote, trustedProxies) == false)
            {
                return remote.ToString();
            }
            string[] parts = forwarded.Split(',');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                IPAddress ip;
                if (TryParseForwarded(parts[i], out ip) == false)
                {
                    // 格式不對就不信這個標頭
                    return remote.ToString();
                }
                if (i == 0 || IsTrusted(ip, trustedProxies) == false)
                {
                    return ip.ToString();
                }
            }
            return remote.ToString();
        }

        /// <summary>
        /// 這個請求是不是「直接從本機連進來」：連線位址是回送位址（127.0.0.1／::1），而且沒有任何代理轉發標頭。
        /// 站台放在同一台機器的反向代理後面時，外部請求經代理轉進來的連線位址也是本機，但代理會帶 X-Forwarded-For 之類的標頭，所以有標頭就不算
        /// </summary>
        public static bool IsDirectLoopback(HttpContext context)
        {
            IPAddress remote = Normalize(context.Connection.RemoteIpAddress);
            if (remote == null || IPAddress.IsLoopback(remote) == false)
            {
                return false;
            }
            IHeaderDictionary h = context.Request.Headers;
            return h.ContainsKey("X-Forwarded-For") == false
                && h.ContainsKey("Forwarded") == false
                && h.ContainsKey("X-Real-IP") == false;
        }

        /// <summary>這個位址是不是設定的 IP 白名單裡的（單一 IP 或 a.b.c.d/nn）；清單是空的一律 false</summary>
        public static bool InList(string ip, IList<string> list)
        {
            IPAddress address;
            if (list == null || list.Count == 0 || IPAddress.TryParse(ip, out address) == false)
            {
                return false;
            }
            address = Normalize(address);
            foreach (string entry in list)
            {
                if (Matches(address, entry))
                {
                    return true;
                }
            }
            return false;
        }

        static bool IsTrusted(IPAddress ip, IList<string> trustedProxies)
        {
            if (trustedProxies == null || trustedProxies.Count == 0)
            {
                return IsLocalOrPrivate(ip);
            }
            foreach (string entry in trustedProxies)
            {
                if (Matches(ip, entry))
                {
                    return true;
                }
            }
            return false;
        }

        static bool Matches(IPAddress ip, string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }
            string text = entry.Trim();
            int slash = text.IndexOf('/');
            IPAddress network;
            if (slash < 0)
            {
                return IPAddress.TryParse(text, out network) && Normalize(network).Equals(ip);
            }
            int bits;
            if (IPAddress.TryParse(text.Substring(0, slash), out network) == false || int.TryParse(text.Substring(slash + 1), out bits) == false)
            {
                return false;
            }
            byte[] a = ip.GetAddressBytes();
            byte[] b = Normalize(network).GetAddressBytes();
            if (a.Length != b.Length || bits < 0 || bits > a.Length * 8)
            {
                return false;
            }
            for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
            {
                int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
                if ((a[i] & mask) != (b[i] & mask))
                {
                    return false;
                }
            }
            return true;
        }

        static bool IsLocalOrPrivate(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254);
            }
            // IPv6：唯一本地位址 fc00::/7、連結本地 fe80::/10
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }

        // X-Forwarded-For 的每一段可能帶連接埠（1.2.3.4:5678、[::1]:5678）或引號
        static bool TryParseForwarded(string text, out IPAddress ip)
        {
            ip = null;
            string s = (text ?? string.Empty).Trim().Trim('"');
            if (s.StartsWith("["))
            {
                int end = s.IndexOf(']');
                if (end < 0)
                {
                    return false;
                }
                s = s.Substring(1, end - 1);
            }
            else if (s.IndexOf(':') >= 0 && s.IndexOf(':') == s.LastIndexOf(':'))
            {
                s = s.Substring(0, s.IndexOf(':'));
            }
            if (IPAddress.TryParse(s, out ip) == false)
            {
                return false;
            }
            ip = Normalize(ip);
            return true;
        }

        static IPAddress Normalize(IPAddress ip)
        {
            if (ip != null && ip.IsIPv4MappedToIPv6)
            {
                return ip.MapToIPv4();
            }
            return ip;
        }
    }
}
