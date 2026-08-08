using System.Buffers;
using System.Security.Cryptography;
using WaterMeterServer.Domain.Interfaces;

namespace WaterMeterServer.Infrastructure.Security
{
    public class AesCryptoService : ICryptoService
    {
        private byte[] _key = Array.Empty<byte>();

        public AesCryptoService(string hexKey)
        {
            SetPrivateKey(hexKey);
        }

        public void SetPrivateKey(string hexKey)
        {
            var key = Convert.FromHexString(hexKey);
            if (key.Length is not (16 or 24 or 32))
            {
                throw new ArgumentException("The AES key must contain 16, 24, or 32 bytes.", nameof(hexKey));
            }

            _key = key;
        }

        public byte[] Decrypt(in ReadOnlySequence<byte> data)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            // طبق سند: استفاده از ECB و PKCS7 
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
