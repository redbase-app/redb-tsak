FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 9090

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["TsakHost.csproj", "./"]
RUN dotnet restore "TsakHost.csproj"
COPY . .
RUN dotnet publish "TsakHost.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/Libs /app/Logs
ENTRYPOINT ["dotnet", "TsakHost.dll"]
