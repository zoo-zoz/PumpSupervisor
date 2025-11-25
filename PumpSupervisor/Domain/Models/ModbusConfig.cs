using System.Text.Json.Serialization;

namespace PumpSupervisor.Domain.Models
{
    public class ModbusConfig
    {
        [JsonPropertyName("connections")]
        public List<ModbusConnectionConfig> Connections { get; set; } = new();

        /// <summary>
        /// 自动创建设备配置
        /// </summary>
        [JsonPropertyName("autoCreateDevices")]
        public List<AutoCreateDeviceConfig> AutoCreateDevices { get; set; } = new();
    }
}