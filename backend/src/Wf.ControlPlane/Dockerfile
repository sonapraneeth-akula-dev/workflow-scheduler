FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY proto/ proto/
COPY db/ db/
COPY src/Wf.Contracts/ src/Wf.Contracts/
COPY src/Wf.Engine.Core/ src/Wf.Engine.Core/
COPY src/Wf.Sdk/ src/Wf.Sdk/
COPY src/Wf.Validation/ src/Wf.Validation/
COPY src/Wf.ControlPlane/ src/Wf.ControlPlane/
RUN --mount=type=cache,id=kardia-nuget,target=/root/.nuget/packages,sharing=locked \
    --mount=type=cache,id=kardia-nuget-http,target=/root/.local/share/NuGet/http-cache,sharing=locked \
    dotnet publish src/Wf.ControlPlane/Wf.ControlPlane.csproj -c Release --no-self-contained -o /app \
    -p:WarningsNotAsErrors=NU1902%3BNU1903 \
    --source "$NUGET_SOURCE"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update -qq && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/wf/blobs /var/lib/wf/secrets \
    && chown -R app:app /var/lib/wf && chmod 700 /var/lib/wf/blobs /var/lib/wf/secrets
USER app
ENTRYPOINT ["dotnet", "Wf.ControlPlane.dll"]
