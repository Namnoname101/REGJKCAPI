FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY deploy/SePayGateway/SePayGateway.csproj deploy/SePayGateway/
RUN dotnet restore deploy/SePayGateway/SePayGateway.csproj
COPY deploy/SePayGateway/ deploy/SePayGateway/
RUN dotnet publish deploy/SePayGateway/SePayGateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Gateway__DatabasePath=/data/sepay-gateway.db
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SePayGateway.dll"]
