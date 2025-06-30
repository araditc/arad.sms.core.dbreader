using System.Net;

using Arad.SMS.Core.DbReader;
using Arad.SMS.Core.DbReader.Authentication;
using Arad.SMS.Core.DbReader.Models;
using Arad.SMS.Core.DbReader.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

RuntimeSettings.Configuration = builder.Configuration;

builder.Services.AddHostedService<TimedWorker>();
builder.Services.AddHostedService<Worker>();

builder.WebHost.UseUrls($"http//{builder.Configuration["URLSetting:IP"] ?? "127.0.0.1"}:{builder.Configuration["URLSetting:Port"] ?? "8888"}");

builder.WebHost.ConfigureKestrel(options =>
                                 {
                                     options.Listen(IPAddress.Parse(builder.Configuration["URLSetting:IP"] ?? "127.0.0.1"),
                                                    Convert.ToInt32(builder.Configuration["URLSetting:Port"] ?? "8888"),
                                                    listenOptions =>
                                                    {
                                                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                                                    });
                                 });

builder.Services.AddWindowsService();
builder.Services.AddSystemd();
builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

LogConfig logConfig = new();
builder.Configuration.Bind("LogConfig", logConfig);

builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Host.UseSerilog((_, loggerConfiguration) => loggerConfiguration.MinimumLevel.Information()
                                                                       .WriteTo.File(logConfig.LogFileAddressDirectory,
                                                                                     rollingInterval: RollingInterval.Day,
                                                                                     rollOnFileSizeLimit: true,
                                                                                     fileSizeLimitBytes: logConfig.FileSizeLimit,
                                                                                     retainedFileCountLimit: logConfig.RetainedFileCountLimit,
                                                                                     buffered: false));

WebApplication app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();
app.UseAuthentication();
app.UseAuthorization();

await app.RunAsync();
