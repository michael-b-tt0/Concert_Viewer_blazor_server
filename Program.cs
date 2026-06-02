using Concert_Viewer.Components;
using Concert_Viewer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Concert_Viewer.Services;
using Microsoft.AspNetCore.DataProtection;
using AppAuthenticationService = Concert_Viewer.Services.AuthenticationService;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents();
builder.Services.AddHttpClient("SocialLinks", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ConcertViewer/1.0)");
});
builder.Services.AddDbContextFactory<ConcertsContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(builder.Environment.ContentRootPath, "concerts.db")}"));


// Configure Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Concert_viewer.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Use Always in production with HTTPS
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AppAuthenticationService>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

if (builder.Environment.IsProduction())
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo("/app/data-protection-keys"))
        .SetApplicationName(builder.Environment.ApplicationName);
}
else
{
    // Development: Use temporary keys that reset on restart
    builder.Services.AddDataProtection()
        .SetApplicationName(builder.Environment.ApplicationName);
}


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
    {
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    }
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();





app.MapStaticAssets();
app.UseRouting();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/auth/login", async (HttpContext httpContext, AppAuthenticationService authenticationService, [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl) =>
{
    var principal = authenticationService.CreatePrincipal(username, password);
    if (principal is null)
    {
        var failedReturnUrl = authenticationService.NormalizeReturnUrl(returnUrl);
        var separator = failedReturnUrl.Contains('?') ? "&" : "?";
        return Results.LocalRedirect($"{failedReturnUrl}{separator}authError=invalid");
    }

    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        authenticationService.CreateAuthenticationProperties());

    return Results.LocalRedirect(authenticationService.NormalizeReturnUrl(returnUrl));
})
.DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext httpContext, AppAuthenticationService authenticationService, [FromForm] string? returnUrl) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect(authenticationService.NormalizeReturnUrl(returnUrl));
})
.DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
    

app.Run();
