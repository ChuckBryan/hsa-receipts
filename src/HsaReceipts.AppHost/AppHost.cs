using Aspire.Hosting.DevTunnels;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var phoneTesting = builder.Configuration.GetValue("PhoneTesting:Enabled", false);
var phonePublicOrigin = phoneTesting
    ? builder.Configuration["PhoneTesting:PublicOrigin"]
    : null;

if (phoneTesting && !builder.ExecutionContext.IsRunMode)
{
    throw new InvalidOperationException("Phone testing is available only in Aspire run mode.");
}

builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(enabled: false)
    .ConfigureComposeFile(compose => compose.Name = "hsa-receipts");

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17.6-alpine")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Name = "postgres";
        service.Restart = "unless-stopped";
        service.AddVolume(new()
        {
            Name = "postgres-data",
            Source = "../data/postgres",
            Target = "/var/lib/postgresql/data",
            Type = "bind",
            ReadOnly = false
        });
    });

var keycloakDatabase = postgres.AddDatabase(
    "keycloak-db",
    phoneTesting ? "phone_keycloak" : "keycloak");
var keycloakAdminPassword = builder.AddParameter(
    phoneTesting ? "phone-keycloak-admin-password" : "keycloak-admin-password",
    secret: true);
var authHost = builder.ExecutionContext.IsPublishMode
    ? builder.AddParameter("auth-host")
    : null;

var keycloak = builder.AddKeycloak(
        "keycloak",
        port: phoneTesting ? 5090 : 5089,
        adminPassword: keycloakAdminPassword)
    .WithPostgres(keycloakDatabase);

if (builder.ExecutionContext.IsRunMode)
{
    var realmImportPath = phoneTesting
        ? Path.Combine(builder.AppHostDirectory, "../../data/phone-testing/realm.json")
        : "../../deploy/realm.development.json";

    if (phoneTesting && !File.Exists(realmImportPath))
    {
        throw new InvalidOperationException(
            "Phone testing is not configured. Run .\\phone-testing.ps1 setup first.");
    }

    keycloak.WithRealmImport(realmImportPath);

    if (phoneTesting)
    {
        if (!Uri.TryCreate(phonePublicOrigin, UriKind.Absolute, out var phoneOriginUri)
            || phoneOriginUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "PhoneTesting:PublicOrigin must be an absolute HTTPS origin.");
        }

        keycloak
            .WithEnvironment(
                "KC_HOSTNAME",
                phoneOriginUri.GetLeftPart(UriPartial.Authority))
            .WithEnvironment("KC_HOSTNAME_STRICT", "true")
            .WithEnvironment("KC_FEATURES", "opentelemetry,hostname:v2")
            .WithEnvironment("KC_HTTP_ENABLED", "true")
            .WithEnvironment("KC_PROXY_HEADERS", "xforwarded");
    }
}
else
{
    keycloak
        .WithEnvironment("KC_HOSTNAME", $"https://{authHost!}")
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
        .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "bootstrap-admin");
}

keycloak.PublishAsDockerComposeService((_, service) =>
{
    service.Name = "keycloak";
    service.Restart = "unless-stopped";
    service.Ports.Clear();
    service.Ports.Add("127.0.0.1:5089:8080");
    service.AddVolume(new()
    {
        Name = "keycloak-realm",
        Source = "../deploy/realm.json",
        Target = "/opt/keycloak/data/import/realm.json",
        Type = "bind",
        ReadOnly = true
    });
});

if (phoneTesting)
{
    var publishDirectory = Path.GetFullPath(
        Path.Combine(builder.AppHostDirectory, "../../.artifacts/phone-testing/app"));
    var appAssembly = Path.Combine(publishDirectory, "HsaReceipts.dll");
    if (!File.Exists(appAssembly))
    {
        throw new InvalidOperationException(
            "The phone-testing app is not published. Run .\\phone-testing.ps1 start.");
    }

    var tunnelId = builder.Configuration["PhoneTesting:TunnelId"];
    if (string.IsNullOrWhiteSpace(tunnelId)
        || !Uri.TryCreate(phonePublicOrigin, UriKind.Absolute, out var publicOriginUri)
        || publicOriginUri.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException(
            "Phone testing tunnel configuration is incomplete. Run .\\phone-testing.ps1 setup.");
    }

    var appClientSecret = builder.AddParameter("phone-app-client-secret", secret: true);
    var app = builder.AddExecutable(
            "app",
            "dotnet",
            publishDirectory,
            appAssembly)
        .WithHttpEndpoint(
            port: 7088,
            targetPort: 7088,
            name: "http",
            isProxied: false)
        .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:7088")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
        .WithEnvironment("PhoneTesting__Enabled", "true")
        .WithEnvironment(
            "PhoneTesting__PublicOrigin",
            publicOriginUri.GetLeftPart(UriPartial.Authority))
        .WithEnvironment("PhoneTesting__KeycloakDestination", keycloak.GetEndpoint("http"))
        .WithEnvironment(
            "DataPath",
            Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../data/phone-testing/app")))
        .WithEnvironment("Auth__ClientSecret", appClientSecret)
        .WithEnvironment("Auth__AllowedUsers", "phone-tester")
        .WithEnvironment("Auth__RequireHttpsMetadata", "true")
        .WithEnvironment("TrustedProxies", "127.0.0.1,::1")
        .WaitFor(keycloak);

    var tunnel = builder.AddDevTunnel(
            "phone-tunnel",
            tunnelId,
            new DevTunnelOptions
            {
                AllowAnonymous = true,
                Description = "Receipt Keep phone testing",
                ExpirationHours = 720,
                Labels = ["hsa-receipts", "phone-testing"]
            })
        .WithReference(app.GetEndpoint("http"), allowAnonymous: true);
}
else
{
    var app = builder.AddProject(
        "app",
        "../HsaReceipts/HsaReceipts.csproj",
        launchProfileName: builder.ExecutionContext.IsRunMode ? "https" : null);

    if (builder.ExecutionContext.IsPublishMode)
    {
        var appHost = builder.AddParameter("app-host");
        var appClientSecret = builder.AddParameter("app-client-secret", secret: true);
        var allowedUsers = builder.AddParameter("allowed-users");
        var trustedProxies = builder.AddParameter("trusted-proxies");

        app
            .WithHttpEndpoint(port: 5088, targetPort: 8080, name: "http")
            .WithExternalHttpEndpoints()
            .WithEnvironment("DataPath", "/data")
            .WithEnvironment("Auth__Authority", $"https://{authHost!}/realms/household")
            .WithEnvironment("Auth__ClientSecret", appClientSecret)
            .WithEnvironment("Auth__AllowedUsers", allowedUsers)
            .WithEnvironment("AllowedHosts", appHost)
            .WithEnvironment("TrustedProxies", trustedProxies);
    }

    app
        .WaitFor(keycloak)
        .PublishAsDockerComposeService((_, service) =>
        {
            service.Name = "app";
            service.Restart = "unless-stopped";
            service.Ports.Clear();
            service.Ports.Add("127.0.0.1:5088:8080");
            service.AddVolume(new()
            {
                Name = "app-data",
                Source = "../data/app",
                Target = "/data",
                Type = "bind",
                ReadOnly = false
            });
        });
}

builder.Build().Run();
