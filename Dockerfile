# ---------- Build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files and restore first (layer-cached)
COPY RapidApiCrawler.Domain/RapidApiCrawler.Domain.csproj RapidApiCrawler.Domain/
COPY RapidApiCrawler.Application/RapidApiCrawler.Application.csproj RapidApiCrawler.Application/
COPY RapidApiCrawler.Infrastructure/RapidApiCrawler.Infrastructure.csproj RapidApiCrawler.Infrastructure/
COPY RapidApiCrawler.Web/RapidApiCrawler.Web.csproj RapidApiCrawler.Web/
RUN dotnet restore RapidApiCrawler.Web/RapidApiCrawler.Web.csproj -r linux-x64

# Copy the rest and publish
COPY RapidApiCrawler.Domain/ RapidApiCrawler.Domain/
COPY RapidApiCrawler.Application/ RapidApiCrawler.Application/
COPY RapidApiCrawler.Infrastructure/ RapidApiCrawler.Infrastructure/
COPY RapidApiCrawler.Web/ RapidApiCrawler.Web/
RUN dotnet publish RapidApiCrawler.Web/RapidApiCrawler.Web.csproj \
    -c Release -r linux-x64 --no-restore --self-contained false \
    -o /app/publish

# ---------- Runtime stage ----------
# CUDA runtime base so libcudart / cublas (needed by LLamaSharp.Backend.Cuda12) exist.
# The NVIDIA Container Toolkit injects the host driver at run time.
FROM nvidia/cuda:12.6.0-runtime-ubuntu24.04

# Install the .NET 10 runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl unzip \
    && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm -rf /var/lib/apt/lists/* /tmp/dotnet-install.sh

WORKDIR /app
COPY --from=build /app/publish .

# Install Chromium + OS dependencies for Playwright (the crawler drives a real browser)
RUN chmod +x ./playwright.sh && ./playwright.sh install --with-deps chromium

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENTRYPOINT ["dotnet", "RapidApiCrawler.Web.dll"]
