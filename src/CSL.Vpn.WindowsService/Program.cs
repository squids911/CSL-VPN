using CSL.Vpn.WindowsService;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CSL VPN Agent";
});

var baseUrl = builder.Configuration["ControlPlane:BaseUrl"] ?? "https://v.csl.by";
builder.Services.AddHttpClient<ControlPlaneClient>(client =>
{
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<ClientIdentityStore>();
builder.Services.AddSingleton<ClientState>();
builder.Services.AddSingleton<CSL.Vpn.WindowsService.Tunnel.SoftEtherTunnelManager>();
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
