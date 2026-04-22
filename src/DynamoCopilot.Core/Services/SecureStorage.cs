using System;
using System.Security.Cryptography;
using System.Text;

namespace DynamoCopilot.Core.Services
{
    // Wraps Windows DPAPI (ProtectedData) so tokens and API keys on disk are
    // encrypted and bound to the OS user account. Another user or process
    // running under a different account cannot decrypt the data.
    internal static class SecureStorage
    {
        internal static byte[] Encrypt(string plaintext)
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        }

        internal static string Decrypt(byte[] ciphertext)
        {
            var bytes = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }

        // Returns false when the bytes are not a valid DPAPI blob (e.g. a legacy plaintext file).
        internal static bool TryDecrypt(byte[] data, out string? plaintext)
        {
            try
            {
                plaintext = Decrypt(data);
                return true;
            }
            catch (CryptographicException)
            {
                plaintext = null;
                return false;
            }
        }
    }
}
