# Complete Setup Guide - Order & Catalog Production-Grade Microservice Setup Guide
**.NET 8 • REST • GraphQL • CQRS • Redis • PostgreSQL • Azure-Ready**

This guide describes building a modern, cloud-native microservice supporting:

- REST + GraphQL APIs  
- OAuth 2.0 + JWT  
- API Versioning  
- Rate Limiting & Throttling  
- Redis Caching & Idempotency  
- Outbox Pattern  
- CQRS + MediatR  
- EF Core + PostgreSQL  
- Azure Service Bus  
- Serilog + Seq + OpenTelemetry  
- Docker / Kubernetes Ready  

---

## 📋 Prerequisites

Ensure you have the following installed:

- **.NET 8 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Docker Desktop** - [Download](https://www.docker.com/products/docker-desktop)
- **Visual Studio 2022** or **VS Code** with C# extension
- **Git**

---

## 🚀 Step 1: Create Solution Structure

### 1.1 Create Root Directory

```bash
mkdir OrderCatalogService
cd OrderCatalogService
```

### 1.2 Create Solution File

```bash
dotnet new sln -n OrderCatalogService
```

### 1.3 Create Project Structure

```bash
# Create main API project
dotnet new webapi -n OrderCatalogService.Api -o src/OrderCatalogService.Api
dotnet sln add src/OrderCatalogService.Api

# Create contracts library
dotnet new classlib -n OrderCatalogService.Contracts -o src/OrderCatalogService.Contracts

# Create test projects
dotnet new xunit -n OrderCatalogService.UnitTests -o tests/OrderCatalogService.UnitTests
dotnet new xunit -n OrderCatalogService.IntegrationTests -o tests/OrderCatalogService.IntegrationTests
```

### 1.4 Add Projects to Solution

```bash
dotnet sln add src/OrderCatalogService.Api/OrderCatalogService.Api.csproj
dotnet sln add src/OrderCatalogService.Contracts/OrderCatalogService.Contracts.csproj
dotnet sln add tests/OrderCatalogService.UnitTests/OrderCatalogService.UnitTests.csproj
dotnet sln add tests/OrderCatalogService.IntegrationTests/OrderCatalogService.IntegrationTests.csproj
```

### 1.5 Add Project References

```bash
# API references Contracts
dotnet add src/OrderCatalogService.Api reference src/OrderCatalogService.Contracts

# Tests reference API
dotnet add tests/OrderCatalogService.UnitTests reference src/OrderCatalogService.Api
dotnet add tests/OrderCatalogService.IntegrationTests reference src/OrderCatalogService.Api
```

---

## 📁 Step 2: Create Folder Structure

### 2.1 Navigate to API Project

```bash
cd src/OrderCatalogService.Api
```

### 2.2 Create All Folders

```bash
# Features (Vertical Slices)
mkdir -p Features/Orders/CreateOrder
mkdir -p Features/Orders/GetOrder
mkdir -p Features/Orders/GetOrders
mkdir -p Features/Orders/CancelOrder
mkdir -p Features/Products
mkdir -p Features/Carts
mkdir -p Features/Webhooks

# Domain
mkdir -p Domain/Orders
mkdir -p Domain/Products
mkdir -p Domain/Carts
mkdir -p Domain/Common

# Infrastructure
mkdir -p Infrastructure/Database
mkdir -p Infrastructure/Idempotency
mkdir -p Infrastructure/Outbox
mkdir -p Infrastructure/Webhooks
mkdir -p Infrastructure/Caching
mkdir -p Infrastructure/ServiceBus

# Common (Cross-cutting)
mkdir -p Common/Behaviors
mkdir -p Common/Middleware
mkdir -p Common/Extensions
mkdir -p Common/Pagination

# GraphQL
mkdir -p GraphQL/Types
mkdir -p GraphQL/DataLoaders

# Return to root
cd ../..
```

---

## 📦 Step 3: Install NuGet Packages

### 3.1 Navigate to API Project

```bash
cd src/OrderCatalogService.Api
```

### 3.2 Install All Required Packages

```bash
# Core ASP.NET
dotnet add package Microsoft.AspNetCore.OpenApi
dotnet add package Swashbuckle.AspNetCore

# Carter (REPR Pattern)
dotnet add package Carter

# MediatR (CQRS)
dotnet add package MediatR

# FluentValidation
dotnet add package FluentValidation.DependencyInjectionExtensions

# EF Core & PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL

# Redis
dotnet add package Microsoft.Extensions.Caching.StackExchangeRedis
dotnet add package StackExchange.Redis
dotnet add package RedLock.net

# Azure Services
dotnet add package Azure.Identity
dotnet add package Azure.Messaging.ServiceBus
dotnet add package Microsoft.Extensions.Azure

# Authentication
dotnet add package Microsoft.Identity.Web
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer

# GraphQL (HotChocolate)
dotnet add package HotChocolate.AspNetCore
dotnet add package HotChocolate.Data.EntityFramework
dotnet add package HotChocolate.AspNetCore.Authorization

# Logging (Serilog)
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.Seq

# OpenTelemetry
dotnet add package Azure.Monitor.OpenTelemetry.AspNetCore
dotnet add package OpenTelemetry.Exporter.Prometheus.AspNetCore
dotnet add package OpenTelemetry.Instrumentation.EntityFrameworkCore
dotnet add package OpenTelemetry.Instrumentation.Http
dotnet add package OpenTelemetry.Instrumentation.StackExchangeRedis

# Polly (Resilience)
dotnet add package Microsoft.Extensions.Http.Polly
dotnet add package Polly

# Rate Limiting
dotnet add package AspNetCoreRateLimit

# API Versioning
dotnet add package Asp.Versioning.Http
dotnet add package Asp.Versioning.Mvc.ApiExplorer

# Health Checks
dotnet add package AspNetCore.HealthChecks.NpgSql
dotnet add package AspNetCore.HealthChecks.Redis
dotnet add package AspNetCore.HealthChecks.AzureServiceBus
```

### 3.3 Install Test Packages

```bash
cd ../../tests/OrderCatalogService.UnitTests

# Unit Test Packages
dotnet add package xunit
dotnet add package xunit.runner.visualstudio
dotnet add package FluentAssertions
dotnet add package NSubstitute
dotnet add package Bogus

cd ../OrderCatalogService.IntegrationTests

# Integration Test Packages
dotnet add package xunit
dotnet add package xunit.runner.visualstudio
dotnet add package FluentAssertions
dotnet add package Microsoft.AspNetCore.Mvc.Testing
dotnet add package Testcontainers
dotnet add package Testcontainers.PostgreSql
dotnet add package Testcontainers.Redis

# Return to root
cd ../..
```

---

## 📝 Step 4: Add All Code Files

Now copy all the code from the artifacts I created earlier into the appropriate files:

### 4.1 Main Application File

**File:** `src/OrderCatalogService.Api/Program.cs`
- Copy code from artifact: **"Program.cs - Main Entry Point"**

### 4.2 Domain Models

**Files in** `src/OrderCatalogService.Api/Domain/Orders/`:
- `Order.cs` - Copy from artifact: **"Domain/Orders/Order.cs - Order Aggregate Root"**

**Files in** `src/OrderCatalogService.Api/Domain/Common/`:
- Create value objects and base classes as shown in the Order.cs artifact

### 4.3 Infrastructure

**File:** `src/OrderCatalogService.Api/Infrastructure/Database/OrderCatalogDbContext.cs`
- Copy from artifact: **"OrderCatalogDbContext.cs - EF Core with Concurrency Tokens"**

**File:** `src/OrderCatalogService.Api/Infrastructure/Idempotency/RedisIdempotencyService.cs`
- Copy from artifact: **"RedisIdempotencyService.cs - Zero Duplicate Orders"**

**File:** `src/OrderCatalogService.Api/Infrastructure/Outbox/OutboxProcessor.cs`
- Copy from artifact: **"OutboxProcessor.cs - Background Service"**

### 4.4 Features (Vertical Slices)

**File:** `src/OrderCatalogService.Api/Features/Orders/CreateOrder/CreateOrderCommand.cs`
- Copy from artifact: **"CreateOrderCommand.cs - CQRS Write Handler"**

**File:** `src/OrderCatalogService.Api/Features/Orders/GetOrder/GetOrderQuery.cs`
- Copy from artifact: **"GetOrderQuery.cs - Read Single Order"**

**File:** `src/OrderCatalogService.Api/Features/Orders/GetOrders/GetOrdersQuery.cs`
- Copy from artifact: **"GetOrdersQuery.cs - List Orders"**

**File:** `src/OrderCatalogService.Api/Features/Orders/CancelOrder/CancelOrderCommand.cs`
- Copy from artifact: **"CancelOrderCommand.cs - Cancel Order"**

**File:** `src/OrderCatalogService.Api/Features/Products/ProductQueries.cs`
- Copy from artifact: **"ProductQueries.cs - Product Catalog"**

**File:** `src/OrderCatalogService.Api/Features/Carts/CartFeatures.cs`
- Copy from artifact: **"CartFeatures.cs - Shopping Cart"**

### 4.5 Common (Cross-Cutting)

**File:** `src/OrderCatalogService.Api/Common/Behaviors/MediatRBehaviors.cs`
- Copy from artifact: **"MediatRBehaviors.cs - Pipeline Behaviors"**

**File:** `src/OrderCatalogService.Api/Common/Middleware/GlobalMiddleware.cs`
- Copy from artifact: **"GlobalMiddleware.cs - Exception Handling"**

### 4.6 GraphQL

**File:** `src/OrderCatalogService.Api/GraphQL/Query.cs` and `Mutation.cs`
- Copy from artifact: **"Query.cs and Mutation.cs - Complete GraphQL Schema"**

### 4.7 Configuration Files

**File:** `src/OrderCatalogService.Api/appsettings.json`
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "PostgreSQL": "Host=localhost;Database=OrderCatalogDb;Username=postgres;Password=postgres;Maximum Pool Size=200;Minimum Pool Size=10",
    "Redis": "localhost:6379,abortConnect=false"
  }
}
```

---

## 🐳 Step 5: Docker Setup

### 5.1 Copy Docker Files

**File:** `src/OrderCatalogService.Api/Dockerfile`
- Copy from artifact: **"Dockerfile & docker-compose.yml"**

**File:** `docker-compose.yml` (root directory)
- Copy from artifact: **"Dockerfile & docker-compose.yml"**

### 5.2 Create .dockerignore

**File:** `src/OrderCatalogService.Api/.dockerignore`
```
**/bin/
**/obj/
**/out/
.vs/
.vscode/
*.user
*.suo
```

---

## 🗄️ Step 6: Database Setup

### 6.1 Install EF Core Tools

```bash
dotnet tool install --global dotnet-ef
```

### 6.2 Create Initial Migration

```bash
cd src/OrderCatalogService.Api

