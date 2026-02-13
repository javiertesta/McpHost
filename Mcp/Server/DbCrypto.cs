using System;
using System.Security.Cryptography;
using System.Text;

namespace McpHost.Server
{
    static class DbCrypto
    {
        // Same key/iv used by clases\clases\Segurida e integridad de datos\seguridad.vb
        static readonly byte[] Key =
        {
            225, 1, 97, 190, 215, 215, 178, 223, 50, 101, 214, 175,
            36, 93, 114, 241, 121, 233, 180, 17, 74, 98, 213, 12
        };

        static readonly byte[] Iv = { 108, 207, 233, 8, 116, 12, 45, 24 };

        public static string Decrypt(string cipherTextBase64)
        {
            if (string.IsNullOrWhiteSpace(cipherTextBase64))
                return "";

            byte[] input = Convert.FromBase64String(cipherTextBase64.Trim());
            using (var provider = new TripleDESCryptoServiceProvider())
            using (var transform = provider.CreateDecryptor(Key, Iv))
            {
                return Encoding.UTF8.GetString(Transform(input, transform));
            }
        }

        static byte[] Transform(byte[] input, ICryptoTransform cryptoTransform)
        {
            using (var memStream = new System.IO.MemoryStream())
            using (var cryptStream = new CryptoStream(memStream, cryptoTransform, CryptoStreamMode.Write))
            {
                cryptStream.Write(input, 0, input.Length);
                cryptStream.FlushFinalBlock();
                return memStream.ToArray();
            }
        }
    }
}
