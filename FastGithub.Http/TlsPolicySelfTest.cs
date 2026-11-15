using System;
using System.Net.Security;

namespace FastGithub.Http
{
    /// <summary>
    /// 【v2.6.13 A3】TLS 证书策略自检（借鉴上游 v2.5.0）。
    /// <para>
    /// 旧实现在 <c>TlsIgnoreNameMismatch</c> 分支直接 return true，
    /// 连带放行了 <c>RemoteCertificateChainErrors</c> —— 污染 IP 背后的中间人
    /// 正是以「名称不匹配 + 链错误」组合出现。这类 IP 一旦被当作可信且响应快，
    /// 会长期占据候选头部，表现为页面转圈/时好时坏。
    /// </para>
    /// </summary>
    public static class TlsPolicySelfTest
    {
        private static int failed;

        public static int Run()
        {
            TestCleanCertificate();
            TestNameMismatchWithIgnore();
            TestNameMismatchWithDnsMatch();
            TestNameMismatchRejected();
            TestChainErrorsAlwaysRejected();
            TestCombinedErrorsRejected();

            Console.WriteLine(failed == 0
                ? "TlsPolicySelfTest: 全部通过"
                : $"TlsPolicySelfTest: 失败 {failed} 项");
            return failed;
        }

        private static void Assert(bool condition, string message)
        {
            if (condition)
            {
                Console.WriteLine($"  OK   {message}");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL {message}");
            }
        }

        private static void TestCleanCertificate()
        {
            Console.WriteLine("TLS：无错误证书");
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.None, false, false),
                "无错误 -> 接受");
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.None, true, false),
                "配置忽略名称不匹配但证书无错误 -> 仍接受");
        }

        private static void TestNameMismatchWithIgnore()
        {
            Console.WriteLine("TLS：名称不匹配");
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch, true, false),
                "仅名称不匹配 + 配置忽略 -> 接受（不发 SNI 时的正常现象）");
        }

        private static void TestNameMismatchWithDnsMatch()
        {
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch, false, true),
                "名称不匹配但证书 SAN 仍匹配域名 -> 接受");
        }

        private static void TestNameMismatchRejected()
        {
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.RemoteCertificateNameMismatch, false, false) == false,
                "名称不匹配且既不忽略也不匹配 -> 拒绝");
        }

        private static void TestChainErrorsAlwaysRejected()
        {
            Console.WriteLine("TLS：链错误绝不放行");
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, false, false) == false,
                "链错误 -> 拒绝");
            Assert(HttpClientHandler.IsCertificateAcceptable(SslPolicyErrors.RemoteCertificateChainErrors, true, true) == false,
                "链错误 + 配置忽略名称不匹配 + SAN 匹配 -> 仍拒绝");
        }

        /// <summary>
        /// 关键回归：旧实现在这一组合上返回 true
        /// </summary>
        private static void TestCombinedErrorsRejected()
        {
            var combined = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
            Assert(HttpClientHandler.IsCertificateAcceptable(combined, true, false) == false,
                "名称不匹配 + 链错误 + 配置忽略 -> 拒绝（旧实现误放行）");
            Assert(HttpClientHandler.IsCertificateAcceptable(combined, false, true) == false,
                "名称不匹配 + 链错误 + SAN 匹配 -> 拒绝（旧实现误放行）");
        }
    }
}
