FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/CSL.Vpn.ControlPlane/CSL.Vpn.ControlPlane.csproj ./
RUN dotnet restore ./CSL.Vpn.ControlPlane.csproj
COPY src/CSL.Vpn.ControlPlane/ ./
RUN dotnet publish ./CSL.Vpn.ControlPlane.csproj --configuration Release --output /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /out/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "CSL.Vpn.ControlPlane.dll"]
