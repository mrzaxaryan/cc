using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using cc;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<cc.Services.LocalStorageService>();
builder.Services.AddSingleton<cc.Services.WindowManager>();
builder.Services.AddScoped<cc.Services.RelayStore>();
builder.Services.AddScoped<cc.Services.CacheManager>();
builder.Services.AddSingleton<cc.Services.MessageService>();
builder.Services.AddScoped<cc.Services.ThemeService>();
builder.Services.AddScoped<cc.Services.AgentStore>();
builder.Services.AddScoped<cc.Services.DownloadStore>();
builder.Services.AddScoped<cc.Services.VfsStore>();
builder.Services.AddScoped<cc.Services.SearchStore>();
builder.Services.AddScoped<cc.Services.ServiceStateStore>();
builder.Services.AddScoped<cc.Services.ExtensionGroupStore>();
builder.Services.AddScoped<cc.Services.RelayConnectionService>();
builder.Services.AddScoped<cc.Services.NotificationStore>();

await builder.Build().RunAsync();
