FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY PlayCards.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish PlayCards.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish ./
ENTRYPOINT ["sh", "-c", "dotnet PlayCards.dll --urls http://0.0.0.0:${PORT:-8080}"]
