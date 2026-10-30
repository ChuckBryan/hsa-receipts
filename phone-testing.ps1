[CmdletBinding()]
param(
    [ValidateSet('setup', 'start', 'stop', 'status', 'credentials')]
    [string]$Action = 'start'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$appHost = Join-Path $root 'src\HsaReceipts.AppHost\HsaReceipts.AppHost.csproj'
$serverProject = Join-Path $root 'src\HsaReceipts\HsaReceipts.csproj'
$publishDirectory = Join-Path $root '.artifacts\phone-testing\app'
$realmDirectory = Join-Path $root 'data\phone-testing'
$realmPath = Join-Path $realmDirectory 'realm.json'
$port = 7088

function Get-DevTunnelCommand {
    $command = Get-Command devtunnel -ErrorAction SilentlyContinue
    if ($command) {
        $env:PATH = "$(Split-Path $command.Source);$env:PATH"
        return $command.Source
    }

    $packageCommand = Get-ChildItem `
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" `
        -Filter devtunnel.exe `
        -Recurse `
        -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($packageCommand) {
        $env:PATH = "$(Split-Path $packageCommand);$env:PATH"
        return $packageCommand
    }

    throw 'Install Dev Tunnels first: winget install Microsoft.devtunnel'
}

function Get-Secrets {
    $output = & dotnet user-secrets list --json --project $appHost
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to read AppHost user secrets.'
    }

    $json = ($output | Where-Object { $_ -notmatch '^//(BEGIN|END)$' }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($json)) {
        return @{}
    }
    return $json | ConvertFrom-Json -AsHashtable
}

function Set-Secret([string]$Name, [string]$Value) {
    & dotnet user-secrets set $Name $Value --project $appHost | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to save user secret '$Name'."
    }
}

