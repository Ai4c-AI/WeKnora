FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY WeKnora.OntologyReasoner.Core/WeKnora.OntologyReasoner.Core.csproj WeKnora.OntologyReasoner.Core/
COPY WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj WeKnora.OntologyReasoner.Api/
RUN dotnet restore WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj

COPY . .
RUN dotnet publish WeKnora.OntologyReasoner.Api/WeKnora.OntologyReasoner.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8090
EXPOSE 8090

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8090/health || exit 1

ENTRYPOINT ["dotnet", "WeKnora.OntologyReasoner.Api.dll"]
