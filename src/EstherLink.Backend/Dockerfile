FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

RUN apt-get update && apt-get install -y nodejs npm && rm -rf /var/lib/apt/lists/*

COPY EstherLink.sln ./
COPY NuGet.config ./
COPY src/EstherLink.Backend/EstherLink.Backend.csproj src/EstherLink.Backend/
COPY src/EstherLink.Backend.Contracts/EstherLink.Backend.Contracts.csproj src/EstherLink.Backend.Contracts/

RUN dotnet restore src/EstherLink.Backend/EstherLink.Backend.csproj

COPY . .
RUN cd src/EstherLink.Backend && npm ci && npm run build:css
RUN dotnet publish src/EstherLink.Backend/EstherLink.Backend.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "EstherLink.Backend.dll"]
