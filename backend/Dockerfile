# Build from the repository root: docker build -f backend/Dockerfile .  (specs/006 research R2)
# The SDK stage runs on the build machine's platform; the published output is portable, so arm64 needs no emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY backend/InTheKnow/ backend/InTheKnow/
RUN dotnet restore backend/InTheKnow/InTheKnow.csproj --locked-mode
RUN dotnet publish backend/InTheKnow/InTheKnow.csproj -c Release --no-restore -p:UseAppHost=false -o /app

# Ubuntu, not chiseled: the stack's healthcheck needs bash, DefaultTimeZone needs ICU and tzdata. No RUN here.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "InTheKnow.dll"]
