# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy NuGet config first (layer cache friendly)
COPY NuGet.config ./

# Copy only the server project (no Dynamo extension deps needed)
COPY src/DynamoCopilot.Server/ src/DynamoCopilot.Server/

RUN dotnet restore src/DynamoCopilot.Server/DynamoCopilot.Server.csproj

RUN dotnet publish src/DynamoCopilot.Server/DynamoCopilot.Server.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# PORT is injected at runtime by Railway — do not hardcode it here.
# Program.cs reads $PORT and calls UseUrls at startup.
EXPOSE 8080

ENTRYPOINT ["dotnet", "DynamoCopilot.Server.dll"]
