using Microsoft.AspNetCore.Components.Authorization;
using SocialHub.Web.Components;
using SocialHub.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Auth: the API issues JWTs; this app keeps them per circuit and exposes the user to Blazor.
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());

var apiBase = builder.Configuration["Api:BaseUrl"]
              ?? throw new InvalidOperationException("Missing Api:BaseUrl in configuration.");
builder.Services.AddHttpClient<ApiClient>(c =>
{
    c.BaseAddress = new Uri(apiBase.EndsWith('/') ? apiBase : apiBase + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<RealtimeService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
