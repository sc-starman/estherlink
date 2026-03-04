using EstherLink.Service.Ipc;
using EstherLink.Service.Runtime;
using EstherLink.Service.Workers;

ServicePaths.EnsureDirectories();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "EstherLink.Service");

builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileLogWriter>();
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<GatewayRuntime>();
builder.Services.AddSingleton<LicenseValidator>();
builder.Services.AddSingleton<IpcCommandHandler>();
builder.Services.AddSingleton<HttpConnectProxyEngine>();
builder.Services.AddHostedService<ProxyCoordinatorWorker>();
builder.Services.AddHostedService<IpcServerWorker>();

var host = builder.Build();
await host.RunAsync();
