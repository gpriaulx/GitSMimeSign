﻿// Copyright (c) 2019 Glenn Watson. All rights reserved.
// Glenn Watson licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

using SMimeSigner.Helpers;

namespace SMimeSigner.Actions
{
    /// <summary>
    /// Verifies the data.
    /// </summary>
    internal static class VerifyAction
    {
        // NEWSIG [<signers_uid>]
        //   Is issued right before a signature verification starts.  This is
        //   useful to define a context for parsing ERROR status messages.
        //   arguments are currently defined.  If SIGNERS_UID is given and is
        //   not "-" this is the percent escape value of the OpenPGP Signer's
        //   User ID signature sub-packet.
        private const string NewSig = "NEWSIG";

        // BADSIG <long_keyid_or_fpr> <username>
        //   The signature with the keyid has not been verified okay. The username is
        //   the primary one encoded in UTF-8 and %XX escaped. The fingerprint may be
        //   used instead of the long keyid if it is available. This is the case with
        //   CMS and might eventually also be available for OpenPGP.
        private const string BadSignature = "BADSIG";

        // GOODSIG  <long_keyid_or_fpr>  <username>
        //   The signature with the keyid is good.  For each signature only one
        //   of the codes GOODSIG, BADSIG, EXPSIG, EXPKEYSIG, REVKEYSIG or
        //   ERRSIG will be emitted.  In the past they were used as a marker
        //   for a new signature; new code should use the NEWSIG status
        //   instead.  The username is the primary one encoded in UTF-8 and %XX
        //   escaped. The fingerprint may be used instead of the long keyid if
        //   it is available.  This is the case with CMS and might eventually
        //   also be available for OpenPGP.
        private const string GoodSignature = "GOODSIG";

        // ERRSIG <keyid> <pkalgo> <hashalgo> <sig_class> <time> <rc>
        //
        //   It was not possible to check the signature. This may be caused by a
        //   missing public key or an unsupported algorithm. A RC of 4 indicates
        //   unknown algorithm, a 9 indicates a missing public key. The other fields
        //   give more information about this signature. sig_class is a 2 byte hex-
        //   value. The fingerprint may be used instead of the keyid if it is
        //   available. This is the case with gpgsm and might eventually also be
        //  available for OpenPGP.
        //
        //   Note, that TIME may either be the number of seconds since Epoch or an ISO
        //   8601 string. The latter can be detected by the presence of the letter
        //   ‘T’.
        private const string ErrorSignature = "ERRSIG";

        // TRUST_
        //   These are several similar status codes:
        //
        //   - TRUST_UNDEFINED <error_token>
        //   - TRUST_NEVER     <error_token>
        //   - TRUST_MARGINAL  [0  [<validation_model>]]
        //   - TRUST_FULLY     [0  [<validation_model>]]
        //   - TRUST_ULTIMATE  [0  [<validation_model>]]
        //
        //   For good signatures one of these status lines are emitted to
        //   indicate the validity of the key used to create the signature.
        //   The error token values are currently only emitted by gpgsm.
        //
        //   VALIDATION_MODEL describes the algorithm used to check the
        //   validity of the key.  The defaults are the standard Web of Trust
        //   model for gpg and the standard X.509 model for gpgsm.  The
        //   defined values are
        //
        //      - pgp   :: The standard PGP WoT.
        //      - shell :: The standard X.509 model.
        //      - chain :: The chain model.
        //      - steed :: The STEED model.
        //      - tofu  :: The TOFU model
        //
        //   Note that the term =TRUST_= in the status names is used for
        //   historic reasons; we now speak of validity.
        private const string FullyTrusted = "TRUST_FULLY";

        public static int Do(string[] fileNames)
        {
            Console.WriteLine(NewSig);

            if (fileNames.Length < 2)
            {
                VerifyAttached(fileNames.FirstOrDefault());
            }
            else
            {
                VerifyDetached(fileNames);
            }

            return 0;
        }

        private static void VerifyAttached(string fileName)
        {
            var bytes = FileSystemStreamHelper.ReadFileStreamFully(fileName);

            PemHelper.TryDecode(bytes, out var body);
            VerifySignedData(new SignedCms(), body);
        }

        private static void VerifyDetached(IReadOnlyList<string> fileNames)
        {
            var signatureBytes = FileSystemStreamHelper.ReadFileStreamFully(fileNames[0]);

            var signedDataBytes = FileSystemStreamHelper.ReadFileStreamFully(fileNames[1]);

            var contentInfo = new ContentInfo(signedDataBytes);

            // Create a new, detached SignedCms message.
            var signedCms = new SignedCms(contentInfo, true);

            PemHelper.TryDecode(signatureBytes, out var signatureBody);
            VerifySignedData(signedCms, signatureBody);
        }

