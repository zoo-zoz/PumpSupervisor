using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Modbus.Factory;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Collections.Concurrent;

namespace PumpSupervisor.Infrastructure.Modbus
{
    /// <summary>
    /// Modbus 连接管理器接口 - 管理和复用连接
    /// </summary>
    public interface IModbusConnectionManager : IAsyncDisposable
    {
        /// <summary>
        /// 注册连接配置（在启动时调用）
        /// </summary>
        void RegisterConfiguration(ModbusConnectionConfig config);

        /// <summary>
        /// 获取或创建连接
        /// </summary>
        Task<IModbusConnection> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 确保连接已建立
        /// </summary>
        Task EnsureConnectedAsync(string connectionId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 关闭指定连接
        /// </summary>
        Task CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Modbus 连接管理器实现
    /// 为每个连接配置维护一个共享的连接实例
    /// </summary>
    public class ModbusConnectionManager : IModbusConnectionManager
    {
        private readonly IModbusConnectionFactory _connectionFactory;
        private readonly ILogger<ModbusConnectionManager> _logger;

        // 连接实例缓存：key = connectionId, value = connection
        private readonly ConcurrentDictionary<string, IModbusConnection> _connections = new();

        // 连接配置缓存
        private readonly ConcurrentDictionary<string, ModbusConnectionConfig> _configurations = new();

        // 连接创建锁：确保每个连接只创建一次
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new();

        public ModbusConnectionManager(
            IModbusConnectionFactory connectionFactory,
            ILogger<ModbusConnectionManager> logger)
        {
            _connectionFactory = connectionFactory;
            _logger = logger;
        }

        /// <summary>
        /// 注册连接配置（在启动时调用）
        /// </summary>
        public void RegisterConfiguration(ModbusConnectionConfig config)
        {
            if (string.IsNullOrEmpty(config.Id))
            {
                throw new ArgumentException("Connection ID cannot be null or empty", nameof(config));
            }

            _configurations[config.Id] = config;
            _connectionLocks[config.Id] = new SemaphoreSlim(1, 1);

            _logger.LogInformation("注册连接配置: {ConnectionId} ({Type} - {Host}:{Port})",
                config.Id, config.Type,
                config.Connection.Host ?? config.Connection.SerialPort,
                config.Connection.Port);
        }

        /// <summary>
        /// 获取或创建连接（线程安全）
        /// </summary>
        public async Task<IModbusConnection> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(connectionId))
            {
                throw new ArgumentException("Connection ID cannot be null or empty", nameof(connectionId));
            }

            // 如果连接已存在且已连接，直接返回
            if (_connections.TryGetValue(connectionId, out var existingConnection)
                && existingConnection.IsConnected)
            {
                _logger.LogTrace("复用现有连接: {ConnectionId}", connectionId);
                return existingConnection;
            }

            // 获取连接锁
            if (!_connectionLocks.TryGetValue(connectionId, out var connectionLock))
            {
                throw new InvalidOperationException(
                    $"Connection configuration not found: {connectionId}. " +
                    "Please call RegisterConfiguration first.");
            }

            await connectionLock.WaitAsync(cancellationToken);
            try
            {
                // 双重检查：其他线程可能已经创建了连接
                if (_connections.TryGetValue(connectionId, out existingConnection)
                    && existingConnection.IsConnected)
                {
                    _logger.LogTrace("复用现有连接（锁内检查）: {ConnectionId}", connectionId);
                    return existingConnection;
                }

                // 获取配置
                if (!_configurations.TryGetValue(connectionId, out var config))
                {
                    throw new InvalidOperationException(
                        $"Connection configuration not found: {connectionId}");
                }

                _logger.LogInformation("创建新的 Modbus 连接: {ConnectionId}", connectionId);

                // 创建新连接
                var connection = _connectionFactory.CreateConnection(config);

                // 建立连接
                await connection.ConnectAsync(cancellationToken);

                // 保存到字典
                _connections[connectionId] = connection;

                _logger.LogInformation("Modbus 连接创建成功: {ConnectionId} (共享连接)", connectionId);
                AppTelemetry.Metrics.SetActiveConnectionsCount(_connections.Count);
                return connection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 Modbus 连接失败: {ConnectionId}", connectionId);
                throw;
            }
            finally
            {
                connectionLock.Release();
            }
        }

        /// <summary>
        /// 确保连接已建立
        /// </summary>
        public async Task EnsureConnectedAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            var connection = await GetConnectionAsync(connectionId, cancellationToken);

            if (!connection.IsConnected)
            {
                _logger.LogWarning("连接 {ConnectionId} 未连接，尝试重新连接...", connectionId);
                await connection.ConnectAsync(cancellationToken);
            }
        }

        /// <summary>
        /// 关闭指定连接
        /// </summary>
        public async Task CloseConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            if (!_connectionLocks.TryGetValue(connectionId, out var connectionLock))
            {
                return;
            }

            await connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_connections.TryRemove(connectionId, out var connection))
                {
                    _logger.LogInformation("关闭 Modbus 连接: {ConnectionId}", connectionId);
                    await connection.DisconnectAsync(cancellationToken);
                    await connection.DisposeAsync();
                }
            }
            finally
            {
                connectionLock.Release();
            }
        }

        /// <summary>
        /// 释放所有连接
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _logger.LogInformation("释放所有 Modbus 连接...");

            var tasks = _connections.Keys.Select(id => CloseConnectionAsync(id));
            await Task.WhenAll(tasks);

            foreach (var lockItem in _connectionLocks.Values)
            {
                lockItem?.Dispose();
            }

            _connectionLocks.Clear();
            _configurations.Clear();
            AppTelemetry.Metrics.SetActiveConnectionsCount(_connections.Count);
            _logger.LogInformation("所有 Modbus 连接已释放");
        }
    }
}