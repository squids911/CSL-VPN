using CSLVpn.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CSL VPN Client";
});
builder.Services.AddHostedService<VpnWorker>();

await builder.Build().RunAsync();
