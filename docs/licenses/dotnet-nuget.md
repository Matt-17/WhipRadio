# .NET and NuGet Licenses

Last reviewed: 2026-06-20

Sources used:

- `*.csproj` files under `src/` and `tests/`
- `dotnet restore .\WhipRadio.slnx --artifacts-path D:\tmp\whipradio-license-restore`
- NuGet `.nuspec` license metadata from the restored package cache
- NuGet package metadata and package-local license files where the `.nuspec` points to an in-package license

The restore completed, but NuGet reported `NU1903` for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11: GHSA-2m69-gcr7-jv3q. That is a security issue rather than a license issue, but it should be tracked before distribution.

## Direct Package References

| Area | Package | Version | License from package metadata |
| --- | --- | ---: | --- |
| AppHost SDK | `Aspire.Dashboard.Sdk.win-x64` | 13.4.2 | MIT |
| AppHost SDK | `Aspire.Hosting.AppHost` | 13.4.2 | MIT |
| AppHost SDK | `Aspire.Hosting.Orchestration.win-x64` | 13.4.2 | MIT |
| AppHost | `MessagePack` | 3.1.7 | MIT |
| ServiceDefaults | `Microsoft.Extensions.Http.Resilience` | 10.6.0 | MIT |
| ServiceDefaults | `Microsoft.Extensions.ServiceDiscovery` | 10.6.0 | MIT |
| ServiceDefaults | `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.3 | Apache-2.0 |
| ServiceDefaults | `OpenTelemetry.Extensions.Hosting` | 1.15.3 | Apache-2.0 |
| ServiceDefaults | `OpenTelemetry.Instrumentation.AspNetCore` | 1.15.2 | Apache-2.0 |
| ServiceDefaults | `OpenTelemetry.Instrumentation.Http` | 1.15.1 | Apache-2.0 |
| ServiceDefaults | `OpenTelemetry.Instrumentation.Runtime` | 1.15.1 | Apache-2.0 |
| Infrastructure | `Microsoft.EntityFrameworkCore.Design` | 10.0.9 | MIT |
| Infrastructure | `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.9 | MIT |
| Infrastructure | `Microsoft.Extensions.Http` | 10.0.9 | MIT |
| Infrastructure | `Microsoft.Extensions.Http.Resilience` | 10.7.0 | MIT |
| Orchestrator | `Microsoft.EntityFrameworkCore.Design` | 10.0.9 | MIT |
| Web | `Microsoft.AspNetCore.SignalR.Client` | 10.0.9 | MIT |
| Tests | `coverlet.collector` | 6.0.4 | MIT |
| Tests | `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT |
| Tests | `MSTest.TestAdapter` | 4.2.3 | MIT |
| Tests | `MSTest.TestFramework` | 4.2.3 | MIT |

## Resolved Transitive License Groups

The following groups are from restored NuGet metadata. Package versions can differ across projects because the solution currently resolves some Microsoft extension packages in both 10.0.8/10.0.9 and 10.6.0/10.7.0 lines.

### Apache-2.0

`AspNetCore.HealthChecks.Uris`, `Grpc.AspNetCore`, `Grpc.AspNetCore.Server`, `Grpc.AspNetCore.Server.ClientFactory`, `Grpc.Core.Api`, `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, `Grpc.Net.Common`, `Grpc.Tools`, `KubernetesClient`, `ModelContextProtocol`, `ModelContextProtocol.Core`, `OpenTelemetry`, `OpenTelemetry.Api`, `OpenTelemetry.Api.ProviderBuilderExtensions`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.core`, `SQLitePCLRaw.lib.e_sqlite3`, `SQLitePCLRaw.provider.e_sqlite3`.

### BSD-3-Clause

`Google.Protobuf`, `Polly.Core`, `Polly.Extensions`, `Polly.RateLimiting`.

### MIT

`Aspire.*`, `coverlet.collector`, `Humanizer.Core`, `Json.More.Net`, `JsonPatch.Net`, `JsonPointer.Net`, `MessagePack`, `MessagePack.Annotations`, `MessagePackAnalyzer`, `Microsoft.*`, `Mono.TextTemplating`, `MSTest.*`, `Nerdbank.Streams`, `Newtonsoft.Json`, `Semver`, `StreamJsonRpc`, `System.*`, `YamlDotNet`.

### Package-local license file

`Fractions` 7.3.0 reports `license.txt` in package metadata. Keep its package license file with any binary redistribution notice bundle.

## Regeneration Notes

Use an isolated artifacts path when the station is running:

```powershell
dotnet restore .\WhipRadio.slnx --artifacts-path D:\tmp\whipradio-license-restore
```

Then inspect `D:\tmp\whipradio-license-restore\obj\*\project.assets.json` and the corresponding `.nuspec` files in the NuGet cache. `dotnet list package --include-transitive --format json` is also useful after a normal restore, but it does not read the isolated artifacts path automatically.

For a release, prefer generating a machine-readable SBOM from the restored graph and copying package-local license files into the release notice bundle.
