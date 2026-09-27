using Mike.Service;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "MikeLocalService";
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
