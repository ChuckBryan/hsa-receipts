using System.Net;
using HsaReceipts;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(args);
var phoneTesting = builder.Configuration.GetValue("PhoneTesting:Enabled", false);
if (phoneTesting && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Phone testing can only run in Development.");
}

var phonePublicOrigin = phoneTesting
    ? GetPhonePublicOrigin(builder.Configuration)
    : null;
if (phonePublicOrigin is not null)
{
    builder.Configuration["AllowedHosts"] = phonePublicOrigin.Host;
    builder.Services.AddHttpForwarder();
}

var data = Path.GetFullPath(builder.Configuration["DataPath"] ?? "./data");
Directory.CreateDirectory(data);

builder.Services.AddSingleton(new ReceiptStore(data));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
    .SetApplicationName("HsaReceipts");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in (builder.Configuration["TrustedProxies"] ?? "")
                 .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        options.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

var allowedUsers = (builder.Configuration["Auth:AllowedUsers"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (allowedUsers.Length is < 1 or > 2)
{
    throw new InvalidOperationException("Configure one or two Auth:AllowedUsers usernames.");
}

var authAuthority = phonePublicOrigin is null
    ? builder.Configuration["Auth:Authority"]
        ?? throw new InvalidOperationException("Auth:Authority required.")
    : new Uri(phonePublicOrigin, "/realms/household").AbsoluteUri.TrimEnd('/');
var authAuthorityUri = new Uri(authAuthority, UriKind.Absolute);
if (authAuthorityUri.Scheme is not ("https" or "http"))
{
    throw new InvalidOperationException("Auth:Authority must use HTTP or HTTPS.");
}
var authOrigin = authAuthorityUri.GetLeftPart(UriPartial.Authority);

var requireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
if (!requireHttpsMetadata && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "Auth:RequireHttpsMetadata can only be disabled in Development.");
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "__Host-HsaReceipts";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/bff"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/bff"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
})
.AddOpenIdConnect(options =>
{
    options.Authority = authAuthority;
    options.ClientId = "hsa-receipts";
    options.ClientSecret = builder.Configuration["Auth:ClientSecret"]
        ?? throw new InvalidOperationException("Auth:ClientSecret required.");
    options.RequireHttpsMetadata = requireHttpsMetadata;
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    options.SaveTokens = true;
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    options.Events.OnTokenValidated = context =>
    {
        var username = context.Principal?.FindFirst("preferred_username")?.Value;
        if (!allowedUsers.Contains(username, StringComparer.Ordinal))
        {
            context.Fail("This account does not have access.");
        }
        return Task.CompletedTask;
    };
    options.Events.OnRemoteFailure = context =>
    {
        context.HandleResponse();
        context.Response.Redirect("/?access=denied");
        return Task.CompletedTask;
    };
    if (phoneTesting)
    {
        options.BackchannelHttpHandler =
            new PhoneIdentityProxy.DevTunnelHeaderHandler(new SocketsHttpHandler());
    }
});

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(
            CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            allowedUsers.Contains(context.User.Identity?.Name, StringComparer.Ordinal))
        .Build();
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "__Host-HsaReceipts-Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = 16 * 1024 * 1024);

var app = builder.Build();
if (phonePublicOrigin is not null)
{
    app.Use((context, next) =>
    {
        context.Request.Headers["X-Forwarded-Host"] = phonePublicOrigin.Authority;
        context.Request.Headers["X-Forwarded-Proto"] = Uri.UriSchemeHttps;
        return next(context);
    });
}
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.Use(async (context, next) =>
{
    var identityRequest = phoneTesting
        && PhoneIdentityProxy.IsAllowedPath(context.Request.Path);
    if (identityRequest)
    {
        context.Response.Headers.CacheControl = "no-store";
        await next();
        return;
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(self)";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self'; img-src 'self' data: blob:; " +
        "connect-src 'self'; worker-src 'self'; " +
        $"object-src 'none'; form-action 'self' {authOrigin}; " +
        "frame-ancestors 'none'; base-uri 'self'";

    if (context.Request.Path.StartsWithSegments("/api")
        || context.Request.Path.StartsWithSegments("/bff")
        || context.Request.Path.StartsWithSegments("/signin-oidc")
        || context.Request.Path.StartsWithSegments("/signout-callback-oidc"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var name = context.File.Name;
        if (name is "index.html" or "service-worker.js" or "service-worker-assets.js"
            or "manifest.webmanifest")
        {
            context.Context.Response.Headers.CacheControl = "no-cache";
        }
        else
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (phoneTesting)
{
    var keycloakDestination = builder.Configuration["PhoneTesting:KeycloakDestination"];
    if (!Uri.TryCreate(keycloakDestination, UriKind.Absolute, out var keycloakUri)
        || keycloakUri.Scheme is not ("http" or "https")
        || !keycloakUri.IsLoopback)
    {
        throw new InvalidOperationException(
            "PhoneTesting:KeycloakDestination must be an absolute local HTTP or HTTPS URL.");
    }

    var identityProxyClient = PhoneIdentityProxy.CreateHttpClient();
    app.Lifetime.ApplicationStopped.Register(identityProxyClient.Dispose);
    PhoneIdentityProxy.Map(
        app,
        keycloakUri.AbsoluteUri,
        phonePublicOrigin!,
        identityProxyClient);
}

app.MapMethods(
    "/error",
    ["GET", "POST"],
    () => Results.Problem(
        "Something went wrong. Please return to the receipt list and try again.",
        statusCode: 500))
    .AllowAnonymous();
app.MapReceiptEndpoints();
app.MapFallbackToFile("index.html").AllowAnonymous();

static Uri GetPhonePublicOrigin(IConfiguration configuration)
{
    var configured = configuration["PhoneTesting:PublicOrigin"];
    var discovered = configuration
        .GetSection("services:app:http")
        .GetChildren()
        .Select(child => child.Value)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    var value = configured ?? discovered;

    if (!Uri.TryCreate(value, UriKind.Absolute, out var origin)
        || origin.Scheme != Uri.UriSchemeHttps
        || !string.IsNullOrEmpty(origin.PathAndQuery.Trim('/')))
    {
        throw new InvalidOperationException(
            "Phone testing requires the HTTPS public origin supplied by the Aspire dev tunnel.");
    }

    return new Uri(origin.GetLeftPart(UriPartial.Authority));
}

app.Run();

public partial class Program;
