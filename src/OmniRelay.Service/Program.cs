using OmniRelay.Service.Ipc;
using OmniRelay.Service.Runtime;
using OmniRelay.Service.Workers;

ServicePaths.EnsureDirectories();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "OmniRelay.Service");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileLogWriter>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<GatewayRuntime>();
builder.Services.AddSingleton<LicenseValidator>();
builder.Services.AddSingleton<TunnelConnectionTester>();
builder.Services.AddSingleton<IpcCommandHandler>();
builder.Services.AddSingleton<HttpConnectProxyEngine>();
builder.Services.AddSingleton<Socks5BootstrapProxyEngine>();
builder.Services.AddHostedService<ProxyCoordinatorWorker>();
builder.Services.AddHostedService<IpcServerWorker>();
builder.Services.AddHostedService<TunnelSupervisorWorker>();

var host = builder.Build();
await host.RunAsync();
