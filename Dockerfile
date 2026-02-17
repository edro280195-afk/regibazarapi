# 1. Usar la imagen oficial de .NET SDK para construir
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias
COPY *.csproj ./
RUN dotnet restore

# Copiar todo lo demás y construir
COPY . .
RUN dotnet publish -c Release -o /app/publish

# 2. Usar la imagen ligera para ejecutar
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Configurar puerto para Render (importante)
ENV ASPNETCORE_URLS=http://+:80

# Arrancar la app
ENTRYPOINT ["dotnet", "EntregasApi.dll"]