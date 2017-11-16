﻿using ICanPay.Core.Utils;
using ICanPay.Unionpay.Properties;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Store;
using System;
using System.Collections;
using System.IO;
using System.Text;

namespace ICanPay.Unionpay
{
    internal static class Util
    {
        #region 证书

#if DEBUG

        #region 测试证书

        private static X509Certificate ACPTESTENCCER = null;
        private static X509Certificate ACPTESTROOTCER = null;
        private static X509Certificate ACPTESTMIDDLECER = null;

        #endregion

#else

        #region 正式证书

        private static X509Certificate ACPPRODENCCER = null;
        private static X509Certificate ACPPRODROOTCER =null;
        private static X509Certificate ACPPRODMIDDLECER =null;

        #endregion

#endif

        #endregion

        #region 证书操作

        /// <summary>
        /// 获取Pkcs12Store
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        private static (Pkcs12Store Store, string Aliase) GetPkcs12Store(string path, string pwd)
        {
            using (FileStream fileStream = new FileStream(path, FileMode.Open))
            {
                Pkcs12Store store = new Pkcs12Store(fileStream, pwd.ToCharArray());
                foreach (string n in store.Aliases)
                {
                    if (store.IsKeyEntry(n))
                    {
                        return (store, n);
                    }
                }
            }

            return (null, null);
        }

        /// <summary>
        /// 获取证书编号
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        public static string GetCertId(string path, string pwd)
        {
            var store = GetPkcs12Store(path, pwd);
            return store
                .Store
                .GetCertificateChain(store.Aliase)[0]
                .Certificate
                .SerialNumber
                .ToString();
        }

        /// <summary>
        /// 获取证书编号
        /// </summary>
        /// <param name="path">证书路径</param>
        /// <param name="pwd">证书密码</param>
        /// <returns></returns>
        public static AsymmetricKeyParameter GetCertKey(string path, string pwd)
        {
            var store = GetPkcs12Store(path, pwd);
            return store
                .Store
                .GetKey(store.Aliase)
                .Key;
        }

        #endregion

        #region 签名

        /// <summary>
        /// 签名
        /// </summary>
        /// <param name="key">私钥</param>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static string Sign(AsymmetricKeyParameter key, string data)
        {
            var byteData = Encoding.UTF8.GetBytes(EncryptUtil.SHA256(data));
            ISigner normalSig = SignerUtilities.GetSigner("SHA256WithRSA");
            normalSig.Init(true, key);
            normalSig.BlockUpdate(byteData, 0, byteData.Length);
            byte[] normalResult = normalSig.GenerateSignature();
            return Convert.ToBase64String(normalResult);
        }

        #endregion

        #region 验签

        /// <summary>
        /// 验签
        /// </summary>
        /// <param name="data">数据</param>
        /// <param name="sign">签名</param>
        /// <param name="signPubKeyCert">验证公钥</param>
        /// <returns></returns>
        public static bool VerifyData(string data, string sign, string signPubKeyCert)
        {
            byte[] dataByte = Encoding.UTF8.GetBytes(EncryptUtil.SHA256(data));
            byte[] signByte = Convert.FromBase64String(sign);

            X509Certificate x509Cert = VerifyAndGetPubKey(signPubKeyCert);
            if (x509Cert == null)
            {
                return false;
            }
            return VerifySignature(x509Cert.GetPublicKey(), signByte, dataByte);
        }

        private static bool VerifySignature(AsymmetricKeyParameter key, byte[] base64DecodingSignStr, byte[] srcByte)
        {
            ISigner verifier = SignerUtilities.GetSigner("SHA256WithRSA");
            verifier.Init(false, key);
            verifier.BlockUpdate(srcByte, 0, srcByte.Length);
            return verifier.VerifySignature(base64DecodingSignStr);
        }

        private static X509Certificate VerifyAndGetPubKey(string signPubKeyCert)
        {
            X509Certificate x509Cert = GetPubKeyCert(signPubKeyCert);
            if (x509Cert == null)
            {
                return null;
            }

            return VerifyCertificate(x509Cert) ? x509Cert : null;
        }

