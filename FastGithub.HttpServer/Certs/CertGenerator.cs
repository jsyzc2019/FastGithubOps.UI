using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FastGithub.HttpServer.Certs
{
    /// <summary>
    /// 证书生成器
    /// </summary>
    static class CertGenerator
    {
        private static readonly Oid tlsServerOid = new("1.3.6.1.5.5.7.3.1");
        private static readonly Oid tlsClientOid = new("1.3.6.1.5.5.7.3.2");

        /// <summary>
        /// 生成ca证书
        /// </summary>
        /// <param name="subjectName"></param>
        /// <param name="notBefore"></param>
        /// <param name="notAfter"></param>
        /// <param name="rsaKeySizeInBits"></param>
        /// <param name="pathLengthConstraint"></param>
        /// <returns></returns>
        public static X509Certificate2 CreateCACertificate(
            X500DistinguishedName subjectName,
            DateTimeOffset notBefore,
            DateTimeOffset notAfter,
            int rsaKeySizeInBits = 2048,
            int pathLengthConstraint = 1)
        {
            using var rsa = RSA.Create(rsaKeySizeInBits);
            var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var basicConstraints = new X509BasicConstraintsExtension(true, pathLengthConstraint > 0, pathLengthConstraint, true);
            request.CertificateExtensions.Add(basicConstraints);

            var keyUsage = new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.KeyCertSign, true);
            request.CertificateExtensions.Add(keyUsage);

            var oids = new OidCollection { tlsServerOid, tlsClientOid };
            var enhancedKeyUsage = new X509EnhancedKeyUsageExtension(oids, true);
            request.CertificateExtensions.Add(enhancedKeyUsage);

            var dnsBuilder = new SubjectAlternativeNameBuilder();
            dnsBuilder.AddCommonName(subjectName);
            request.CertificateExtensions.Add(dnsBuilder.Build());

            var subjectKeyId = new X509SubjectKeyIdentifierExtension(request.PublicKey, false);
            request.CertificateExtensions.Add(subjectKeyId);

            return request.CreateSelfSigned(notBefore, notAfter);
        }

        /// <summary>
        /// 生成服务器证书
        /// </summary>
        /// <param name="issuerCertificate"></param>
        /// <param name="subjectName"></param>
        /// <param name="extraDnsNames"></param>
        /// <param name="notBefore"></param>
        /// <param name="notAfter"></param>
        /// <param name="rsaKeySizeInBits"></param>
        /// <returns></returns>
        public static X509Certificate2 CreateEndCertificate(
            X509Certificate2 issuerCertificate,
            X500DistinguishedName subjectName,
            IEnumerable<string>? extraDnsNames = default,
            DateTimeOffset? notBefore = default,
            DateTimeOffset? notAfter = default,
            int rsaKeySizeInBits = 2048)
        {
            using var rsa = RSA.Create(rsaKeySizeInBits);
            var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var basicConstraints = new X509BasicConstraintsExtension(false, false, 0, true);
            request.CertificateExtensions.Add(basicConstraints);

            var keyUsage = new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true);
            request.CertificateExtensions.Add(keyUsage);

            var oids = new OidCollection { tlsServerOid, tlsClientOid };
            var enhancedKeyUsage = new X509EnhancedKeyUsageExtension(oids, true);
            request.CertificateExtensions.Add(enhancedKeyUsage);

            var authorityKeyId = GetAuthorityKeyIdentifierExtension(issuerCertificate);
            request.CertificateExtensions.Add(authorityKeyId);

            var subjectKeyId = new X509SubjectKeyIdentifierExtension(request.PublicKey, false);
            request.CertificateExtensions.Add(subjectKeyId);

            var dnsBuilder = new SubjectAlternativeNameBuilder();
            dnsBuilder.AddCommonName(subjectName);

            if (extraDnsNames != null)
            {
                foreach (var dnsName in extraDnsNames)
                {
                    dnsBuilder.Add(dnsName);
                }
            }

            var dnsNames = dnsBuilder.Build();
            request.CertificateExtensions.Add(dnsNames);

            if (notBefore == null || notBefore.Value < issuerCertificate.NotBefore)
            {
                notBefore = issuerCertificate.NotBefore;
            }

            if (notAfter == null || notAfter.Value > issuerCertificate.NotAfter)
            {
                notAfter = issuerCertificate.NotAfter;
            }

            var serialNumber = BitConverter.GetBytes(Random.Shared.NextInt64());
            using var certOnly = request.Create(issuerCertificate, notBefore.Value, notAfter.Value, serialNumber);
            return certOnly.CopyWithPrivateKey(rsa);
        }



        private static void AddCommonName(this SubjectAlternativeNameBuilder builder, X500DistinguishedName subjectName)
        {
            // 证书主题形如 "CN=xxx"，需去掉 "CN=" 前缀后作为 SAN。
            // 原实现直接 subjectName.Name[3..]，隐含假设名称至少 3 个字符；
            // 若名称为空或过短（改名、克隆、容器环境等），会抛
            // ArgumentOutOfRangeException。改为先剥离前缀、再判空。
            var name = subjectName.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            const string cnPrefix = "CN=";
            if (name.StartsWith(cnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[cnPrefix.Length..];
            }

            builder.Add(name);
        }

        private static void Add(this SubjectAlternativeNameBuilder builder, string name)
        {
            // 【必须判空】SubjectAlternativeNameBuilder.AddDnsName 对空串/空白串直接抛
            // ArgumentOutOfRangeException("dnsName")。本方法由证书生成调用链
            // （CertService.GetExtraDomains -> CreateEndCertificate -> Kestrel 的
            // ServerOptionsCallback）触发，且 Kestrel 侧未捕获该异常：
            // 任何一个不带 SNI 的 TLS 连接都会让异常逃逸并中断该连接，
            // 日志中已实测到 3 次 "String cannot be empty or null. (Parameter 'dnsName')"。
            // 空值对SAN 也没有任何意义，直接跳过即可。
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (IPAddress.TryParse(name, out var address))
            {
                builder.AddIpAddress(address);
            }
            else
            {
                builder.AddDnsName(name);
            }
        }


        private static X509Extension GetAuthorityKeyIdentifierExtension(X509Certificate2 certificate)
        { 
            var extension = new X509SubjectKeyIdentifierExtension(certificate.PublicKey, false);
#if NET7_0_OR_GREATER
            return X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(extension);
#else
            var subjectKeyIdentifier = extension.RawData.AsSpan(2);
            var rawData = new byte[subjectKeyIdentifier.Length + 4];
            rawData[0] = 0x30;
            rawData[1] = 0x16;
            rawData[2] = 0x80;
            rawData[3] = 0x14;
            subjectKeyIdentifier.CopyTo(rawData);

            return new X509Extension("2.5.29.35", rawData, false);
#endif
        }
    }
}
