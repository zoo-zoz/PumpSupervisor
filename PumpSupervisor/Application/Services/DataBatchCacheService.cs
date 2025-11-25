using PumpSupervisor.Domain.Models;
using System.Collections.Concurrent;

namespace PumpSupervisor.Application.Services
{
    public interface IDataBatchCacheService
    {
        void Cache(string connectionId, string deviceId, ModbusDataBatch batch);

        ModbusDataBatch? GetAndRemove(string connectionId, string deviceId);
    }

    public class DataBatchCacheService : IDataBatchCacheService
    {
        private readonly ConcurrentDictionary<string, ModbusDataBatch> _cache = new();

        public void Cache(string connectionId, string deviceId, ModbusDataBatch batch)
        {
            var key = $"{connectionId}:{deviceId}";
            _cache[key] = batch;
        }

        public ModbusDataBatch? GetAndRemove(string connectionId, string deviceId)
        {
            var key = $"{connectionId}:{deviceId}";
            _cache.TryRemove(key, out var batch);
            return batch;
        }
    }
}