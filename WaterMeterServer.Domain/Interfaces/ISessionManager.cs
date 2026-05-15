namespace WaterMeterServer.Domain.Interfaces
{
    public interface ISessionManager
    {
        uint GenerateSessionId(string meterSerialNumber);
        bool ValidateSession(string meterId, uint sessionId);
        void RemoveSession(string meterId);
    }
}