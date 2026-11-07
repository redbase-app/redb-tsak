# Build context: repository root (parent of redb.Tsak, redb.Core, redb.Route, etc.)
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base
WORKDIR /app
EXPOSE 9090

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy csproj files for restore
COPY redb.Core/redb.Core.csproj redb.Core/
COPY redb.Core.Pro/redb.Core.Pro.csproj redb.Core.Pro/
COPY redb.Postgres/redb.Postgres.csproj redb.Postgres/
COPY redb.Postgres.Pro/redb.Postgres.Pro.csproj redb.Postgres.Pro/
COPY redb.MSSql/redb.MSSql.csproj redb.MSSql/
COPY redb.MSSql.Pro/redb.MSSql.Pro.csproj redb.MSSql.Pro/
COPY redb.Route/src/redb.Route.Core/redb.Route.Core.csproj redb.Route/src/redb.Route.Core/
COPY redb.Route/src/redb.Route.Http/redb.Route.Http.csproj redb.Route/src/redb.Route.Http/
COPY redb.Route/src/redb.Route.Quartz/redb.Route.Quartz.csproj redb.Route/src/redb.Route.Quartz/
COPY redb.Tsak/src/redb.Tsak.Core/redb.Tsak.Core.csproj redb.Tsak/src/redb.Tsak.Core/
COPY redb.Tsak/src/redb.Tsak.Worker/redb.Tsak.Worker.csproj redb.Tsak/src/redb.Tsak.Worker/
COPY redb.Tsak/Directory.Build.props redb.Tsak/

RUN dotnet restore redb.Tsak/src/redb.Tsak.Worker/redb.Tsak.Worker.csproj

# Copy everything and build
COPY redb.Core/ redb.Core/
COPY redb.Core.Pro/ redb.Core.Pro/
COPY redb.Postgres/ redb.Postgres/
COPY redb.Postgres.Pro/ redb.Postgres.Pro/
COPY redb.MSSql/ redb.MSSql/
COPY redb.MSSql.Pro/ redb.MSSql.Pro/
COPY redb.Route/src/ redb.Route/src/
COPY redb.Tsak/src/ redb.Tsak/src/
COPY redb.Tsak/Directory.Build.props redb.Tsak/

WORKDIR /src/redb.Tsak/src/redb.Tsak.Worker
RUN dotnet build -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "redb.Tsak.Worker.dll"]
