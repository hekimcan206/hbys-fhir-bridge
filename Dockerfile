# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
# Alpine SDK: smaller pull; the output is portable IL (no apphost), so it runs unchanged on the chiseled image.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/HbysFhirBridge.Core/HbysFhirBridge.Core.csproj src/HbysFhirBridge.Core/
COPY src/HbysFhirBridge.Synthetic/HbysFhirBridge.Synthetic.csproj src/HbysFhirBridge.Synthetic/
COPY src/HbysFhirBridge.Oracle/HbysFhirBridge.Oracle.csproj src/HbysFhirBridge.Oracle/
COPY src/HbysFhirBridge.Fhir/HbysFhirBridge.Fhir.csproj src/HbysFhirBridge.Fhir/
COPY src/HbysFhirBridge.Worker/HbysFhirBridge.Worker.csproj src/HbysFhirBridge.Worker/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/HbysFhirBridge.Worker/HbysFhirBridge.Worker.csproj

COPY .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/HbysFhirBridge.Worker/HbysFhirBridge.Worker.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="hbys-fhir-bridge" \
      org.opencontainers.image.source="https://github.com/hekimcan206/hbys-fhir-bridge" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "hbys-fhir-bridge.dll"]
