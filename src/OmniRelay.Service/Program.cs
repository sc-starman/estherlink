using OmniRelay.Service.Ipc;
using OmniRelay.Service.Runtime;
using OmniRelay.Service.Workers;

ServicePaths.EnsureDirectories();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OmniRelay.Service");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileLogWriter>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<GatewayRuntime>();
builder.Services.AddSingleton<LicenseValidator>();
builder.Services.AddSingleton<TunnelConnectionTester>();
builder.Services.AddSingleton<IpcCommandHandler>();
builder.Services.AddSingleton<Socks5ProxyEngine>();
builder.Services.AddSingleton<Socks5BootstrapProxyEngine>();
builder.Services.AddHostedService<IpcServerWorker>();
builder.Services.AddHostedService<LogRetentionWorker>();
builder.Services.AddHostedService<RelayRuntimeCleanupWorker>();
builder.Services.AddHostedService<RelayRuntimeWorker>();

var host = builder.Build();
await host.RunAsync();
