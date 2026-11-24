using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SoulsAiStudio.Mcp;

// stdout is the MCP protocol: everything else goes to stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var service = new AiProjectService();
string? projectArg = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--project") projectArg = args[i + 1];
if (projectArg != null)
{
    try { service.OpenProject(projectArg); Console.Error.WriteLine($"[souls-ai] opened {projectArg}"); }
    catch (Exception ex) { Console.Error.WriteLine($"[souls-ai] could not open project: {ex.Message}"); }
}

builder.Services.AddSingleton(service);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
// The host does not dispose a pre-built singleton: clean the temp decompile cache on shutdown.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(service.Dispose);
await host.RunAsync();
