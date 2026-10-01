using System;
using System.Security.Cryptography;
using System.Text;

namespace SsmsAutoConnect
{
    /// <summary>DPAPI (CurrentUser) protection for SQL-login passwords stored in connections.xml.
    /// Shared with the SsmsAutoConnect.Protect console tool.</summary>
    public static class PasswordProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SsmsAutoConnect.v1");

        public static string Protect(string plainText)
        {
            byte[] data = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
            return Convert.ToBase64String(ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string protectedBase64)
        {
            byte[] data = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
    }
}
