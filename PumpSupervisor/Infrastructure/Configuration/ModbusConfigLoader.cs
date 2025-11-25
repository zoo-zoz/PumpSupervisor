using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using System.Text.Json;

namespace PumpSupervisor.Infrastructure.Configuration
{
    /// <summary>
    /// Modbus 配置加载器 - 支持设备配置外部化
    /// </summary>
    public interface IModbusConfigLoader
    {
        Task<ModbusConfig> LoadConfigAsync(string configPath);
    }

    public class ModbusConfigLoader : IModbusConfigLoader
    {
        private readonly ILogger<ModbusConfigLoader> _logger;
        private readonly string _baseDirectory;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public ModbusConfigLoader(ILogger<ModbusConfigLoader> logger)
        {
            _logger = logger;
            _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        public async Task<ModbusConfig> LoadConfigAsync(string configPath)
        {
            try
            {
                _logger.LogInformation("📖 加载 Modbus 配置: {Path}", configPath);

                if (!File.Exists(configPath))
                {
                    throw new FileNotFoundException($"配置文件不存在: {configPath}");
                }

                var json = await File.ReadAllTextAsync(configPath);
                var config = JsonSerializer.Deserialize<ModbusConfig>(json, JsonOptions);

                if (config == null)
                {
                    throw new InvalidOperationException("配置反序列化失败");
                }

                // 处理每个连接的设备配置
                foreach (var connection in config.Connections.Where(c => c.Enabled))
                {
                    await LoadDeviceConfigsAsync(connection);
                }

                _logger.LogInformation("✅ 配置加载完成: Connections={ConnCount}, AutoCreate={AutoCount}",
                    config.Connections.Count, config.AutoCreateDevices?.Count ?? 0);

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 加载配置失败: {Path}", configPath);
                throw;
            }
        }

        /// <summary>
        /// 加载连接的设备配置 - 支持外部文件
        /// </summary>
        private async Task LoadDeviceConfigsAsync(ModbusConnectionConfig connection)
        {
            var devicesToLoad = connection.Devices.Where(d => d.Enabled).ToList();

            foreach (var device in devicesToLoad)
            {
                // 如果配置了 path,从外部文件加载
                if (!string.IsNullOrEmpty(device.Path))
                {
                    await MergeExternalConfigAsync(device);
                }
                else
                {
                    _logger.LogDebug("设备使用内联配置: {ConnectionId}/{DeviceId}",
                        connection.Id, device.Id);
                }
            }
        }

        /// <summary>
        /// 合并外部配置文件到设备配置
        /// </summary>
        private async Task MergeExternalConfigAsync(DeviceConfig device)
        {
            try
            {
                // 构建完整路径
                var externalPath = device.Path!.TrimStart('/', '\\');
                var fullPath = Path.Combine(_baseDirectory, externalPath);

                _logger.LogDebug("📄 加载外部配置: {DeviceId} <- {Path}",
                    device.Id, externalPath);

                if (!File.Exists(fullPath))
                {
                    _logger.LogError("❌ 外部配置文件不存在: {Path}", fullPath);
                    throw new FileNotFoundException($"设备配置文件不存在: {fullPath}");
                }

                var json = await File.ReadAllTextAsync(fullPath);
                var externalConfig = JsonSerializer.Deserialize<DeviceConfig>(json, JsonOptions);

                if (externalConfig == null)
                {
                    throw new InvalidOperationException($"外部配置反序列化失败: {fullPath}");
                }

                // 合并配置 (外部文件的配置会覆盖主配置中的对应字段)
                device.PollMode = externalConfig.PollMode;
                device.ReadBlocks = externalConfig.ReadBlocks;
                device.Parameters = externalConfig.Parameters;

                // 如果外部文件有 description,也更新
                if (!string.IsNullOrEmpty(externalConfig.Description))
                {
                    device.Description = externalConfig.Description;
                }

                _logger.LogDebug("✅ 外部配置已合并: {DeviceId}, Params={ParamCount}, Blocks={BlockCount}",
                    device.Id, device.Parameters.Count, device.ReadBlocks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 合并外部配置失败: {DeviceId}, Path={Path}",
                    device.Id, device.Path);
                throw;
            }
        }
    }
}