        private static void VerifySignedData(SignedCms signedCms, byte[] body)
        {
            try
            {
                signedCms.Decode(body);

                signedCms.CheckSignature(false);

                if (signedCms.SignerInfos.Count == 0)
                {
                    throw new CryptographicException("Must have valid signing information. There is none in the signature.");
                }

                var issuedCertificate = signedCms.SignerInfos[0].Certificate;

                foreach (var signedInfo in signedCms.SignerInfos)
                {
                    if (CheckSignerInfo(signedInfo, issuedCertificate.NotBefore, issuedCertificate.NotAfter) == false)
                    {
                        throw new CryptographicException("There is not a valid signing timestamp authority stamp.");
                    }
                }

                WriteGpgCertificateData(GoodSignature, signedCms.Certificates);
                WriteSigningInformation(issuedCertificate, true, signedCms);

                GpgOutputHelper.WriteLine($"{FullyTrusted} 0 shell"); // This indicates we fully trust using the x509 model.
            }
            catch (Exception)
            {
                if (signedCms.Certificates.Count == 0)
                {
                    GpgOutputHelper.WriteLine(ErrorSignature);
                }
                else
                {
                    var issuedCertificate = signedCms.SignerInfos[0].Certificate;
                    WriteGpgCertificateData(BadSignature, signedCms.Certificates);
                    WriteSigningInformation(issuedCertificate, false, signedCms);
                }

                throw;
            }
        }

        /// <summary>
        /// Write out the format in the GPG format.
        /// </summary>
        /// <param name="type">The GPG type, if it's good or bad usually.</param>
        /// <param name="certificates">The certificates to write.</param>
        private static void WriteGpgCertificateData(string type, X509Certificate2Collection certificates)
        {
            foreach (var certificate in certificates)
            {
                GpgOutputHelper.WriteLine($"{type} {certificate.Thumbprint} {certificate.Subject}");
            }
        }

        /// <summary>
        /// Write out in user friendly format information about the signing.
        /// </summary>
        /// <param name="issuedCertificate">The issued certificate.</param>
        /// <param name="goodSignature">If this is a good signature or not.</param>
        /// <param name="signedCms">The information about the signing.</param>
        private static void WriteSigningInformation(X509Certificate2 issuedCertificate, bool goodSignature, SignedCms signedCms)
        {
            InfoOutputHelper.WriteLine($"Signature made using certificate ID 0x{issuedCertificate.Thumbprint}");

            foreach (var signerInfo in signedCms.SignerInfos)
            {
                foreach (var attributeObject in signerInfo.SignedAttributes)
                {
                    if (attributeObject.Values[0] is Pkcs9SigningTime signingTime)
                    {
                        InfoOutputHelper.WriteLine($"Signature made at {signingTime.SigningTime.ToString("O", CultureInfo.InvariantCulture)}");
                    }
                }
            }

            InfoOutputHelper.WriteLine($"Signature issued by '{issuedCertificate.Issuer}'");

            if (goodSignature)
            {
                InfoOutputHelper.WriteLine($"Good signature from '{issuedCertificate.Subject}'");
            }
            else
            {
                InfoOutputHelper.WriteLine($"Bad signature from '{issuedCertificate.Subject}'");
            }
        }

        private static bool? CheckSignerInfo(SignerInfo signerInfo, DateTimeOffset? notBefore, DateTimeOffset? notAfter)
        {
            const string TimeStampTokenOid = "1.2.840.113549.1.9.16.2.14";
            bool found = false;
            byte[] signatureBytes = null;

            foreach (CryptographicAttributeObject attr in signerInfo.UnsignedAttributes)
            {
                if (attr.Oid.Value == TimeStampTokenOid)
                {
                    foreach (AsnEncodedData attrInst in attr.Values)
                    {
                        byte[] attrData = attrInst.RawData;

                        // New API starts here:
                        if (!Rfc3161TimestampToken.TryDecode(attrData, out var token, out var bytesRead))
                        {
                            return false;
                        }

                        if (bytesRead != attrData.Length)
                        {
                            return false;
                        }

                        signatureBytes = signatureBytes ?? signerInfo.GetSignature();

                        // Check that the token was issued based on the SignerInfo's signature value
                        if (!token.VerifySignatureForSignerInfo(signerInfo, out var _))
                        {
                            return false;
                        }

                        var timestamp = token.TokenInfo.Timestamp;

                        // Check that the signed timestamp is within the provided policy range
                        // (which may be (signerInfo.Certificate.NotBefore, signerInfo.Certificate.NotAfter);
                        // or some other policy decision)
                        if (timestamp < notBefore.GetValueOrDefault(timestamp) ||
                            timestamp > notAfter.GetValueOrDefault(timestamp))
                        {
                            return false;
                        }

                        var tokenSignerCert = token.AsSignedCms().SignerInfos[0].Certificate;

                        // Implicit policy decision: Tokens required embedded certificates (since this method has
                        // no resolver)
                        if (tokenSignerCert == null)
                        {
                            return false;
                        }

                        found = true;
                    }
                }
            }

            // If we found any attributes and none of them returned an early false, then the SignerInfo is
            // conformant to policy.
            if (found)
            {
                return true;
            }

            // Inconclusive, as no signed timestamps were found
            return null;
        }
    }
}
