using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PumpSupervisor.API;
using PumpSupervisor.Application.Services;
using PumpSupervisor.Infrastructure.Cache;
using PumpSupervisor.Infrastructure.Messaging.Mqtt;
using PumpSupervisor.Infrastructure.Modbus;
using PumpSupervisor.Infrastructure.Modbus.DataParser;
using PumpSupervisor.Infrastructure.Modbus.Factory;
using PumpSupervisor.Infrastructure.Storage.InfluxDb;
using PumpSupervisor.Infrastructure.Storage.ModbusSlave;
using PumpSupervisor.Infrastructure.Telemetry;
using Serilog;
using Wolverine;

namespace PumpSupervisor
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .Filter.ByExcluding(logEvent =>
                {
                    var message = logEvent.MessageTemplate.Text;
                    return message.Contains("Successfully processed message");
                })
                .Enrich.WithProperty("Application", "PumpSupervisor")
                .Enrich.WithProperty("Environment", "Production")
                .CreateLogger();

            try
            {
                Log.Information("=== PumpSupervisor 服务启动 ===");

                var host = CreateHostBuilder(args, configuration).Build();
                // 在服务启动前初始化配置缓存
                await InitializeConfigCacheAsync(host.Services);
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "服务启动失败");
                throw;
            }
            finally
            {
                Log.Information("=== PumpSupervisor 服务停止 ===");
                Log.CloseAndFlush();
            }
        }

        private static async Task InitializeConfigCacheAsync(IServiceProvider services)
        {
            try
            {
                var configCache = services.GetRequiredService<IModbusConfigCacheService>();
                await configCache.RefreshConfigAsync();
                Log.Information("✅ 配置缓存初始化成功");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ 配置缓存初始化失败");
                throw;
            }
        }

        private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration)
        {
            var serviceName = configuration["OpenTelemetry:ServiceName"] ?? "PumpSupervisor";
            var serviceVersion = configuration["OpenTelemetry:ServiceVersion"] ?? "1.0.0";

            return Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = configuration["AppSettings:ServiceName"] ?? "PumpSupervisor";
                })
                .UseSerilog()
                .ConfigureServices((hostContext, services) =>
                {
                    // ========== 第一层: 基础设施服务 ==========
                    services.AddMemoryCache();
                    services.AddSingleton<IModbusConfigCacheService, ModbusConfigCacheService>();
                    services.AddSingleton<IModbusConnectionFactory, ModbusConnectionFactory>();
                    services.AddSingleton<IModbusConnectionManager, ModbusConnectionManager>();
                    services.AddSingleton<IModbusDataParser, ModbusDataParser>();
                    services.AddSingleton<IModbusCommandQueue, PriorityModbusCommandQueue>();
                    services.AddSingleton<IDataBatchCacheService, DataBatchCacheService>();
                    services.AddSingleton<IInfluxDbService, InfluxDbService>();
                    services.AddSingleton<IMqttPublisher, MqttPublisher>();
                    services.AddSingleton<IModbusApiService, ModbusApiService>();
                    services.AddSingleton<IParameterValueTracker, ParameterValueTracker>();
                    // ========== 业务逻辑服务 ==========
                    services.AddSingleton<ParameterChangeBusinessService>();

                    // ========== 第二层: 独立服务(不依赖 Wolverine) ==========
                    services.AddHostedService<StartupCoordinator>();
                    services.AddSingleton<ModbusTcpSlaveService>();
                    services.AddHostedService(sp => sp.GetRequiredService<ModbusTcpSlaveService>());
                    services.AddHostedService<ModbusConfigApiService>();

                    // ========== 第三层: 依赖 Wolverine 的服务 ==========
                    services.AddHostedService(sp => (MqttPublisher)sp.GetRequiredService<IMqttPublisher>());
                    services.AddHostedService(sp => (InfluxDbService)sp.GetRequiredService<IInfluxDbService>());
                    services.AddHostedService<ModbusPollingService>();
                    services.AddHostedService<MetricsCollectionService>();

                    ConfigureOpenTelemetry(services, configuration, serviceName, serviceVersion);
                })
                .UseWolverine(opts =>
                {
                    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
                    opts.Policies.AutoApplyTransactions();
                    opts.LocalQueue("modbus-commands").Sequential();
                    opts.LocalQueue("modbus-events").MaximumParallelMessages(5);
                    opts.Policies.UseDurableInboxOnAllListeners();
                    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                });
        }

        private static void ConfigureOpenTelemetry(
            IServiceCollection services,
            IConfiguration configuration,
            string serviceName,
            string serviceVersion)
        {
            var openTelemetryEnabled = configuration.GetValue<bool>("OpenTelemetry:Enabled", true);

            if (!openTelemetryEnabled)
            {
                Log.Information("OpenTelemetry 已禁用");
                return;
            }

            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource
                    .AddService(
                        serviceName: serviceName,
                        serviceVersion: serviceVersion)
                    .AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.environment"] = "production",
                        ["host.name"] = Environment.MachineName
                    }))
                .WithTracing(tracing =>
                {
                    tracing
                        .AddSource(AppTelemetry.ActivitySource.Name)
                        .AddSource("Wolverine")
                        .SetSampler(new AlwaysOnSampler())
                        .AddAspNetCoreInstrumentation(options =>
                        {
                            options.RecordException = true;
                        })
                        .AddHttpClientInstrumentation(options =>
                        {
                            options.RecordException = true;
                        });

                    // 配置 OTLP 导出器
                    ConfigureOtlpTraceExporter(tracing, configuration);

                    // 配置 Jaeger 导出器(通过 OTLP)
                    ConfigureJaegerTraceExporter(tracing, configuration);

                    // 配置控制台导出器
                    if (configuration.GetValue<bool>("OpenTelemetry:Exporters:Console:Enabled", false))
                    {
                        tracing.AddConsoleExporter();
                        Log.Information("✓ 已启用控制台 Trace 导出");
                    }
                })
                .WithMetrics(metrics =>
                {
                    metrics
                        .AddMeter(AppTelemetry.Meter.Name)
                        .AddRuntimeInstrumentation()
                        .AddProcessInstrumentation()
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();

                    // 配置 OTLP 指标导出
                    ConfigureOtlpMetricsExporter(metrics, configuration);

                    // 配置控制台指标导出
                    if (configuration.GetValue<bool>("OpenTelemetry:Exporters:Console:Enabled", false))
                    {
                        metrics.AddConsoleExporter();
                        Log.Information("✓ 已启用控制台 Metrics 导出");
                    }
                });
        }

        private static void ConfigureOtlpTraceExporter(
            TracerProviderBuilder tracing,
            IConfiguration configuration)
        {
            var otlpEnabled = configuration.GetValue<bool>("OpenTelemetry:Exporters:Otlp:Enabled", false);

            if (!otlpEnabled)
            {
                return;
            }

            var endpoint = configuration["OpenTelemetry:Exporters:Otlp:Endpoint"];
            if (string.IsNullOrEmpty(endpoint))
            {
                Log.Warning("OTLP Trace 导出已启用但未配置端点");
                return;
            }

            var protocol = configuration["OpenTelemetry:Exporters:Otlp:Protocol"]?.ToLower();
            var otlpProtocol = protocol switch
            {
                "grpc" => OtlpExportProtocol.Grpc,
                "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.Grpc
            };

            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(endpoint);
                options.Protocol = otlpProtocol;
            });

            Log.Information("✓ 已启用 OTLP Trace 导出: {Endpoint} (协议: {Protocol})",
                endpoint, otlpProtocol);
        }

        private static void ConfigureJaegerTraceExporter(
            TracerProviderBuilder tracing,
            IConfiguration configuration)
        {
            var jaegerEnabled = configuration.GetValue<bool>("OpenTelemetry:Exporters:Jaeger:Enabled", false);

            if (!jaegerEnabled)
            {
                return;
            }

            var endpoint = configuration["OpenTelemetry:Exporters:Jaeger:Endpoint"];
            if (string.IsNullOrEmpty(endpoint))
            {
                Log.Warning("Jaeger Trace 导出已启用但未配置端点");
                return;
            }

            var protocol = configuration["OpenTelemetry:Exporters:Jaeger:Protocol"]?.ToLower();
            var otlpProtocol = protocol switch
            {
                "grpc" => OtlpExportProtocol.Grpc,
                "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.HttpProtobuf
            };

            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(endpoint);
                options.Protocol = otlpProtocol;
            });

            Log.Information("✓ 已启用 Jaeger Trace 导出: {Endpoint} (协议: {Protocol})",
                endpoint, otlpProtocol);
        }

        private static void ConfigureOtlpMetricsExporter(
            MeterProviderBuilder metrics,
            IConfiguration configuration)
        {
            var otlpEnabled = configuration.GetValue<bool>("OpenTelemetry:Exporters:Otlp:Enabled", false);

            if (!otlpEnabled)
            {
                return;
            }

            var endpoint = configuration["OpenTelemetry:Exporters:Otlp:Endpoint"];
            if (string.IsNullOrEmpty(endpoint))
            {
                return;
            }

            var protocol = configuration["OpenTelemetry:Exporters:Otlp:Protocol"]?.ToLower();
            var otlpProtocol = protocol switch
            {
                "grpc" => OtlpExportProtocol.Grpc,
                "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.Grpc
            };

            metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(endpoint);
                options.Protocol = otlpProtocol;
            });

            Log.Information("✓ 已启用 OTLP Metrics 导出: {Endpoint} (协议: {Protocol})",
                endpoint, otlpProtocol);
        }
    }
}