# Fake MES as a Linux container, next to the gateway (the gateway's docker-compose.yml
# starts it as "fake-mes"). Built from the gateway folder, so the default
# Config/mes-schema.json can go in as the fallback MES format.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY tools/MES_EMULATOR/MES_EMULATOR.csproj .
RUN dotnet restore MES_EMULATOR.csproj
COPY tools/MES_EMULATOR/Program.cs .
RUN dotnet publish MES_EMULATOR.csproj -c Release -o /app --no-restore
COPY Config/mes-schema.json /app/mes-schema.json

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENV TZ=Asia/Bangkok
# /mes: received.jsonl (what the fake MES stored), kept on a volume.
RUN mkdir -p /mes && chown "$APP_UID" /mes
VOLUME /mes
# 5000: envelopes from the gateway. 5001: control (/stats, /mode, /reset).
EXPOSE 5000 5001
USER $APP_UID
# --schema: the gateway's live MES format (its data folder, mounted read-only by compose).
ENTRYPOINT ["dotnet", "MES_EMULATOR.dll", "--all-ips", "--data", "/mes", "--schema", "/gateway-data/Config/mes-schema.json"]