        private static bool VerifyCertificate(X509Certificate x509Cert)
        {
            string cn = GetIdentitiesFromCertficate(x509Cert);
            try
            {
                x509Cert.CheckValidity();//验证有效期
                if (!VerifyCertificateChain(x509Cert))//验证书链
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            string unionpayCnName = "中国银联股份有限公司";

#if DEBUG

            // 验证公钥是否属于银联
            if (!unionpayCnName.Equals(cn))
            {
                return false;
            }

#else

            if (!unionpayCnName.Equals(cn) && !"00040000:SIGN".Equals(cn))
            {
                return false;
            }

#endif

            return true;
        }

        private static bool VerifyCertificateChain(X509Certificate cert)
        {
            if (cert is null)
            {
                return false;
            }

            X509Certificate rootCert = GetRootCert();
            if (rootCert is null)
            {
                return false;
            }

            X509Certificate middleCert = GetMiddleCert();
            if (middleCert is null)
            {
                return false;
            }

            try
            {
                X509CertStoreSelector selector = new X509CertStoreSelector
                {
                    Subject = cert.SubjectDN
                };

                ISet trustAnchors = new HashSet
                {
                    new TrustAnchor(rootCert, null)
                };
                PkixBuilderParameters pkixParams = new PkixBuilderParameters(trustAnchors, selector);

                IList intermediateCerts = new ArrayList
                {
                    rootCert,
                    middleCert,
                    cert
                };

                pkixParams.IsRevocationEnabled = false;

                IX509Store intermediateCertStore = X509StoreFactory.Create(
                    "Certificate/Collection",
                    new X509CollectionStoreParameters(intermediateCerts));
                pkixParams.AddStore(intermediateCertStore);

                PkixCertPathBuilder pathBuilder = new PkixCertPathBuilder();
                PkixCertPathBuilderResult result = pathBuilder.Build(pkixParams);
                PkixCertPath path = result.CertPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static X509Certificate GetCert(byte[] input)
        {
            try
            {
                return new X509CertificateParser().ReadCertificate(input);
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetRootCert()
        {
            try
            {
                X509Certificate cert;
#if DEBUG
                cert = ACPTESTROOTCER;
#else
                cert = ACPPRODROOTCER;
#endif
                if (cert is null)
                {
#if DEBUG
                    cert = GetCert(Resources.acp_test_root);
                    ACPTESTROOTCER = cert;
#else
                    cert= GetCert(Resources.acp_prod_root);
                    ACPPRODROOTCER = cert;
#endif
                }

                return cert;
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetMiddleCert()
        {
            try
            {
                X509Certificate cert;
#if DEBUG
                cert = ACPTESTMIDDLECER;
#else
                cert = ACPPRODMIDDLECER;
#endif
                if (cert is null)
                {
#if DEBUG
                    cert = GetCert(Resources.acp_test_middle);
                    ACPTESTMIDDLECER = cert;
#else
                    cert= GetCert(Resources.acp_prod_middle);
                    ACPPRODMIDDLECER = cert;
#endif
                }

                return cert;
            }
            catch
            {
                return null;
            }
        }

        private static X509Certificate GetEncCert()
        {
            try
            {
                X509Certificate cert;
#if DEBUG
                cert = ACPTESTENCCER;
#else
                cert = ACPPRODENCCER;
#endif
                if (cert is null)
                {
#if DEBUG
                    cert = GetCert(Resources.acp_test_enc);
                    ACPTESTENCCER = cert;
#else
                    cert= GetCert(Resources.acp_prod_enc);
                    ACPPRODENCCER = cert;
#endif
                }

                return cert;
            }
            catch
            {
                return null;
            }
        }

        private static string GetIdentitiesFromCertficate(X509Certificate aCert)
        {
            string tDN = aCert.SubjectDN.ToString();
            string tPart = "";
            if ((tDN != null))
            {
                string[] tSplitStr = tDN.Substring(tDN.IndexOf("CN=")).Split("@".ToCharArray());
                if (tSplitStr != null && tSplitStr.Length > 2 && tSplitStr[2] != null)
                {
                    tPart = tSplitStr[2];
                }
            }
            return tPart;
        }

        private static X509Certificate GetPubKeyCert(string pubKeyCert)
        {
            try
            {
                pubKeyCert = pubKeyCert
                    .Replace("-----END CERTIFICATE-----", "")
                    .Replace("-----BEGIN CERTIFICATE-----", "");
                byte[] x509CertBytes = Convert.FromBase64String(pubKeyCert);
                X509CertificateParser cf = new X509CertificateParser();
                X509Certificate x509Cert = cf.ReadCertificate(x509CertBytes);
                return x509Cert;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 解密

        /// <summary>
        /// 解密
        /// </summary>
        /// <param name="key">私钥</param>
        /// <param name="data">数据</param>
        /// <returns></returns>
        public static string DecryptData(AsymmetricKeyParameter key, string data)
        {
            byte[] dataByte = Convert.FromBase64String(data);
            IBufferedCipher c = CipherUtilities.GetCipher("RSA/NONE/PKCS1Padding");
            c.Init(false, key);
            return Encoding.UTF8.GetString(c.DoFinal(dataByte));
        }

        #endregion
    }
}