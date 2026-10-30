# Receipt Keep — offline-capable household HSA receipt tracker

A .NET 10 Blazor WebAssembly PWA with an ASP.NET Core backend-for-frontend (BFF) for two people. Receipts live on your Synology, not in a hosted service. One household collection; either approved account can see and update everything.

## Version one

- Upload one JPG, PNG, or PDF (up to 15 MB) per receipt.
- Record provider, patient, service date, amount paid, and optional notes.
- Edit details; original receipt file remains unchanged.
- Mark a receipt reimbursed with a date, or undo it.
- Search, filter by reimbursement status, and see the household unreimbursed total.
- Download original receipts individually and export metadata as CSV.
- Two Keycloak accounts with public registration disabled and an app username allowlist.
- Installable mobile/desktop PWA.
- Optional offline metadata snapshot and local new-receipt drafts, including one JPG/PNG/PDF.
- Explicit, retry-safe submission of pending drafts after reconnecting and signing in as the same user.

Submitted receipt files, edits, reimbursements, downloads, exports, and sign-in remain online-only. Offline data is stored in the browser profile, can be removed by browser storage cleanup, and is not included in NAS backups. Use offline access only on a trusted device.

Not included: OCR, partial reimbursements, bulk ZIP downloads, deleting/replacing receipt files, automated tax eligibility decisions, cross-server synchronization, background submission, or Apple sign-in. The app records amounts you enter; it does not determine HSA eligibility. Passkeys are an optional Keycloak setup step, not preconfigured.

## Requirements

Synology with Docker/Container Manager support and sufficient free RAM for your existing services plus Keycloak/PostgreSQL. The exact NAS model and resource capacity have not been checked. Python 3 generates private configuration, and the Aspire CLI generates Docker Compose deployment artifacts. Building requires internet access to Microsoft container images, NuGet, Quay, and Docker Hub.

## 1. Generate your private configuration

Extract the project and open its folder in VS Code. Run:

```sh
python3 setup.py
```

Enter your app and authentication hostnames, two usernames, and the exact trusted proxy IP address as seen from inside the app container.

For a reverse proxy on the NAS host forwarding to a Docker-published port, this address may be a Docker bridge gateway, rather than your LAN address. Inspect the actual network gateway using `docker network inspect` and verify forwarded HTTPS is recognized during the sign-in test. Do not blindly trust all proxy addresses. The app already trusts loopback by framework default; explicitly configure the observed source address and, if the socket presents an IPv4-mapped IPv6 address, that exact mapped address too. Comma-separated addresses are supported.

The script creates `.env` and `deploy/realm.json`. These contain the generated application image name, random secrets, and temporary household passwords. Keep them private and transfer them securely with the project; they are ignored by git. `realm.json` is readable to the Keycloak container user, so restrict the surrounding project folder to NAS administrators.

## 2. Put the project on your NAS

For example, use `/volume1/docker/hsa-receipts`. Create a persistent app data folder and allow the .NET container user (uid 1654) to write to it. Using SSH from the project directory:

```sh
sudo mkdir -p data/app
sudo chown -R 1654:1654 data/app
sudo chmod 700 data/app
```

Do not recursively change ownership of `data/postgres`; its container manages it. NAS ACLs may also need adjustment to allow the container user access.

## 3. Add your reverse proxy entries

| HTTPS source | HTTP destination on the same NAS |
| --- | --- |
| `hsa.yourdomain.com:443` | `127.0.0.1:5088` |
| `auth.yourdomain.com:443` | `127.0.0.1:5089` |

Use valid HTTPS certificates. Preserve the public Host header and have the proxy overwrite `X-Forwarded-Proto` with `https` and set `X-Forwarded-For` correctly. Restrict accepted hostnames to the two configured domains. Only ports 443 (and any certificate validation port your setup requires) should be forwarded from the router; the Compose application ports bind to NAS loopback.

This assumes your reverse proxy runs on the NAS host. If yours runs in another container or on a separate device, adjust the port bindings/network and firewall rules to permit only that proxy, and configure its exact source IP in `TRUSTED_PROXIES`.

