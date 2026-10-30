using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using HsaReceipts.Client;
using HsaReceipts.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddTransient<BrowserCredentialsHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<BrowserCredentialsHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler)
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
});
builder.Services.AddScoped<ReceiptApiClient>();
builder.Services.AddScoped<OfflineStore>();
builder.Services.AddScoped<UserContext>();

await builder.Build().RunAsync();
