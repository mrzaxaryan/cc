using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using cc;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<cc.Services.WindowManager>();
builder.Services.AddScoped<cc.Services.RelayStore>();
builder.Services.AddScoped<cc.Services.CacheManager>();
builder.Services.AddSingleton<cc.Services.MessageService>();

await builder.Build().RunAsync();
