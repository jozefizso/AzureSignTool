﻿using AzureSignTool.Interop;
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AzureSignTool
{
    public class AuthenticodeKeyVaultSigner : IDisposable
    {
        private readonly AzureKeyVaultMaterializedConfiguration _configuration;
        private readonly TimeStampConfiguration _timeStampConfiguration;
        private readonly MemoryCertificateStore _certificateStore;
        private readonly X509Chain _chain;
        private readonly ILogger _logger;
        private readonly SignCallback _signCallback;

        public AuthenticodeKeyVaultSigner(AzureKeyVaultMaterializedConfiguration configuration, TimeStampConfiguration timeStampConfiguration, X509Certificate2Collection additionalCertificates,
            ILogger logger)
        {
            _logger = logger;
            _timeStampConfiguration = timeStampConfiguration;
            _configuration = configuration;
            _certificateStore = MemoryCertificateStore.Create();
            _chain = new X509Chain();
            _chain.ChainPolicy.ExtraStore.AddRange(additionalCertificates);
            //We don't care about the trustworthiness of the cert. We just want a chain to sign with.
            _chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;


            if (!_chain.Build(_configuration.PublicCertificate))
            {
                throw new InvalidOperationException("Failed to build chain for certificate.");
            }
            for (var i = 0; i < _chain.ChainElements.Count; i++)
            {
                _certificateStore.Add(_chain.ChainElements[i].Certificate);
            }
            _signCallback = SignCallback;
        }

        public unsafe int SignFile(ReadOnlySpan<char> path, ReadOnlySpan<char> description, ReadOnlySpan<char> descriptionUrl, bool? pageHashing)
        {
            void CopyAndNullTerminate(ReadOnlySpan<char> str, Span<char> destination)
            {
                str.CopyTo(destination);
                destination[destination.Length - 1] = '\0';
            }

            var flags = SignerSignEx3Flags.SIGN_CALLBACK_UNDOCUMENTED;
            if (pageHashing == true)
            {
                flags |= SignerSignEx3Flags.SPC_INC_PE_PAGE_HASHES_FLAG;
            }
            else if (pageHashing == false)
            {
                flags |= SignerSignEx3Flags.SPC_EXC_PE_PAGE_HASHES_FLAG;
            }

            SignerSignTimeStampFlags timeStampFlags;
            ReadOnlySpan<byte> timestampAlgorithmOid;
            string timestampUrl;
            switch (_timeStampConfiguration.Type)
            {
                case TimeStampType.Authenticode:
                    timeStampFlags = SignerSignTimeStampFlags.SIGNER_TIMESTAMP_AUTHENTICODE;
                    timestampAlgorithmOid = default;
                    timestampUrl = _timeStampConfiguration.Url;
                    break;
                case TimeStampType.RFC3161:
                    timeStampFlags = SignerSignTimeStampFlags.SIGNER_TIMESTAMP_RFC3161;
                    timestampAlgorithmOid = AlgorithmTranslator.HashAlgorithmToOidAsciiTerminated(_timeStampConfiguration.DigestAlgorithm);
                    timestampUrl = _timeStampConfiguration.Url;
                    break;
                default:
                    timeStampFlags = 0;
                    timestampAlgorithmOid = default;
                    timestampUrl = null;
                    break;
            }

            Span<char> pathWithNull = path.Length > 0x100 ? new char[path.Length + 1] : stackalloc char[path.Length + 1];
            Span<char> descriptionWithNull = description.Length > 0x100 ? new char[description.Length + 1] : stackalloc char[description.Length + 1];
            Span<char> descriptionUrlWithNull = descriptionUrl.Length > 0x100 ? new char[descriptionUrl.Length + 1] : stackalloc char[descriptionUrl.Length + 1];
            Span<char> timestampUrlWithNull = timestampUrl == null ?
                default : timestampUrl.Length > 0x100 ?
                new char[timestampUrl.Length + 1] : stackalloc char[timestampUrl.Length + 1];

            CopyAndNullTerminate(path, pathWithNull);
            CopyAndNullTerminate(description, descriptionWithNull);
            CopyAndNullTerminate(descriptionUrl, descriptionUrlWithNull);
            if (timestampUrl != null)
            {
                CopyAndNullTerminate(timestampUrl, timestampUrlWithNull);
            }

            fixed (byte* pTimestampAlgorithm = &timestampAlgorithmOid.GetPinnableReference())
            fixed (char* pTimestampUrlWithNull = &timestampUrlWithNull.GetPinnableReference())
            fixed (char* pPathWithNull = &pathWithNull.GetPinnableReference())
            fixed (char* pDescriptionWithNull = &descriptionWithNull.GetPinnableReference())
            fixed (char* pDescriptionUrlWithNull = &descriptionUrlWithNull.GetPinnableReference())
            {
                var fileInfo = new SIGNER_FILE_INFO(pPathWithNull, default);
                var subjectIndex = 0u;
                var signerSubjectInfoUnion = new SIGNER_SUBJECT_INFO_UNION(&fileInfo);
                var subjectInfo = new SIGNER_SUBJECT_INFO(&subjectIndex, SignerSubjectInfoUnionChoice.SIGNER_SUBJECT_FILE, signerSubjectInfoUnion);
                var authCodeStructure = new SIGNER_ATTR_AUTHCODE(pDescriptionWithNull, pDescriptionUrlWithNull);
                var storeInfo = new SIGNER_CERT_STORE_INFO(
                    dwCertPolicy: SignerCertStoreInfoFlags.SIGNER_CERT_POLICY_CHAIN,
                    hCertStore: _certificateStore.Handle,
                    pSigningCert: _configuration.PublicCertificate.Handle
                );
                var signerCert = new SIGNER_CERT(
                    dwCertChoice: SignerCertChoice.SIGNER_CERT_STORE,
                    union: new SIGNER_CERT_UNION(&storeInfo)
                );
                var signatureInfo = new SIGNER_SIGNATURE_INFO(
                    algidHash: AlgorithmTranslator.HashAlgorithmToAlgId(_configuration.FileDigestAlgorithm),
                    psAuthenticated: IntPtr.Zero,
                    psUnauthenticated: IntPtr.Zero,
                    dwAttrChoice: SignerSignatureInfoAttrChoice.SIGNER_AUTHCODE_ATTR,
                    attrAuthUnion: new SIGNER_SIGNATURE_INFO_UNION(&authCodeStructure)
                );
                var callbackPtr = Marshal.GetFunctionPointerForDelegate(_signCallback);
                var signCallbackInfo = new SIGN_INFO(callbackPtr);


                _logger.Log("Getting SIP Data", LogLevel.Verbose);
                IntPtr context = default;
                _logger.Log("Calling SignerSignEx3", LogLevel.Verbose);
                return mssign32.SignerSignEx3
                (
                    flags, //TODO
                    &subjectInfo,
                    &signerCert,
                    &signatureInfo,
                    IntPtr.Zero,
                    timeStampFlags,
                    pTimestampAlgorithm,
                    pTimestampUrlWithNull,
                    IntPtr.Zero,
                    IntPtr.Zero, //TODO
                    &context,
                    IntPtr.Zero,
                    &signCallbackInfo,
                    IntPtr.Zero
                );
            }
        }

        public void Dispose()
        {
            _chain.Dispose();
            _certificateStore.Close();
        }

        private int SignCallback(
            IntPtr pCertContext,
            IntPtr pvExtra,
            uint algId,
            byte[] pDigestToSign,
            uint dwDigestToSign,
            ref CRYPTOAPI_BLOB blob
        )
        {
            _logger.Log("SignCallback", LogLevel.Verbose);
            var context = new KeyVaultSigningContext(_configuration, _logger);
            var result = context.SignDigestAsync(pDigestToSign).ConfigureAwait(false).GetAwaiter().GetResult();
            var resultPtr = Marshal.AllocHGlobal(result.Length);
            Marshal.Copy(result, 0, resultPtr, result.Length);
            blob.pbData = resultPtr;
            blob.cbData = (uint)result.Length;
            return 0;
        }
    }
}
