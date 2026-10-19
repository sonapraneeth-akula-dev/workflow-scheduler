FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY proto/ proto/
COPY src/Wf.Contracts/ src/Wf.Contracts/
COPY src/Wf.Agent/ src/Wf.Agent/
RUN dotnet publish src/Wf.Agent/Wf.Agent.csproj -c Release -p:PublishAot=false --no-self-contained -o /app \
    --source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/wf-agent && chown app:app /var/lib/wf-agent && chmod 700 /var/lib/wf-agent
USER app
ENTRYPOINT ["dotnet", "wf-agent.dll"]
