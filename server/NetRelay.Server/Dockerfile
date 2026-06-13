FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NuGet.Config NetRelay.sln ./
COPY src/NetRelay.Contracts/NetRelay.Contracts.csproj src/NetRelay.Contracts/
COPY server/NetRelay.Server/NetRelay.Server.csproj server/NetRelay.Server/
RUN dotnet restore server/NetRelay.Server/NetRelay.Server.csproj
COPY src/NetRelay.Contracts/ src/NetRelay.Contracts/
COPY server/NetRelay.Server/ server/NetRelay.Server/
RUN dotnet publish server/NetRelay.Server/NetRelay.Server.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
RUN addgroup --system netrelay \
    && adduser --system --ingroup netrelay netrelay \
    && mkdir -p /var/lib/netrelay/data-protection /var/lib/netrelay/releases /var/lib/netrelay/feedback-attachments \
    && chown -R netrelay:netrelay /var/lib/netrelay
COPY --from=build /app/publish .
USER netrelay
EXPOSE 8080
ENTRYPOINT ["dotnet", "NetRelay.Server.dll"]
