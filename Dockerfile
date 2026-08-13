FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore WaterMeterServer.WorkerService/WorkerService.csproj
RUN dotnet publish WaterMeterServer.WorkerService/WorkerService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

RUN mkdir -p /var/lib/water-meter/firmware
COPY --from=build /app/publish .

EXPOSE 502 5080
ENTRYPOINT ["dotnet", "WorkerService.dll"]
