using System.Reflection;
using Arad.SMS.Core.MySqlReader.Models;
using Serilog;

namespace Arad.SMS.Core.MySqlReader;

public class Program
{
    public static readonly LogConfig _logConfig = new();
    public static IConfiguration Configuration { get; set; }

    public static void Main(string[] args)
    {
        CreateHostBuilder(args).Build().Run();
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(config => config.AddUserSecrets(Assembly.GetExecutingAssembly()))
            .ConfigureServices((hostContext, services) =>
            {
                Configuration = hostContext.Configuration;
                Configuration.Bind("LogConfig", _logConfig);

                services.AddHttpClient();

                services.AddHostedService<Worker>();
            })
            .UseWindowsService()
            .UseSerilog((_, _, configuration) => configuration.WriteTo.File(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _logConfig.LogFileAddressDirectory,
                    _logConfig.LogFileName),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: _logConfig.FileSizeLimit)
            );
    }
}