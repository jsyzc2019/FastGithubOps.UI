using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP地址过滤器
    /// <para>
    /// 参考 dev-sidecar 的 <c>isZeroIp</c>（过滤 0.0.0.0 / :: 这类 Adblock 拦截特征）
    /// 并补齐 DNS 污染地址过滤。原 FastGithub 对解析结果只做了 loopback 过滤，
    /// 国内 DNS 返回的污染地址会直接进入测速队列——它们往往"握手很快"，
    /// 于是被稳定选中、稳定失败，是"装了没效果"的重要原因之一。
    /// </para>
    /// </summary>
    public static class IpAddressFilter
    {
        /// <summary>
        /// 已知的DNS污染IP（来自公开整理，命中即丢弃）
        /// </summary>
        private static readonly HashSet<string> knownPoisonIps = new(StringComparer.Ordinal)
        {
            "203.98.7.65", "159.106.121.75", "243.185.187.39", "8.7.198.45",
            "37.61.54.158", "46.82.174.68", "59.24.3.173", "78.16.49.15",
            "93.46.8.89", "128.121.126.139", "180.168.41.175", "188.5.4.96",
            "197.4.4.4", "203.161.230.171", "207.12.88.98", "209.36.73.33",
            "211.94.66.147", "213.169.251.35", "216.221.188.182", "216.234.179.13",
            "23.253.183.71", "249.129.46.48", "253.157.14.165", "192.30.255.113",
        };

        /// <summary>
        /// 判定是否应丢弃该解析结果
        /// </summary>
        /// <param name="address">IP</param>
        /// <returns>true表示应丢弃</returns>
        public static bool IsInvalid(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return IsInvalidIPv6(address);
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();

            // 0.0.0.0 —— Adblock / 拦截特征
            if (bytes[0] == 0)
            {
                return true;
            }
            // 127.0.0.0/8
            if (bytes[0] == 127)
            {
                return true;
            }
            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }
            // 100.64.0.0/10（运营商级NAT）
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }
            // 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }
      // 192.168.0.0/16（私有网段）
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }
            // 192.0.0.0/24（IETF协议保留）、192.0.2.0/24（TEST-NET-1）、
            // 192.88.99.0/24（6to4中继，RFC 7526 已废弃）
            if (bytes[0] == 192 && bytes[2] == 0 && (bytes[1] == 0 || bytes[1] == 2 || bytes[1] == 88))
            {
                return true;
            }
     // 198.18.0.0/15（基准测试保留段）
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }
            // 198.51.100.0/24（TEST-NET-2）
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            {
                return true;
            }
            // 203.0.113.0/24(TEST-NET-3)
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            {
                return true;
            }
            // 224.0.0.0/4（组播）+ 240.0.0.0/4（保留，含255.255.255.255）
            if (bytes[0] >= 224)
            {
                return true;
            }

            return knownPoisonIps.Contains(address.ToString());
        }

        /// <summary>
        /// 判定IPv6是否应丢弃
        /// </summary>
        private static bool IsInvalidIPv6(IPAddress address)
        {
            var bytes = address.GetAddressBytes();

            // :: 与 ::1
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }
            // fc00::/7 与 fe80::/10（唯一本地地址与链路本地）
            if ((bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80))
            {
                return true;
            }
            // ff00::/8（组播）
            if (bytes[0] == 0xFF)
            {
                return true;
            }
            // 2002::/16（6to4）不是有效公网出口
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return true;
            }

            // ::ffff:0:0/96（IPv4映射地址）—— 必须单独判定。
            // 已知污染地址常以 ::ffff:203.98.7.65 这样的映射形式经 AAAA 记录返回，
            // 而 knownPoisonIps 只收录 IPv4 字面量；不在此处拆回 IPv4 递归判定，
            // 污染地址就能完整绕过整个过滤器。
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[10] == 0xFF && bytes[11] == 0xFF)
            {
                var mapped = new byte[4];
                Array.Copy(bytes, 12, mapped, 0, 4);
                return IsInvalid(new IPAddress(mapped));
            }

            // ::/96（IPv4兼容地址，已废弃）与 64:ff9b::/96（NAT64）
            if ((bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
                || (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 过滤一批IP
        /// </summary>
        public static IEnumerable<IPAddress> Filter(IEnumerable<IPAddress> addresses)
        {
            foreach (var address in addresses)
            {
                if (IsInvalid(address) == false)
                {
                    yield return address;
                }
            }
        }
    }
}
