using System;
using System.Security.Cryptography;

namespace McpHost.Utils
{
    static class HashUtil
    {
        public static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }
    }
}