In the authentication reverse proxy, restrict `/admin/` to LAN/VPN access. Keycloak needs public realm sign-in endpoints, not public administration access.

Both the phone and the application container must be able to resolve and reach `https://auth.yourdomain.com`. Use split DNS or hairpin NAT where necessary. Keep HTTPS certificate validation enabled.

## 4. Build and start

Generate the Docker Compose artifact and build the application image:

```sh
aspire publish --apphost src/HsaReceipts.AppHost/HsaReceipts.AppHost.csproj --output-path aspire-output --non-interactive
docker build --tag hsa-receipts-app:latest .
cp .env aspire-output/.env
cd aspire-output
docker compose config --quiet
docker compose up -d
docker compose logs --tail=100 app keycloak
```

Alternatively, create a Container Manager project pointing at `aspire-output/docker-compose.yaml` and its copied `.env` file. The AppHost is the source of truth; regenerate the artifact after changing services or deployment settings. First startup of Keycloak can take time. Retry sign-in after startup completes.

Container image versions are pinned to known releases; this is not a claim that they are the newest security patches. Review supported patch versions and NuGet updates before exposing this deployment, and maintain them with your other NAS applications. .NET base images use the 10.0 servicing tag.

## 5. First sign-in

Open the app's HTTPS address. Each household user's temporary password is in `deploy/realm.json` under that username. Keycloak requires a password change on first sign-in. No SMTP server is configured; an administrator must reset forgotten passwords.

The Keycloak bootstrap login is `bootstrap-admin`; its password is in `.env`. Create a permanent administrative account with a strong password and MFA, then remove the temporary bootstrap administrator and the bootstrap variables from Compose/.env. Use household accounts for the app, not the administrator.

Realm imports happen only when `household` does not already exist. Rebuilding containers or editing `realm.json` does not update an existing realm. Make later account/client changes through Keycloak's administration interface, and keep your app allowlist synchronized if usernames change. Do not delete the PostgreSQL data to reconfigure accounts.

### Optional passkeys

Start with passwords to verify the deployment. Keycloak supports WebAuthn/passwordless authentication, but enabling it requires configuring its authentication flow and registering each user's credential. Follow the documentation for the installed Keycloak version. Keep an administrator recovery path before changing the sign-in flow. This project does not enable Apple federation or passkeys automatically.

### Install and use offline

Open the app once while online and sign in. On the receipt list, enable **Offline access** only on a trusted browser profile. Then install it from the browser:

- iPhone/iPad Safari: Share → Add to Home Screen.
- Chrome/Edge: use the install action in the address bar or browser menu.

The application shell, opted-in receipt metadata, and unsent drafts can reopen without the server. Original files already submitted to the NAS remain online-only. Pending drafts are never submitted automatically; reconnect, sign in as the same user, and choose **Submit pending receipts**.

Signing out or disabling offline access clears private local data after warning about unsent drafts. Browser storage eviction or clearing site data can also remove drafts. Camera/file picker behavior depends on iOS; if an existing HEIC photo is rejected, export it as JPG, or upload a PDF scan.

## Backups

Back up the entire `data/app` directory, including `receipts.db`, `receipts/`, and `keys/`. Stored filenames use stable receipt IDs; CSV includes the ID and original filename. Individual downloads use the original name with a normalized extension. CSV alone does not contain receipt images.

For the simplest consistent backup, stop the project before copying all project data with your NAS backup tool, then start it again:

```sh
cd aspire-output
docker compose stop
# Run your NAS backup of data/, .env, and deploy/realm.json now.
docker compose start
```

Back up PostgreSQL consistently (stopped volume or `pg_dump`), not a live filesystem copy. NAS storage alone is not a second backup. Protect backups like the receipt files themselves. Restore `keys/` with app data; cookies become invalid if keys are lost.

## Development

Requires the .NET 10 SDK, Docker, and a trusted ASP.NET Core development certificate.

```sh
dotnet dev-certs https --trust
aspire run --apphost src/HsaReceipts.AppHost/HsaReceipts.AppHost.csproj
```

Open `https://localhost:7088` and sign in with username `developer` and password `developer`. The local realm, credentials, and client secret are intentionally development-only and must never be used for deployment.

