# Edge Gateway as a Linux container. Start it with docker-compose.yml:
#   docker compose up -d --build
# Build stage: the .NET 8 SDK compiles. Final image: only the .NET 8 runtime.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY EdgeGateway.csproj .
RUN dotnet restore EdgeGateway.csproj
COPY . .
RUN dotnet publish EdgeGateway.csproj -c Release -o /app --no-restore
# Defaults for a new data folder, as seen from inside a container: listen on every
# address (Docker maps the host's port to it), MES = the fake MES next to it
# (docker-compose.yml), and the local MQTT broker on this PC is host.docker.internal.
# Set the real MES and broker on the config page.
RUN sed -i -e 's/"listenAddress": *"[^"]*"/"listenAddress": "0.0.0.0"/' \
           -e 's#//localhost:5000/#//fake-mes:5000/#' /app/Config/machine-config.json \
 && sed -i -e 's/"brokerAddress": *"127\.0\.0\.1"/"brokerAddress": "host.docker.internal"/' /app/Config/mqtt-config.json

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# Day files, logs and the MQTT time stamps use local time: Thailand, not the container's UTC.
ENV TZ=Asia/Bangkok
# /data: Config (machine tabs), outbox (unsent rows), logs, state. Keep it on a volume.
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data
# 9000: machines (TCP). 5100: config page.
EXPOSE 9000 5100
USER $APP_UID
# docker stop sends SIGTERM: the gateway stops cleanly (needs up to ~15 s, see compose).
ENTRYPOINT ["dotnet", "EdgeGateway.dll", "--data", "/data", "--admin-all-ips"]
