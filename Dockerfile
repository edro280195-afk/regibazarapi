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

ENV PATH="/usr/local/bin:${PATH}"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ffmpeg \
        python3 \
        python3-pip \
        tesseract-ocr \
        tesseract-ocr-eng \
        tesseract-ocr-spa \
    && python3 -m pip install --no-cache-dir --break-system-packages -U yt-dlp \
    && ln -sf /usr/local/bin/yt-dlp /usr/bin/yt-dlp \
    && yt-dlp --version \
    && ffmpeg -version >/dev/null \
    && tesseract --version >/dev/null \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Configurar puerto para Render (importante)
ENV ASPNETCORE_URLS=http://+:80

# Arrancar la app
ENTRYPOINT ["dotnet", "EntregasApi.dll"]
