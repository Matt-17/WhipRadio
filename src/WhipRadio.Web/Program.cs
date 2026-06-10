using WhipRadio.Web.Components;
using WhipRadio.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<RadioApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Orchestrator:Endpoint"] ?? "http://orchestrator");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<RadioLiveClient>();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
