FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json
WORKDIR /src/backend
COPY backend/Directory.Build.props backend/Directory.Packages.props ./
COPY backend/proto/ proto/
COPY backend/db/ db/
COPY backend/src/Wf.Contracts/ src/Wf.Contracts/
COPY backend/src/Wf.Engine.Core/ src/Wf.Engine.Core/
COPY backend/src/Wf.Sdk/ src/Wf.Sdk/
COPY backend/src/Wf.Validation/ src/Wf.Validation/
COPY backend/src/Wf.ControlPlane/ src/Wf.ControlPlane/
COPY failure-categories/ /src/failure-categories/
RUN --mount=type=cache,id=kardia-nuget,target=/root/.nuget/packages,sharing=locked \
    --mount=type=cache,id=kardia-nuget-http,target=/root/.local/share/NuGet/http-cache,sharing=locked \
    dotnet publish src/Wf.ControlPlane/Wf.ControlPlane.csproj -c Release --no-self-contained -o /app \
    -p:WarningsNotAsErrors=NU1902%3BNU1903 \
    --source "$NUGET_SOURCE"

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
RUN apt-get update -qq && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/wf/blobs /var/lib/wf/secrets \
    && chown -R app:app /var/lib/wf && chmod 700 /var/lib/wf/blobs /var/lib/wf/secrets
USER app
ENTRYPOINT ["dotnet", "Wf.ControlPlane.dll"]
