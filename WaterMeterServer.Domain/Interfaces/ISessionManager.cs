namespace WaterMeterServer.Domain.Interfaces
{
    public interface ISessionManager
    {
        uint GenerateSessionId(string meterSerialNumber);
        bool ValidateSession(string meterId, uint sessionId);

        bool ValidateSessionAndSequence(string meterId, uint sessionId, byte incomingMid);

        void RemoveSession(string meterId);
        uint? GetSession(string meterId);

        string? GetMeterIdBySession(uint sessionId);
    }
}