# Build from the repository root:
#   docker build -f src/WhipRadio.Orchestrator/Dockerfile -t whip-radio-orchestrator .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/WhipRadio.Orchestrator -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg drives the playout pipeline (encoder push to Icecast + per-item decoders).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "WhipRadio.Orchestrator.dll"]