Stop the AppHost with Ctrl+C when finished. In VS Code, use the Aspire tooling to start or debug the AppHost.

The local profile disables HTTPS metadata only while `ASPNETCORE_ENVIRONMENT=Development`; startup rejects that setting in every other environment. Browser cookies and the application itself still use HTTPS.

Blazor's offline service worker is active only for published output. To validate the actual PWA locally, publish the server and run the published application with the same development authentication settings, then use browser developer tools to confirm the service worker and offline reload behavior:

```sh
dotnet publish src/HsaReceipts/HsaReceipts.csproj -c Release -o .artifacts/publish
```

### Test the published PWA from a phone

Phone testing uses an anonymous Microsoft Dev Tunnel for one published Release instance. The tunnel URL is public, but receipt data and APIs still require the isolated `phone-tester` Keycloak account. The Aspire dashboard, PostgreSQL, Keycloak administration, the master realm, and other realms are not exposed. Do not enter real receipt or identity data in this test mode.

On Windows PowerShell:

```powershell
.\phone-testing.ps1 setup
.\phone-testing.ps1 start
.\phone-testing.ps1 credentials
```

`setup` installs no application dependencies; it requires the Microsoft Dev Tunnels CLI, signs in to Dev Tunnels when necessary, creates a persistent tunnel, and generates ignored test-only configuration under `data\phone-testing`. `start` publishes the app, starts the isolated Aspire resources, waits for them to become healthy, and prints the authoritative public HTTPS URL.

Open that URL on the phone and accept Microsoft's one-time developer-tunnel warning. Sign in with the credentials printed by `credentials`, enable **Offline access** only on a trusted device, and install the PWA from the browser menu. The phone can use Wi-Fi or cellular data; the development machine must remain online with Aspire running.

```powershell
.\phone-testing.ps1 status
.\phone-testing.ps1 stop
```

Treat the URL as publicly discoverable. Stop phone mode when testing is finished. Tunnel transport is anonymous by design so Keycloak can complete browser redirects; the restricted same-origin proxy exposes only the `household` realm sign-in/resources paths.

## CI and releases

The root [Directory.Build.props](Directory.Build.props) defines the shared .NET product version for all five projects, including the AppHost and tests. Semantic-release updates its single `Version` property automatically; do not manually bump it or duplicate version properties in individual project files. The SDK derives assembly and file versions from it and retains normal source-revision metadata in informational versions.

Compiler/analyzer and NuGet warnings are errors in both Debug and Release builds. CI also passes `-warnaserror` during restore and build so warnings from other MSBuild tasks fail the checks. Docker builds copy the shared properties file before restore and inherit the same version and warning policy.

The [CI workflow](.github/workflows/ci.yml) runs on pull requests targeting `main` and pushes to `main`. Its **Build and Test** check restores the required .NET workloads (including `wasm-tools`) and builds the full solution in Release mode, runs the xUnit tests and Python configuration tests, and retains TRX test results for seven days. It does not start Aspire services or require private configuration, Docker, or deployment secrets.

Run the same checks locally with the .NET 10 SDK and Python 3:

```sh
dotnet workload restore HsaReceipts.slnx
dotnet restore HsaReceipts.slnx -warnaserror
dotnet build HsaReceipts.slnx --configuration Release --no-restore -warnaserror
dotnet test test/HsaReceipts.Tests/HsaReceipts.Tests.csproj --configuration Release --no-build --no-restore --logger trx --results-directory TestResults
python3 -m unittest discover -s test -v
```

After all checks pass on a push to `main`, semantic-release analyzes commits since the previous release, updates the shared .NET version and [CHANGELOG.md](CHANGELOG.md), and commits those two files as `chore(release): X.Y.Z [skip ci]`. The `vX.Y.Z` tag points to that release commit, and the GitHub release contains the generated notes. Release jobs are serialized. Pull requests never publish releases, and a failed check blocks publishing.

Use Conventional Commit messages, including the final squash-merge commit:

