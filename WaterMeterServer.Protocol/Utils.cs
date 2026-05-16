using System.Buffers;
using System.Buffers.Binary;

namespace WaterMeterServer.Protocol
{
    public static class Utils
    {
        public static byte ByteToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10)); 

        public static byte ReadByteAt(ReadOnlySequence<byte> seq, int index)
        {
            return seq.Slice(index, 1).FirstSpan[0]; 
        }

        public static ushort ReadUInt16BigEndianAt(ReadOnlySequence<byte> seq, int index)
        {
            Span<byte> tmp = stackalloc byte[2];
            seq.Slice(index, 2).CopyTo(tmp);
            return BinaryPrimitives.ReadUInt16BigEndian(tmp); 
        }

        public static string ByteArrayToHexString(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }
    }
}