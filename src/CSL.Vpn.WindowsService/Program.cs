using CSL.Vpn.WindowsService;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CSL VPN Agent";
});
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
