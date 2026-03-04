FROM node:20-bookworm-slim AS webassets
WORKDIR /src/src/EstherLink.Backend

COPY src/EstherLink.Backend/package.json ./
COPY src/EstherLink.Backend/package-lock.json ./
COPY src/EstherLink.Backend/tailwind.config.js ./
COPY src/EstherLink.Backend/postcss.config.js ./
COPY src/EstherLink.Backend/Assets ./Assets
COPY src/EstherLink.Backend/Pages ./Pages
COPY src/EstherLink.Backend/wwwroot/js ./wwwroot/js

RUN npm ci
RUN npm run build:css

FROM mcr.microsoft.com/dotnet/sdk:8.0.408 AS build
WORKDIR /src

COPY EstherLink.sln ./
COPY NuGet.config ./
COPY src/EstherLink.Backend/EstherLink.Backend.csproj src/EstherLink.Backend/
COPY src/EstherLink.Backend.Contracts/EstherLink.Backend.Contracts.csproj src/EstherLink.Backend.Contracts/

RUN dotnet restore src/EstherLink.Backend/EstherLink.Backend.csproj

COPY . .
COPY --from=webassets /src/src/EstherLink.Backend/wwwroot/css ./src/EstherLink.Backend/wwwroot/css
RUN dotnet publish src/EstherLink.Backend/EstherLink.Backend.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0.15 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "EstherLink.Backend.dll"]
