FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HsaReceipts/HsaReceipts.csproj src/HsaReceipts/
COPY src/HsaReceipts.Client/HsaReceipts.Client.csproj src/HsaReceipts.Client/
COPY src/HsaReceipts.Contracts/HsaReceipts.Contracts.csproj src/HsaReceipts.Contracts/
RUN dotnet restore src/HsaReceipts/HsaReceipts.csproj
COPY src/HsaReceipts/ src/HsaReceipts/
COPY src/HsaReceipts.Client/ src/HsaReceipts.Client/
COPY src/HsaReceipts.Contracts/ src/HsaReceipts.Contracts/
RUN dotnet publish src/HsaReceipts/HsaReceipts.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "HsaReceipts.dll"]
