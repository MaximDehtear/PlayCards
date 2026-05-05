FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY PlayCards.csproj ./
RUN dotnet restore PlayCards.csproj

COPY Program.cs ./
COPY App.razor ./
COPY _Imports.razor ./
COPY appsettings.json ./
COPY Hubs ./Hubs
COPY Models ./Models
COPY Services ./Services
COPY Pages ./Pages
COPY Shared ./Shared
COPY wwwroot ./wwwroot

RUN dotnet publish PlayCards.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish ./
ENTRYPOINT ["sh", "-c", "dotnet PlayCards.dll --urls http://0.0.0.0:${PORT:-8080}"]
