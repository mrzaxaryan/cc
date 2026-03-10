using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using cc;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
// Infrastructure
builder.Services.AddScoped<cc.Infrastructure.LocalStorageService>();
builder.Services.AddSingleton<cc.Infrastructure.MessageService>();
builder.Services.AddScoped<cc.Infrastructure.ServiceStateStore>();
builder.Services.AddScoped<cc.Infrastructure.ThemeService>();

// Features
builder.Services.AddScoped<cc.Features.Agents.AgentStore>();
builder.Services.AddScoped<cc.Features.Downloads.DownloadStore>();
builder.Services.AddScoped<cc.Features.Extensions.ExtensionGroupStore>();
builder.Services.AddScoped<cc.Features.FileManager.VfsStore>();
builder.Services.AddScoped<cc.Features.Notifications.NotificationStore>();
builder.Services.AddScoped<cc.Features.Relay.RelayConnectionService>();
builder.Services.AddScoped<cc.Features.Relay.RelayStore>();
builder.Services.AddScoped<cc.Features.Search.SearchStore>();
builder.Services.AddScoped<cc.Features.Storage.CacheManager>();
builder.Services.AddSingleton<cc.Features.Workspace.WindowManager>();

await builder.Build().RunAsync();
