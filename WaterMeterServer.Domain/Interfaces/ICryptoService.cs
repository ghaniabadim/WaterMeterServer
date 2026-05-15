using System.Buffers;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface ICryptoService
    {
        byte[] Decrypt(in ReadOnlySequence<byte> data);
        byte[] Encrypt(ReadOnlySpan<byte> data);
        void SetPrivateKey(string hexKey);
    }
}