dotnet ef migrations add InitialCreate --output-dir Infrastructure/Database/Migrations

dotnet ef database update
```

---

## ▶️ Step 7: Run the Application

### 7.1 Option A: Run Locally (without Docker)

```bash
# Terminal 1: Start dependencies
docker-compose up postgres redis seq

# Terminal 2: Run application
cd src/OrderCatalogService.Api
dotnet run
```

Open browser: https://localhost:5001/swagger

### 7.2 Option B: Run Everything with Docker

```bash
# Build and start all services
docker-compose up --build

# View logs
docker-compose logs -f order-catalog-api
```

Open browser:
- API: http://localhost:5000/swagger
- Seq Logs: http://localhost:5341
- Grafana: http://localhost:3000 (admin/admin)
- Jaeger: http://localhost:16686
- PgAdmin: http://localhost:5050

---

## ✅ Step 8: Verify Installation

### 8.1 Health Checks

```bash
# Liveness
curl http://localhost:5000/health/live

# Readiness
curl http://localhost:5000/health/ready

# Metrics
curl http://localhost:5000/metrics
```

### 8.2 Test Endpoints

```bash
# Get Products
curl http://localhost:5000/api/v1/products

# Create Order
curl -X POST http://localhost:5000/api/v1/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: test-123" \
  -d '{
    "customerId": "00000000-0000-0000-0000-000000000001",
    "shippingAddress": {
      "addressLine1": "123 Main St",
      "city": "Seattle",
      "stateOrProvince": "WA",
      "postalCode": "98101",
      "country": "US"
    },
    "lines": [
      {
        "productId": "00000000-0000-0000-0000-000000000001",
        "quantity": 2
      }
    ]
  }'
