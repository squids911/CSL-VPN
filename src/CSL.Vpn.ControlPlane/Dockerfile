FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/CSL.Vpn.Shared/CSL.Vpn.Shared.csproj src/CSL.Vpn.Shared/
COPY src/CSL.Vpn.ControlPlane/CSL.Vpn.ControlPlane.csproj src/CSL.Vpn.ControlPlane/
RUN dotnet restore src/CSL.Vpn.ControlPlane/CSL.Vpn.ControlPlane.csproj

COPY src/CSL.Vpn.Shared/ src/CSL.Vpn.Shared/
COPY src/CSL.Vpn.ControlPlane/ src/CSL.Vpn.ControlPlane/
# Migrations are embedded into the assembly (MigrationRunner applies them at startup).
COPY db/migrations/ db/migrations/
RUN dotnet publish src/CSL.Vpn.ControlPlane/CSL.Vpn.ControlPlane.csproj --configuration Release --output /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "CSL.Vpn.ControlPlane.dll"]
