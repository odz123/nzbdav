# Build the project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN dotnet restore
RUN dotnet publish -c Release -r linux-x64 -o ./publish

# Build the production image
# Using Debian-based image for better HTTP/3 (QUIC) support via libmsquic
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config

# Install required dependencies including libmsquic for HTTP/3 support
# libmsquic provides QUIC protocol implementation required by HTTP/3
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ca-certificates \
    libmsquic \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]