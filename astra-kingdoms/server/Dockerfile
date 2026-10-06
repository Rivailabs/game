# Astra Kingdoms online match service (ticket 56).
# Build context is the astra-kingdoms folder (the service compiles the shared rules assembly and
# the client protocol assembly from source):
#   docker build -f server/Dockerfile -t astra-match:<commit> .
#   docker run -p 8080:8080 -v astra-data:/data \
#     -e AstraServer__Auth__FirebaseProjectId=<project> astra-match:<commit>

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/AstraKingdoms.Rules/ src/AstraKingdoms.Rules/
COPY tools/UnityCompileCheck/UnityCheck.props tools/UnityCompileCheck/
COPY tools/UnityCompileCheck/OnlineProtocol/ tools/UnityCompileCheck/OnlineProtocol/
COPY unity/Assets/Scripts/Online/Protocol/ unity/Assets/Scripts/Online/Protocol/
COPY server/AstraKingdoms.Server/ server/AstraKingdoms.Server/
RUN dotnet publish server/AstraKingdoms.Server/AstraKingdoms.Server.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Non-root user; the database lives on a mounted volume (it holds seeds and choices: restrict access).
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    AstraServer__Storage__SqlitePath=/data/astra-server.db \
    AstraServer__Auth__Mode=Firebase
EXPOSE 8080
# Probes are HTTP and belong to the orchestrator (the runtime image has no curl):
#   liveness  GET /healthz  (200 while the process serves requests)
#   readiness GET /readyz   (503 while draining or when storage is unavailable)
# SIGTERM starts the graceful drain (suspend or void active matches per Lifecycle:ShutdownPolicy).
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "AstraKingdoms.Server.dll"]
