# ═══════════════════════════════════════════════════════════
# FILE: src/OrderCatalogService.Api/Dockerfile
# Multi-stage build for optimized production image
# ═══════════════════════════════════════════════════════════

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["OrderCatalogService.sln", "./"]
COPY ["src/OrderCatalogService.Api/OrderCatalogService.Api.csproj", "src/OrderCatalogService.Api/"]
COPY ["src/OrderCatalogService.Contracts/OrderCatalogService.Contracts.csproj", "src/OrderCatalogService.Contracts/"]

# Restore dependencies (cached layer)
RUN dotnet restore "OrderCatalogService.sln"

# Copy entire source code
COPY . .

# Build application
WORKDIR "/src/src/OrderCatalogService.Api"
RUN dotnet build "OrderCatalogService.Api.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "OrderCatalogService.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Create non-root user for security
RUN groupadd -r appuser && useradd -r -g appuser appuser

# Copy published output
COPY --from=publish /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose ports
EXPOSE 8080
EXPOSE 8081

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail http://localhost:8080/health/live || exit 1

# Environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Entry point
ENTRYPOINT ["dotnet", "OrderCatalogService.Api.dll"]

# ═══════════════════════════════════════════════════════════
# FILE: src/OrderCatalogService.Api/.dockerignore
# ═══════════════════════════════════════════════════════════

# Binaries
**/bin/
**/obj/
**/out/

# IDE
.vs/
.vscode/
.idea/
*.user
*.suo

# Build artifacts
**/publish/
**/TestResults/

# Git
.git/
.gitignore
.gitattributes

# Docker
**/Dockerfile*
**/docker-compose*

# Documentation
**/*.md
LICENSE

# Tests
**/tests/

