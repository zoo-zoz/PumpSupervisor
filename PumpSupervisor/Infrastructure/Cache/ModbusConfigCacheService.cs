using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Configuration;

namespace PumpSupervisor.Infrastructure.Cache
{
    public interface IModbusConfigCacheService
    {
        Task<ModbusConfig?> GetConfigAsync();

        Task RefreshConfigAsync();

        Task<ModbusConnectionConfig?> GetConnectionConfigAsync(string connectionId);

        Task<DeviceConfig?> GetDeviceConfigAsync(string connectionId, string deviceId);
    }

    public class ModbusConfigCacheService : IModbusConfigCacheService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<ModbusConfigCacheService> _logger;
        private readonly IModbusConfigLoader _configLoader;
        private const string CacheKey = "ModbusConfig";
        private readonly string _configPath;

        public ModbusConfigCacheService(
            IMemoryCache cache,
            ILogger<ModbusConfigCacheService> logger,
            IModbusConfigLoader configLoader)
        {
            _cache = cache;
            _logger = logger;
            _configLoader = configLoader;
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readModbus.json");
        }

        public async Task<ModbusConfig?> GetConfigAsync()
        {
            if (_cache.TryGetValue(CacheKey, out ModbusConfig? config))
            {
                return config;
            }

            await RefreshConfigAsync();
            return _cache.Get<ModbusConfig>(CacheKey);
        }

        public async Task RefreshConfigAsync()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _logger.LogError("配置文件不存在: {Path}", _configPath);
                    return;
                }

                _logger.LogInformation("🔄 开始刷新配置缓存...");

                // 使用配置加载器 (支持外部文件合并)
                var config = await _configLoader.LoadConfigAsync(_configPath);

                if (config != null)
                {
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetSlidingExpiration(TimeSpan.FromMinutes(30))
                        .SetAbsoluteExpiration(TimeSpan.FromHours(2))
                        .SetPriority(CacheItemPriority.High);

                    _cache.Set(CacheKey, config, cacheOptions);

                    _logger.LogInformation(
                        "✅ 配置已缓存: Connections={ConnectionCount}, AutoCreate={AutoCreateCount}",
                        config.Connections?.Count ?? 0,
                        config.AutoCreateDevices?.Count ?? 0);
                }
                else
                {
                    _logger.LogWarning("⚠️ 配置加载结果为 null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 刷新配置缓存失败");
                throw;
            }
        }

        public async Task<ModbusConnectionConfig?> GetConnectionConfigAsync(string connectionId)
        {
            var config = await GetConfigAsync();
            return config?.Connections?.FirstOrDefault(c => c.Id == connectionId);
        }

        public async Task<DeviceConfig?> GetDeviceConfigAsync(string connectionId, string deviceId)
        {
            var connection = await GetConnectionConfigAsync(connectionId);
            return connection?.Devices?.FirstOrDefault(d => d.Id == deviceId);
        }
    }
}