| Commit | Release |
| --- | --- |
| `fix: correct receipt total` | Patch |
| `perf: reduce receipt query allocations` | Patch |
| `feat: add receipt filtering` | Minor |
| A commit with a `BREAKING CHANGE:` footer | Major |
| `docs:`, `chore:`, or `ci:` alone | None |

For a breaking change, preserve the footer in the squash commit body:

```text
feat: change receipt export format

BREAKING CHANGE: exported dates now use ISO 8601.
```

Git tags are the release-version source of truth; each new release synchronizes the shared .NET version and changelog with its tag. The version updater parses the XML and changes only the shared version value, preserving other build settings. This pipeline does not publish packages or container images or deploy the application. GitHub provides source archives automatically; no application binaries are attached. Without an existing release tag, the first release-worthy history produces `v1.0.0`. A CI-only change does not force a release, although earlier unreleased features or fixes can still trigger one.

Before enabling releases, confirm the repository's default branch is `main` and inspect existing `v*` tags. Preserve valid historical release tags; do not recreate or rewrite them. Require **Build and Test** in branch protection, and ensure repository/organization Actions policy and rulesets allow the release identity to push release commits to `main` as well as create tags and releases. The workflow uses `GITHUB_TOKEN` by default, with `contents: write` only on the release job. That token does not automatically bypass required pull requests, status checks, or signed-commit rules; protected branches may need an explicitly approved release-bot exception or a GitHub App token configured in the workflow. Do not disable branch protections to make releases work. No npm token is needed. Issue/PR comments and labels are disabled in the [release configuration](.releaserc.json).

Before the first real release, preview from an up-to-date, trusted `main` checkout with full history and tags, Node.js 24.15 or newer, and an authorized `GITHUB_TOKEN` supplied through your environment:

```sh
npx --yes --package=semantic-release@25.0.9 --package=@semantic-release/changelog@7.0.0 --package=@semantic-release/exec@7.1.0 --package=@semantic-release/git@11.0.1 semantic-release --dry-run --no-ci
```

The dry run verifies push permissions and reports the calculated version and notes, but skips release preparation: it does not change the XML or changelog, create a release commit, or publish a tag or release. Do not expose a release token to pull-request code. Release commits include `[skip ci]` to prevent redundant builds, and pushes/releases created with the built-in `GITHUB_TOKEN` do not trigger separate workflows; this pipeline does not depend on those events.

## Validation status and acceptance checks

Configuration generator tests and AppHost publishing can be validated with:

```sh
python3 -m unittest discover -s test -v
dotnet test test/HsaReceipts.Tests/HsaReceipts.Tests.csproj -c Release
dotnet build HsaReceipts.slnx -c Release -warnaserror
aspire publish --apphost src/HsaReceipts.AppHost/HsaReceipts.AppHost.csproj --output-path aspire-output --non-interactive
```

Browser layout, OIDC login, and Synology deployment still require validation on the target environment.

Before entering real receipts:

1. Run `dotnet build` or the Docker build successfully.
2. Sign in separately with both accounts; verify the same list is visible.
3. In a private browser window, request `/?handler=Export` and a receipt download URL; both must require login.
4. Add a non-allowlisted Keycloak test account and verify it cannot access the app.
5. Upload a sample JPG, PNG, and PDF; download each and compare file hashes. Reject unsupported files and files over 15 MB.
6. Enter an amount such as $12.34, edit it, mark reimbursed, confirm the outstanding total, undo, and check CSV. A provider beginning with `=` must export as text.
7. Sign out and verify session handling; refresh, restart containers, and verify records and files survive.
8. Enable offline access, install the PWA, load the list offline, create a draft, close/reopen it, reconnect, and explicitly submit it once.
9. Verify signing out warns about unsent drafts and clears local private data.
10. Verify iPhone layout, photo upload, Home Screen launching, and offline draft behavior.
11. Test a backup restore before depending on the records.

## Reference documentation

- ASP.NET Core trusted proxies: https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0
- Keycloak container deployment: https://www.keycloak.org/server/containers
- Keycloak reverse proxy settings: https://www.keycloak.org/server/reverseproxy
- Keycloak administration/WebAuthn: https://www.keycloak.org/docs/latest/server_admin/
