using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Modbus.Factory.PumpSupervisor.Infrastructure.Modbus.Factory;

namespace PumpSupervisor.Infrastructure.Modbus.Factory
{
    public class ModbusConnectionFactory : IModbusConnectionFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public ModbusConnectionFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public IModbusConnection CreateConnection(ModbusConnectionConfig config)
        {
            return config.Type.ToUpper() switch
            {
                "TCP" => new ModbusTcpConnection(config, _loggerFactory.CreateLogger<ModbusTcpConnection>()),
                "RTU" => new ModbusRtuConnection(config, _loggerFactory.CreateLogger<ModbusRtuConnection>()),
                _ => throw new ArgumentException($"不支持的连接类型: {config.Type}")
            };
        }
    }
}