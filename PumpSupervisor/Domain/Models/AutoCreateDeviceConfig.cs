using System.Text.Json.Serialization;

namespace PumpSupervisor.Domain.Models
{
    /// <summary>
    /// 自动创建设备配置 - 用于启动时自动创建虚拟 Slave
    /// </summary>
    public class AutoCreateDeviceConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // TCP or RTU

        [JsonPropertyName("register_type")]
        public string RegisterType { get; set; } = string.Empty;

        [JsonPropertyName("connection")]
        public ConnectionSettings Connection { get; set; } = new();

        [JsonPropertyName("byte_order")]
        public string ByteOrder { get; set; } = "ABCD";

        [JsonPropertyName("slave_id")]
        public int SlaveId { get; set; } = 1;
    }
}