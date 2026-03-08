FROM node:20-bookworm-slim AS webassets
WORKDIR /src/src/OmniRelay.Backend

COPY src/OmniRelay.Backend/package.json ./
COPY src/OmniRelay.Backend/package-lock.json ./
COPY src/OmniRelay.Backend/tailwind.config.js ./
COPY src/OmniRelay.Backend/postcss.config.js ./
COPY src/OmniRelay.Backend/Assets ./Assets
COPY src/OmniRelay.Backend/Pages ./Pages
COPY src/OmniRelay.Backend/wwwroot/js ./wwwroot/js

RUN npm ci
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:8.0.408 AS build
WORKDIR /src

COPY OmniRelay.sln ./
COPY NuGet.config ./
COPY src/OmniRelay.Backend/OmniRelay.Backend.csproj src/OmniRelay.Backend/
COPY src/OmniRelay.Backend.Contracts/OmniRelay.Backend.Contracts.csproj src/OmniRelay.Backend.Contracts/

RUN dotnet restore src/OmniRelay.Backend/OmniRelay.Backend.csproj

COPY . .
COPY --from=webassets /src/src/OmniRelay.Backend/wwwroot/css ./src/OmniRelay.Backend/wwwroot/css
RUN dotnet publish src/OmniRelay.Backend/OmniRelay.Backend.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0.15 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "OmniRelay.Backend.dll"]