```

### 8.3 Test GraphQL

Open: http://localhost:5000/graphql

```graphql
query {
  products(first: 10) {
    nodes {
      id
      name
      price
      isAvailable
    }
  }
}
```

---

## 🧪 Step 9: Run Tests

### 9.1 Unit Tests

```bash
cd tests/OrderCatalogService.UnitTests
dotnet test --logger "console;verbosity=detailed"
```

### 9.2 Integration Tests

```bash
cd tests/OrderCatalogService.IntegrationTests
dotnet test --logger "console;verbosity=detailed"
```

### 9.3 Load Tests

```bash
# Install k6
brew install k6  # macOS
choco install k6  # Windows

# Run load test
k6 run tests/load/black-friday-test.js
```

---

## 🎯 Step 10: Production Deployment

### 10.1 Azure Setup

```bash
# Login to Azure
az login

# Create resource group
az group create --name order-catalog-prod-rg --location eastus

# Deploy infrastructure
az deployment group create \
  --resource-group order-catalog-prod-rg \
  --template-file infrastructure/main.bicep \
  --parameters environment=prod
```

### 10.2 Build & Push Docker Image

```bash
# Build image
docker build -t ordercatalogservice:latest -f src/OrderCatalogService.Api/Dockerfile .

# Tag for ACR
docker tag ordercatalogservice:latest ordercatalogprodacr.azurecr.io/ordercatalogservice:latest

