using System.Text.Json.Serialization;

namespace PumpSupervisor.Domain.Models
{
    public class ModbusConfig
    {
        [JsonPropertyName("connections")]
        public List<ModbusConnectionConfig> Connections { get; set; } = new();
    }
}