using System;
using System.Collections.Generic;
using System.Text;
using WaterMeterServer.Domain.Entities;

namespace WaterMeterServer.Domain.Interfaces
{
    public interface IDeviceRegistry
    {
        Task<Device> EnsureDeviceExistsAsync(string meterId);
        Task UpdateDeviceAtivityAsync(Device device);
    }
}
