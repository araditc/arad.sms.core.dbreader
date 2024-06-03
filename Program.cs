//
//  --------------------------------------------------------------------
//  Copyright (c) 2005-2024 Arad ITC.
//
//  Author : Ammar Heidari <ammar@arad-itc.org>
//  Licensed under the Apache License, Version 2.0 (the "License")
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0 
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  --------------------------------------------------------------------

using System.Reflection;

using Arad.SMS.Core.DbReader.Models;

using Serilog;

namespace Arad.SMS.Core.DbReader;

public class Program
{
    public static readonly LogConfig LogConfig = new();

    public static IConfiguration? Configuration { get; set; }

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
                                          Configuration.Bind("LogConfig", LogConfig);

                                          services.AddHttpClient();

                                          services.AddHostedService<Worker>();
                                      })
                   .UseWindowsService()
                   .UseSerilog((_, _, configuration) => configuration.WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogConfig.LogFileAddressDirectory, LogConfig.LogFileName),
                                                                                   rollingInterval: RollingInterval.Day,
                                                                                   rollOnFileSizeLimit: true,
                                                                                   fileSizeLimitBytes: LogConfig.FileSizeLimit)
                   );
    }
}