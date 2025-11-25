using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Cache;

namespace PumpSupervisor.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class ModbusConfigController : ControllerBase
    {
        private readonly IModbusConfigCacheService _configCache;
        private readonly ILogger<ModbusConfigController> _logger;

        public ModbusConfigController(
            IModbusConfigCacheService configCache,
            ILogger<ModbusConfigController> logger)
        {
            _configCache = configCache;
            _logger = logger;
        }

        /// <summary>
        /// 获取完整的 Modbus 配置
        /// </summary>
        /// <returns>完整的配置对象</returns>
        [HttpGet]
        public async Task<ActionResult<ModbusConfig>> GetFullConfig()
        {
            try
            {
                var config = await _configCache.GetConfigAsync();
                if (config == null)
                {
                    return NotFound(new { message = "配置未找到" });
                }
                return Ok(config);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取配置失败");
                return StatusCode(500, new { message = "获取配置失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 获取所有连接列表
        /// </summary>
        [HttpGet("connections")]
        public async Task<ActionResult> GetConnections()
        {
            try
            {
                var config = await _configCache.GetConfigAsync();
                if (config?.Connections == null)
                {
                    return NotFound(new { message = "配置未找到" });
                }

                var connections = config.Connections.Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Enabled,
                    c.Type,
                    c.RegisterType,
                    c.SlaveId,
                    c.PollInterval,
                    DeviceCount = c.Devices?.Count ?? 0
                });

                return Ok(connections);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取连接列表失败");
                return StatusCode(500, new { message = "获取连接列表失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 获取指定连接的完整配置
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        [HttpGet("connections/{connectionId}")]
        public async Task<ActionResult<ModbusConnectionConfig>> GetConnection(string connectionId)
        {
            try
            {
                var connection = await _configCache.GetConnectionConfigAsync(connectionId);
                if (connection == null)
                {
                    return NotFound(new { message = $"连接 {connectionId} 未找到" });
                }
                return Ok(connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取连接配置失败: {ConnectionId}", connectionId);
                return StatusCode(500, new { message = "获取连接配置失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 获取指定连接下的所有设备列表
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        [HttpGet("connections/{connectionId}/devices")]
        public async Task<ActionResult> GetDevices(string connectionId)
        {
            try
            {
                var connection = await _configCache.GetConnectionConfigAsync(connectionId);
                if (connection?.Devices == null)
                {
                    return NotFound(new { message = $"连接 {connectionId} 或设备列表未找到" });
                }

                var devices = connection.Devices.Select(d => new
                {
                    d.Id,
                    d.Name,
                    d.Enabled,
                    d.PollMode,
                    d.Description,
                    ParameterCount = d.Parameters?.Count ?? 0,
                    ReadBlockCount = d.ReadBlocks?.Count ?? 0
                });

                return Ok(devices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备列表失败: {ConnectionId}", connectionId);
                return StatusCode(500, new { message = "获取设备列表失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 获取指定设备的完整配置
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="deviceId">设备ID</param>
        [HttpGet("connections/{connectionId}/devices/{deviceId}")]
        public async Task<ActionResult<DeviceConfig>> GetDevice(string connectionId, string deviceId)
        {
            try
            {
                var device = await _configCache.GetDeviceConfigAsync(connectionId, deviceId);
                if (device == null)
                {
                    return NotFound(new { message = $"设备 {connectionId}/{deviceId} 未找到" });
                }
                return Ok(device);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备配置失败: {ConnectionId}/{DeviceId}", connectionId, deviceId);
                return StatusCode(500, new { message = "获取设备配置失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 获取指定设备的参数列表
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="deviceId">设备ID</param>
        [HttpGet("connections/{connectionId}/devices/{deviceId}/parameters")]
        public async Task<ActionResult<IEnumerable<ParameterConfig>>> GetDeviceParameters(
            string connectionId,
            string deviceId)
        {
            try
            {
                var device = await _configCache.GetDeviceConfigAsync(connectionId, deviceId);
                if (device?.Parameters == null)
                {
                    return NotFound(new { message = $"设备参数未找到: {connectionId}/{deviceId}" });
                }

                return Ok(device.Parameters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备参数失败: {ConnectionId}/{DeviceId}", connectionId, deviceId);
                return StatusCode(500, new { message = "获取设备参数失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 刷新配置缓存
        /// </summary>
        [HttpPost("refresh")]
        public async Task<ActionResult> RefreshConfig()
        {
            try
            {
                await _configCache.RefreshConfigAsync();
                _logger.LogInformation("配置缓存已刷新");
                return Ok(new { message = "配置已刷新", timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新配置失败");
                return StatusCode(500, new { message = "刷新配置失败", error = ex.Message });
            }
        }

        /// <summary>
        /// 健康检查
        /// </summary>
        [HttpGet("health")]
        public ActionResult Health()
        {
            return Ok(new
            {
                status = "healthy",
                timestamp = DateTime.Now,
                service = "Modbus Config API"
            });
        }
    }
}