using System.Buffers;
using System.Security.Cryptography;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Security
{
    public class AesCryptoService : ICryptoService
    {
        private byte[] _key = null!;

        public void SetPrivateKey(string hexKey) => _key = Convert.FromHexString(hexKey);

        public byte[] Decrypt(in ReadOnlySequence<byte> data)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            // طبق سند: استفاده از ECB و PKCS7 [cite: 94, 97]
            return aes.DecryptEcb(data.ToArray(), PaddingMode.PKCS7);
        }

        public byte[] Encrypt(ReadOnlySpan<byte> data)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            return aes.EncryptEcb(data, PaddingMode.PKCS7);
        }
    }
}