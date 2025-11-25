using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using PumpSupervisor.Domain.Configuration;
using PumpSupervisor.Infrastructure.Cache;

namespace PumpSupervisor.API
{
    public class ModbusConfigApiService : IHostedService
    {
        private IWebHost? _webHost;
        private readonly ILogger<ModbusConfigApiService> _logger;
        private readonly ApiSettings _apiSettings;
        private readonly IConfiguration _configuration;

        public ModbusConfigApiService(
            ILogger<ModbusConfigApiService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;

            // 从配置读取 API 设置
            _apiSettings = new ApiSettings();
            configuration.GetSection("ApiSettings").Bind(_apiSettings);

            _logger.LogDebug("API 配置加载: Port={Port}, EnableSwagger={EnableSwagger}, EnableCors={EnableCors}",
                _apiSettings.Port, _apiSettings.EnableSwagger, _apiSettings.EnableCors);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _webHost = new WebHostBuilder()
                    .UseKestrel(options =>
                    {
                        options.ListenAnyIP(_apiSettings.Port);
                    })
                    .ConfigureServices(services =>
                    {
                        // 注册配置
                        services.AddSingleton(_apiSettings);

                        services.AddMemoryCache();
                        services.AddSingleton<IModbusConfigCacheService, ModbusConfigCacheService>();
                        services.AddControllers()
                            .AddApplicationPart(typeof(ModbusConfigApiService).Assembly);

                        // CORS 配置
                        if (_apiSettings.EnableCors)
                        {
                            services.AddCors(options =>
                            {
                                options.AddPolicy("AllowAll", builder =>
                                {
                                    builder.AllowAnyOrigin()
                                           .AllowAnyMethod()
                                           .AllowAnyHeader();
                                });
                            });
                        }

                        // Swagger 配置
                        if (_apiSettings.EnableSwagger)
                        {
                            services.AddSwaggerGen(c =>
                            {
                                c.SwaggerDoc(_apiSettings.SwaggerVersion, new OpenApiInfo
                                {
                                    Title = _apiSettings.SwaggerTitle,
                                    Version = _apiSettings.SwaggerVersion,
                                    Description = _apiSettings.SwaggerDescription ?? "PumpSupervisor Modbus 设备配置管理 API",
                                    Contact = new OpenApiContact
                                    {
                                        Name = _apiSettings.ContactName ?? "PumpSupervisor",
                                        Email = _apiSettings.ContactEmail
                                    }
                                });

                                // 添加 XML 注释支持
                                var xmlFile = $"{typeof(ModbusConfigApiService).Assembly.GetName().Name}.xml";
                                var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
                                if (File.Exists(xmlPath))
                                {
                                    c.IncludeXmlComments(xmlPath);
                                }
                            });
                        }
                    })
                    .Configure(app =>
                    {
                        // CORS
                        if (_apiSettings.EnableCors)
                        {
                            app.UseCors("AllowAll");
                        }

                        // Swagger
                        if (_apiSettings.EnableSwagger)
                        {
                            app.UseSwagger();
                            app.UseSwaggerUI(c =>
                            {
                                c.SwaggerEndpoint($"/swagger/{_apiSettings.SwaggerVersion}/swagger.json",
                                    $"{_apiSettings.SwaggerTitle} {_apiSettings.SwaggerVersion}");
                                c.RoutePrefix = "swagger";
                                c.DocumentTitle = $"{_apiSettings.SwaggerTitle} - 文档";
                                c.DisplayRequestDuration();
                            });
                        }

                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                        });
                    })
                    .Build();

                _webHost.Start();

                _logger.LogInformation("✅ Modbus 配置 API 服务已启动");
                _logger.LogInformation("   端口: {Port}", _apiSettings.Port);
                _logger.LogInformation("   API 地址: http://localhost:{Port}/api/modbusconfig", _apiSettings.Port);

                if (_apiSettings.EnableSwagger)
                {
                    _logger.LogInformation("   Swagger UI: http://localhost:{Port}/swagger", _apiSettings.Port);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 启动 API 服务失败");
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_webHost != null)
            {
                await _webHost.StopAsync(cancellationToken);
                _webHost.Dispose();
                _logger.LogInformation("Modbus 配置 API 服务已停止");
            }
        }
    }
}