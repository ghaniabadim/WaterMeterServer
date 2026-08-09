using System.Buffers.Binary;

namespace WaterMeterServer.Protocol
{
    public static class FotaPayloadParser
    {
        public const int ReportedObjectsOffset = 11;

        public static bool TryFindReportedObject(
            ReadOnlySpan<byte> decryptedData,
            ushort targetObjectId,
            out byte[] objectContent)
        {
            objectContent = Array.Empty<byte>();
            if (decryptedData.Length <= ReportedObjectsOffset)
            {
                return false;
            }

            int currentOffset = ReportedObjectsOffset;
            byte objectCount = decryptedData[currentOffset++];

            for (int index = 0; index < objectCount; index++)
            {
                if (currentOffset + 3 > decryptedData.Length)
                {
                    return false;
                }

                ushort objectId = BinaryPrimitives.ReadUInt16BigEndian(
                    decryptedData.Slice(currentOffset, 2));
                currentOffset += 2;

                int objectLength = decryptedData[currentOffset++];
                if (currentOffset + objectLength > decryptedData.Length)
                {
                    return false;
                }

                if (objectId == targetObjectId)
                {
                    objectContent = decryptedData.Slice(currentOffset, objectLength).ToArray();
                    return true;
                }

                currentOffset += objectLength;
            }

            return false;
        }
    }
}