# Push to ACR
az acr login --name ordercatalogprodacr
docker push ordercatalogprodacr.azurecr.io/ordercatalogservice:latest
```

### 10.3 Deploy to AKS

```bash
# Get AKS credentials
az aks get-credentials --resource-group order-catalog-prod-rg --name order-catalog-prod-aks

# Apply Kubernetes manifests
kubectl apply -f k8s/

# Verify deployment
kubectl get pods -n production
kubectl logs -f deployment/order-service -n production
```

---

## 📊 Step 11: Monitor & Observe

### 11.1 View Logs (Seq)

http://localhost:5341

### 11.2 View Traces (Jaeger)

http://localhost:16686

### 11.3 View Metrics (Grafana)

http://localhost:3000
- Username: admin
- Password: admin

### 11.4 View Application Insights (Production)

https://portal.azure.com → Application Insights

---

## 🔧 Troubleshooting

### Issue: Database Connection Failed

```bash
# Check if PostgreSQL is running
docker ps | grep postgres

# Check connection string
cat src/OrderCatalogService.Api/appsettings.json | grep PostgreSQL

# Test connection
docker exec -it order-catalog-postgres psql -U postgres -d OrderCatalogDb
```

### Issue: Redis Connection Failed

```bash
# Check if Redis is running
docker ps | grep redis

# Test connection
docker exec -it order-catalog-redis redis-cli ping
```

### Issue: Migrations Not Applied

```bash
# Drop and recreate database
dotnet ef database drop -f
dotnet ef database update
```

### Issue: Package Restore Failed

```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore packages
dotnet restore
```

---

## 🎉 Success!

You now have a fully functional, production-ready microservice running! 

### Next Steps:

1. ✅ Explore API documentation at `/swagger`
2. ✅ Test GraphQL playground at `/graphql`
3. ✅ View structured logs in Seq
4. ✅ Monitor traces in Jaeger
5. ✅ Run load tests with k6
6. ✅ Deploy to Azure for production

---

## 📚 Additional Resources

- **API Documentation**: http://localhost:5000/swagger
- **GraphQL Playground**: http://localhost:5000/graphql
- **Architecture Docs**: `ARCHITECTURE.md`
- **Load Test Results**: Run `k6 run tests/load/black-friday-test.js`

---

**Built with ❤️ for Black Friday Scale**