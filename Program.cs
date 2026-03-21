using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using C2;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
// Infrastructure
builder.Services.AddSingleton<C2.Infrastructure.IEventBus, C2.Infrastructure.EventBus>();
builder.Services.AddScoped<C2.Infrastructure.LocalStorageService>();
builder.Services.AddSingleton<C2.Infrastructure.MessageService>();
builder.Services.AddScoped<C2.Infrastructure.ServiceStateStore>();
builder.Services.AddScoped<C2.Infrastructure.ThemeService>();

// Features
builder.Services.AddScoped<C2.Features.Agents.AgentStore>();
builder.Services.AddScoped<C2.Features.Transfers.TransferService>();
builder.Services.AddScoped<C2.Features.Transfers.TransferStore>();
builder.Services.AddScoped<C2.Features.Extensions.ExtensionGroupStore>();
builder.Services.AddScoped<C2.Features.FileSystem.VfsStore>();
builder.Services.AddScoped<C2.Features.Notifications.NotificationStore>();
builder.Services.AddScoped<C2.Features.Relay.RelayConnectionService>();
builder.Services.AddScoped<C2.Features.Relay.RelayStore>();
builder.Services.AddScoped<C2.Features.Scan.ScanService>();
builder.Services.AddScoped<C2.Features.Scan.ScanStore>();
builder.Services.AddScoped<C2.Features.Loaders.LoaderStore>();
builder.Services.AddScoped<C2.Features.Storage.CacheManager>();
builder.Services.AddSingleton<C2.Features.Workspace.WindowManager>();

await builder.Build().RunAsync();
