using YesVPN.Service;
using YesVPN.Service.Engine;
using YesVPN.Service.Ipc;
using YesVPN.Service.KillSwitch;
using YesVPN.Service.Logging;
using YesVPN.Service.Tunnel;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "YesVPNService";
});

builder.Services.AddSingleton(_ => new LogRingBuffer(capacity: 1500));
builder.Services.AddSingleton<IKillSwitchManager, WfpKillSwitchManager>();
builder.Services.AddSingleton<ISingBoxRunner, SingBoxProcessRunner>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton<IpcPipeServer>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