function New-RandomSecret([int]$Bytes = 32) {
    $buffer = [byte[]]::new($Bytes)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return [Convert]::ToBase64String($buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Get-OrCreateSecret(
    [hashtable]$Secrets,
    [string]$Name,
    [scriptblock]$Factory
) {
    if ($Secrets.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace($Secrets[$Name])) {
        return [string]$Secrets[$Name]
    }

    $value = & $Factory
    Set-Secret $Name $value
    $Secrets[$Name] = $value
    return $value
}

function Get-PublicOrigin($PortDetails) {
    $portProperty = $PortDetails.PSObject.Properties['port']
    if ($portProperty) {
        $PortDetails = $portProperty.Value
    }

    $candidate = $null
    foreach ($name in @('webForwardingUri', 'webForwardingUris', 'clientUri', 'uri')) {
        $property = $PortDetails.PSObject.Properties[$name]
        if ($property -and $property.Value) {
            $candidate = $property.Value
            break
        }
    }

    if ($candidate -is [array]) {
        $candidate = $candidate | Select-Object -First 1
    }
    if (-not $candidate) {
        $tunnelProperty = $PortDetails.PSObject.Properties['tunnelId']
        $portProperty = $PortDetails.PSObject.Properties['portNumber']
        $fullTunnelId = [string]$tunnelProperty.Value
        $separator = $fullTunnelId.LastIndexOf('.')
        if ($separator -gt 0 -and $portProperty.Value) {
            $baseId = $fullTunnelId.Substring(0, $separator)
            $region = $fullTunnelId.Substring($separator + 1)
            $candidate = "https://$baseId-$($portProperty.Value).$region.devtunnels.ms"
        }
    }
    $uri = $null
    if (-not [Uri]::TryCreate([string]$candidate, [UriKind]::Absolute, [ref]$uri)) {
        throw 'Dev Tunnels did not return a public forwarding URL.'
    }
    return $uri.GetLeftPart([UriPartial]::Authority)
}

function Write-Realm(
    [string]$PublicOrigin,
    [string]$ClientSecret,
    [string]$TestPassword
) {
    New-Item -ItemType Directory -Force $realmDirectory | Out-Null
    $realm = [ordered]@{
        realm = 'household'
        enabled = $true
        sslRequired = 'external'
        registrationAllowed = $false
        resetPasswordAllowed = $false
        bruteForceProtected = $true
        clients = @(
            [ordered]@{
                clientId = 'hsa-receipts'
                name = 'Receipt Keep (Phone Testing)'
                enabled = $true
                protocol = 'openid-connect'
                publicClient = $false
                secret = $ClientSecret
                standardFlowEnabled = $true
                implicitFlowEnabled = $false
                directAccessGrantsEnabled = $false
                redirectUris = @("$PublicOrigin/signin-oidc")
                webOrigins = @($PublicOrigin)
                attributes = [ordered]@{
                    'pkce.code.challenge.method' = 'S256'
                    'post.logout.redirect.uris' = "$PublicOrigin/signout-callback-oidc"
                }
                defaultClientScopes = @('profile', 'email')
            }
        )
        users = @(
            [ordered]@{
                username = 'phone-tester'
                enabled = $true
                email = 'phone-tester@localhost'
                emailVerified = $true
                firstName = 'Phone'
                lastName = 'Tester'
                credentials = @(
                    [ordered]@{
                        type = 'password'
                        value = $TestPassword
                        temporary = $false
                    }
                )
            }
        )
    }

    $realm | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 $realmPath
}

function Invoke-Setup {
    $devTunnel = Get-DevTunnelCommand
    $user = (& $devTunnel user show --json | ConvertFrom-Json)
    if ($user.status -eq 'Not logged in') {
        Write-Host 'Dev Tunnels authentication is required. Complete the browser sign-in.'
        & $devTunnel user login --github --use-browser-auth
        if ($LASTEXITCODE -ne 0) {
            throw 'Dev Tunnels login did not complete.'
        }
    }

    $secrets = Get-Secrets
    $tunnelId = Get-OrCreateSecret $secrets 'PhoneTesting:TunnelId' {
        "hsa-receipts-$((New-RandomSecret 6).ToLowerInvariant())"
    }
    $clientSecret = Get-OrCreateSecret $secrets 'Parameters:phone-app-client-secret' {
        New-RandomSecret
    }
    $adminPassword = Get-OrCreateSecret $secrets 'Parameters:phone-keycloak-admin-password' {
        "Adm!$(New-RandomSecret 24)"
    }
    $testPassword = Get-OrCreateSecret $secrets 'PhoneTesting:TestPassword' {
        "Pwa!$(New-RandomSecret 24)"
    }

    $existingTunnel = $null
    try {
        $existingTunnel = & $devTunnel show $tunnelId --json 2>$null | ConvertFrom-Json
    }
    catch {
        $existingTunnel = $null
    }
    if (-not $existingTunnel) {
        & $devTunnel create $tunnelId `
            --allow-anonymous `
            --expiration 30d `
            --description 'Receipt Keep phone testing' `
            --labels hsa-receipts `
            --labels phone-testing `
            --host-header unchanged `
            --origin-header unchanged | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to create the persistent dev tunnel.'
        }
    }

    $portDetails = $null
    try {
        $portDetails = & $devTunnel port show $tunnelId -p $port --json 2>$null |
            ConvertFrom-Json
    }
    catch {
        $portDetails = $null
    }
    if (-not $portDetails) {
        & $devTunnel port create $tunnelId `
            --port-number $port `
            --protocol http `
            --description 'Receipt Keep published PWA' `
            --host-header unchanged `
            --origin-header unchanged | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to configure the dev tunnel port.'
        }
        $portDetails = & $devTunnel port show $tunnelId -p $port --json |
            ConvertFrom-Json
    }

    $publicOrigin = Get-PublicOrigin $portDetails
    Set-Secret 'PhoneTesting:PublicOrigin' $publicOrigin
    Write-Realm $publicOrigin $clientSecret $testPassword

    Write-Host "Phone testing configured at $publicOrigin"
    Write-Host 'Run .\phone-testing.ps1 start to publish and host the PWA.'
}

function Invoke-Start {
    Get-DevTunnelCommand | Out-Null
    $secrets = Get-Secrets
    if (-not $secrets.ContainsKey('PhoneTesting:PublicOrigin') -or -not (Test-Path $realmPath)) {
        Invoke-Setup
    }

    New-Item -ItemType Directory -Force $publishDirectory | Out-Null
    & dotnet publish $serverProject -c Release -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'The phone-testing application publish failed.'
    }

    function Start-PhoneApp {
        $env:PhoneTesting__Enabled = 'true'
        & aspire start --non-interactive --apphost $appHost
        if ($LASTEXITCODE -ne 0) {
            throw 'Aspire could not start phone-testing mode.'
        }

        foreach ($resource in @(
            @{ Name = 'postgres'; Timeout = 120 },
            @{ Name = 'keycloak'; Timeout = 180 },
            @{ Name = 'phone-tunnel'; Timeout = 180 },
            @{ Name = 'app'; Timeout = 120 }
        )) {
            & aspire wait $resource.Name --timeout $resource.Timeout --apphost $appHost
            if ($LASTEXITCODE -ne 0) {
                throw "Phone-testing resource '$($resource.Name)' did not become healthy."
            }
        }
    }

    Start-PhoneApp
    $secrets = Get-Secrets
    $description = & aspire describe --include-hidden --format Json --apphost $appHost |
        ConvertFrom-Json
    $tunnelPort = $description.resources |
        Where-Object { $_.resourceType -eq 'DevTunnelPort' } |
        Select-Object -First 1
    $actualOrigin = [string]$tunnelPort.properties.TunnelUrl
    if (-not [Uri]::IsWellFormedUriString($actualOrigin, [UriKind]::Absolute)) {
        throw 'Aspire did not publish the phone tunnel URL.'
    }

    if ($actualOrigin -ne $secrets['PhoneTesting:PublicOrigin']) {
        Set-Secret 'PhoneTesting:PublicOrigin' $actualOrigin
        Write-Realm `
            $actualOrigin `
            $secrets['Parameters:phone-app-client-secret'] `
            $secrets['PhoneTesting:TestPassword']
        & aspire stop --non-interactive --apphost $appHost | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'Aspire could not restart after allocating the tunnel URL.'
        }
        Start-Sleep -Seconds 5
        Start-PhoneApp
    }

    Write-Host ""
    Write-Host "Receipt Keep phone URL: $actualOrigin"
    Write-Host "First Safari visit: Microsoft Dev Tunnels shows a safety page."
    Write-Host "Tap Continue before the Receipt Keep sign-in page can load."
    Write-Host "If Safari looks blank, reload the page once to reveal the safety prompt."
}

function Invoke-Stop {
    & aspire stop --non-interactive --apphost $appHost
}

function Show-Status {
    & aspire ps --format Json
    $secrets = Get-Secrets
    if ($secrets.ContainsKey('PhoneTesting:PublicOrigin')) {
        Write-Host "Configured phone URL: $($secrets['PhoneTesting:PublicOrigin'])"
    }
}

function Show-Credentials {
    $secrets = Get-Secrets
    if (-not $secrets.ContainsKey('PhoneTesting:TestPassword')) {
        throw 'Phone testing is not configured. Run setup first.'
    }
    Write-Host 'Username: phone-tester'
    Write-Host "Password: $($secrets['PhoneTesting:TestPassword'])"
}

switch ($Action) {
    'setup' { Invoke-Setup }
    'start' { Invoke-Start }
    'stop' { Invoke-Stop }
    'status' { Show-Status }
    'credentials' { Show-Credentials }
}
