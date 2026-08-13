namespace WaterMeterServer.Domain.Constants
{
    public static class ProtocolConstants
    {
        public const byte Head =                    0x68;
        public const byte Tail =                    0x16;

        public const byte TypeTransport =           0x04;
        public const byte TypeHandshake =           0x05;


        public const ushort HandshakeSuccess =      0x0000;
        public const ushort HandshakeFailure =      0x0001;

        // کدهای تابع طبق بخش 7.1.1 سند
        public const byte ControlReporting =        0x01;
        public const byte ControlDistribution =     0x02;
        public const byte ControlEndFrame =         0x05;

        public const byte FunCodeEndCommunication = 0x02;
        public const byte FunCodeDataDistribution = 0x02;
        public const byte FunCodeResume =           0x03;

        public const byte FunCodeReadData =         0x04;
        public const byte FunCodeWriteData =        0x05;
        public const byte FunCodeReadRecordsByTime= 0x07;
        public const byte FunCodeReadRecentRecords= 0x08;

        public const byte FunCodeResponseReadData = 0x84;
        public const byte FunCodeResponseWriteData = 0x85;
        public const byte FunCodeResponseReadRecordsByTime = 0x87;
        public const byte FunCodeResponseReadRecentRecords = 0x88;
        public const byte FunCodeNegativeReadRecordsByTime = 0xC7;
        public const byte FunCodeNegativeReadRecentRecords = 0xC8;

        // شناسه‌های اشیاء پرکاربرد طبق Appendix A
        public const ushort ObjId_RealTimeData = 0x70F2;
        public const ushort ObjId_AlarmStatus = 0xB070;
    }
}
