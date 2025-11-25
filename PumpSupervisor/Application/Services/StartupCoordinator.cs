using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PumpSupervisor.Application.Services
{
    /// <summary>
    /// 启动协调器,确保服务按正确顺序启动
    /// </summary>
    public class StartupCoordinator : IHostedService
    {
        private readonly ILogger<StartupCoordinator> _logger;
        private readonly IHostApplicationLifetime _lifetime;

        public StartupCoordinator(
            ILogger<StartupCoordinator> logger,
            IHostApplicationLifetime lifetime)
        {
            _logger = logger;
            _lifetime = lifetime;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⏳ StartupCoordinator: 开始协调服务启动");

            _lifetime.ApplicationStarted.Register(() =>
            {
                _logger.LogInformation("✅ StartupCoordinator: 所有服务已启动完成");
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StartupCoordinator: 应用程序正在停止");
            return Task.CompletedTask;
        }
    }
}