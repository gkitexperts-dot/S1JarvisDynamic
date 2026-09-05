using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Decrypts the provider credential envelope returned by
    /// POST /api/licensing/v1/verify. The recognition secret is used only as
    /// transport key material and plaintext provider credentials are never logged
    /// or persisted by this class.
    /// </summary>
    internal static class VerilicNativeS1CredentialDecryptor
    {
        private const string KdfContext = "verilic-ai-runtime-v1";
        private const string ExpectedAlgorithm = "A256GCM";
        private const string ExpectedKdf = "HMAC-SHA256/verilic-ai-runtime-v1";
        private const int NonceBytes = 12;
        private const int TagBytes = 16;

        public static string Decrypt(
            Newtonsoft.Json.Linq.JObject credential,
            string recognitionSecret,
            string decisionId,
            string callerProductId,
            string targetProductId,
            string contractId,
            string agentAccountRef,
            string model)
        {
            if (credential == null)
                throw new CryptographicException("NativeS1 credential envelope is missing.");
            if (string.IsNullOrWhiteSpace(recognitionSecret))
                throw new CryptographicException("NativeS1 recognition secret is missing.");

            string algorithm = (string)credential["algorithm"];
            string kdf = (string)credential["kdf"];
            if (!string.Equals(algorithm, ExpectedAlgorithm, StringComparison.Ordinal) ||
                !string.Equals(kdf, ExpectedKdf, StringComparison.Ordinal))
                throw new CryptographicException("NativeS1 credential envelope algorithm is unsupported.");

            byte[] nonce = DecodeRequired((string)credential["nonce"], "nonce");
            byte[] ciphertext = DecodeRequired((string)credential["ciphertext"], "ciphertext");
            byte[] tag = DecodeRequired((string)credential["tag"], "tag");
            if (nonce.Length != NonceBytes || tag.Length != TagBytes || ciphertext.Length == 0)
                throw new CryptographicException("NativeS1 credential envelope size is invalid.");

            byte[] secretBytes = Encoding.UTF8.GetBytes(recognitionSecret);
            byte[] contextBytes = Encoding.UTF8.GetBytes(KdfContext + "\n" + callerProductId);
            byte[] key = null;
            byte[] plaintext = null;
            try
            {
                using (var hmac = new HMACSHA256(secretBytes))
                    key = hmac.ComputeHash(contextBytes);

                string aadText = string.Join("\n", new[]
                {
                    KdfContext,
                    decisionId ?? string.Empty,
                    callerProductId ?? string.Empty,
                    targetProductId ?? string.Empty,
                    contractId ?? string.Empty,
                    agentAccountRef ?? string.Empty,
                    model ?? string.Empty
                });
                byte[] aad = Encoding.UTF8.GetBytes(aadText);
                plaintext = AesGcmWindows.Decrypt(key, nonce, ciphertext, tag, aad);
                string value = Encoding.UTF8.GetString(plaintext);
                if (string.IsNullOrWhiteSpace(value))
                    throw new CryptographicException("NativeS1 credential plaintext is empty.");
                return value;
            }
            finally
            {
                Clear(secretBytes);
                Clear(contextBytes);
                Clear(key);
                Clear(plaintext);
                Clear(nonce);
                Clear(ciphertext);
                Clear(tag);
            }
        }

        private static byte[] DecodeRequired(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new CryptographicException("NativeS1 credential " + name + " is missing.");
            try { return Convert.FromBase64String(value); }
            catch (FormatException ex)
            {
                throw new CryptographicException("NativeS1 credential " + name + " is invalid Base64.", ex);
            }
        }

        private static void Clear(byte[] value)
        {
            if (value != null)
                Array.Clear(value, 0, value.Length);
        }

        // .NET Framework 4.8 does not expose System.Security.Cryptography.AesGcm.
        // Use the Windows CNG AES-GCM primitive already present on supported
        // Windows hosts instead of adding another crypto dependency to the Soft1 DLL.
        private static class AesGcmWindows
        {
            private const string Bcrypt = "bcrypt.dll";
            private const string AesAlgorithm = "AES";
            private const string ChainingModeProperty = "ChainingMode";
            private const string ChainingModeGcm = "ChainingModeGCM";
            private const string ObjectLengthProperty = "ObjectLength";
            private const int AuthInfoVersion = 1;

            [StructLayout(LayoutKind.Sequential)]
            private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
            {
                public int cbSize;
                public int dwInfoVersion;
                public IntPtr pbNonce;
                public int cbNonce;
                public IntPtr pbAuthData;
                public int cbAuthData;
                public IntPtr pbTag;
                public int cbTag;
                public IntPtr pbMacContext;
                public int cbMacContext;
                public int cbAAD;
                public long cbData;
                public int dwFlags;
            }

            [DllImport(Bcrypt, CharSet = CharSet.Unicode)]
            private static extern int BCryptOpenAlgorithmProvider(
                out IntPtr phAlgorithm,
                string pszAlgId,
                string pszImplementation,
                int dwFlags);

            [DllImport(Bcrypt, CharSet = CharSet.Unicode)]
            private static extern int BCryptSetProperty(
                IntPtr hObject,
                string pszProperty,
                byte[] pbInput,
                int cbInput,
                int dwFlags);

            [DllImport(Bcrypt, CharSet = CharSet.Unicode)]
            private static extern int BCryptGetProperty(
                IntPtr hObject,
                string pszProperty,
                byte[] pbOutput,
                int cbOutput,
                out int pcbResult,
                int dwFlags);

            [DllImport(Bcrypt)]
            private static extern int BCryptGenerateSymmetricKey(
                IntPtr hAlgorithm,
                out IntPtr phKey,
                byte[] pbKeyObject,
                int cbKeyObject,
                byte[] pbSecret,
                int cbSecret,
                int dwFlags);

            [DllImport(Bcrypt)]
            private static extern int BCryptDecrypt(
                IntPtr hKey,
                byte[] pbInput,
                int cbInput,
                ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
                byte[] pbIV,
                int cbIV,
                byte[] pbOutput,
                int cbOutput,
                out int pcbResult,
                int dwFlags);

            [DllImport(Bcrypt)]
            private static extern int BCryptDestroyKey(IntPtr hKey);

            [DllImport(Bcrypt)]
            private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, int dwFlags);

            public static byte[] Decrypt(
                byte[] key,
                byte[] nonce,
                byte[] ciphertext,
                byte[] tag,
                byte[] aad)
            {
                IntPtr algorithm = IntPtr.Zero;
                IntPtr keyHandle = IntPtr.Zero;
                byte[] keyObject = null;
                GCHandle noncePin = default(GCHandle);
                GCHandle aadPin = default(GCHandle);
                GCHandle tagPin = default(GCHandle);

                try
                {
                    Check(BCryptOpenAlgorithmProvider(out algorithm, AesAlgorithm, null, 0),
                        "open AES provider");

                    byte[] mode = Encoding.Unicode.GetBytes(ChainingModeGcm + "\0");
                    Check(BCryptSetProperty(algorithm, ChainingModeProperty, mode, mode.Length, 0),
                        "enable AES-GCM");

                    byte[] objectLengthBytes = new byte[4];
                    int ignored;
                    Check(BCryptGetProperty(
                        algorithm,
                        ObjectLengthProperty,
                        objectLengthBytes,
                        objectLengthBytes.Length,
                        out ignored,
                        0),
                        "read AES key object size");

                    int objectLength = BitConverter.ToInt32(objectLengthBytes, 0);
                    if (objectLength <= 0)
                        throw new CryptographicException("Windows AES key object size is invalid.");
                    keyObject = new byte[objectLength];

                    Check(BCryptGenerateSymmetricKey(
                        algorithm,
                        out keyHandle,
                        keyObject,
                        keyObject.Length,
                        key,
                        key.Length,
                        0),
                        "create AES-GCM key");

                    noncePin = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                    tagPin = GCHandle.Alloc(tag, GCHandleType.Pinned);
                    if (aad != null && aad.Length > 0)
                        aadPin = GCHandle.Alloc(aad, GCHandleType.Pinned);

                    var auth = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
                    {
                        cbSize = Marshal.SizeOf(typeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO)),
                        dwInfoVersion = AuthInfoVersion,
                        pbNonce = noncePin.AddrOfPinnedObject(),
                        cbNonce = nonce.Length,
                        pbAuthData = aadPin.IsAllocated ? aadPin.AddrOfPinnedObject() : IntPtr.Zero,
                        cbAuthData = aad == null ? 0 : aad.Length,
                        pbTag = tagPin.AddrOfPinnedObject(),
                        cbTag = tag.Length,
                        pbMacContext = IntPtr.Zero,
                        cbMacContext = 0,
                        cbAAD = 0,
                        cbData = 0,
                        dwFlags = 0
                    };

                    byte[] plaintext = new byte[ciphertext.Length];
                    int written;
                    int status = BCryptDecrypt(
                        keyHandle,
                        ciphertext,
                        ciphertext.Length,
                        ref auth,
                        null,
                        0,
                        plaintext,
                        plaintext.Length,
                        out written,
                        0);
                    if (status != 0)
                    {
                        Clear(plaintext);
                        throw new CryptographicException(
                            "NativeS1 AES-GCM authentication/decryption failed (0x" +
                            status.ToString("X8") + ").");
                    }
                    if (written != plaintext.Length)
                    {
                        byte[] exact = new byte[written];
                        Buffer.BlockCopy(plaintext, 0, exact, 0, written);
                        Clear(plaintext);
                        plaintext = exact;
                    }
                    return plaintext;
                }
                finally
                {
                    if (tagPin.IsAllocated) tagPin.Free();
                    if (aadPin.IsAllocated) aadPin.Free();
                    if (noncePin.IsAllocated) noncePin.Free();
                    if (keyHandle != IntPtr.Zero) BCryptDestroyKey(keyHandle);
                    if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
                    Clear(keyObject);
                }
            }

            private static void Check(int status, string operation)
            {
                if (status != 0)
                    throw new CryptographicException(
                        "Windows CNG failed to " + operation + " (0x" + status.ToString("X8") + ").");
            }
        }
    }
}
