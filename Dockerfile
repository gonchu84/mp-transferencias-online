# Build + Run (ASP.NET Core .NET 8)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/MpTransferenciasLocal/MpTransferenciasLocal.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .

# Muchos hosts setean PORT. El Program.cs lo lee y bindea 0.0.0.0.
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}
EXPOSE 8080

ENTRYPOINT ["dotnet", "MpTransferenciasLocal.dll"]
