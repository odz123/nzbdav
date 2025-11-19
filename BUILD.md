# Building NzbDav Docker Application

This document provides comprehensive instructions for building and deploying the NzbDav application using Docker.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Building the Docker Image](#building-the-docker-image)
- [Running the Application](#running-the-application)
- [Configuration](#configuration)
- [Development](#development)
- [Troubleshooting](#troubleshooting)

## Prerequisites

Before building the Docker application, ensure you have the following installed:

- **Docker** (version 20.10 or later)
- **Docker Compose** (version 2.0 or later)
- **Git** (for version tagging, optional)

To verify your installation:

```bash
docker --version
docker-compose --version
```

## Quick Start

The fastest way to get NzbDav running:

```bash
# Using docker-compose
docker-compose up -d

# Or using Make
make run
```

This will build the image (if not already built) and start the application on port 3000.

## Building the Docker Image

### Method 1: Using docker-compose

```bash
docker-compose build
```

### Method 2: Using the build script

```bash
./build.sh
```

### Method 3: Using Make

```bash
make build
```

### Method 4: Manual build

```bash
docker build --build-arg NZBDAV_VERSION=dev -t nzbdav:latest .
```

### Build Options

You can customize the build with environment variables:

```bash
# Set a specific version
NZBDAV_VERSION=1.0.0 make build

# Build without cache
make build-no-cache

# Build for multiple platforms (requires buildx)
docker buildx build --platform linux/amd64,linux/arm64 -t nzbdav:latest .
```

## Running the Application

### Using Docker Compose (Recommended)

```bash
# Start in detached mode
docker-compose up -d

# Start in foreground (see logs)
docker-compose up

# Stop the application
docker-compose down
```

### Using Docker Run

```bash
# Create config directory
mkdir -p ./config

# Run the container
docker run --rm -it \
  -p 3000:3000 \
  -v $(pwd)/config:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  nzbdav:latest
```

### Using Make

```bash
# Start in background
make run

# Start in foreground
make run-attached

# View logs
make logs

# Stop
make stop

# Restart
make restart
```

## Configuration

### Environment Variables

The following environment variables can be configured:

| Variable | Default | Description |
|----------|---------|-------------|
| `PUID` | 1000 | User ID for file permissions |
| `PGID` | 1000 | Group ID for file permissions |
| `NODE_ENV` | production | Node.js environment |
| `LOG_LEVEL` | info | Logging level (debug, info, warning, error) |
| `BACKEND_URL` | http://localhost:8080 | Internal backend URL |
| `MAX_BACKEND_HEALTH_RETRIES` | 30 | Backend health check retries |
| `MAX_BACKEND_HEALTH_RETRY_DELAY` | 1 | Delay between retries (seconds) |

### Volumes

- `/config` - Persistent configuration and data storage

### Ports

- `3000` - Web UI and API

## Architecture

The Docker image uses a multi-stage build process:

1. **Stage 1: Frontend Build**
   - Uses `node:alpine` as base
   - Installs frontend dependencies
   - Builds React application
   - Builds Node.js server
   - Prunes dev dependencies

2. **Stage 2: Backend Build**
   - Uses `.NET SDK 9.0` as base
   - Restores NuGet packages
   - Publishes .NET application
   - Targets linux-musl for Alpine compatibility

3. **Stage 3: Runtime**
   - Uses `.NET aspnet 9.0-alpine` as base
   - Installs Node.js for frontend
   - Copies built artifacts from both stages
   - Sets up entrypoint script
   - Configures user permissions

## Development

### Building for Development

```bash
# Build with dev tag
NZBDAV_VERSION=dev make build

# Run with live logs
make run-attached
```

### Accessing the Container

```bash
# Open a shell in the running container
make shell

# Or using docker-compose
docker-compose exec nzbdav /bin/bash
```

### Viewing Logs

```bash
# Follow logs
make logs

# Or using docker-compose
docker-compose logs -f
```

### Cleaning Up

```bash
# Remove containers and volumes
make clean

# Remove all unused Docker resources
make prune
```

## Image Size Optimization

The `.dockerignore` file excludes unnecessary files from the build context:

- Git metadata
- Documentation files
- Node modules (reinstalled in container)
- Build artifacts
- IDE files

This significantly reduces build time and image size.

## Multi-Architecture Support

To build for multiple architectures:

```bash
# Enable buildx (if not already enabled)
docker buildx create --use

# Build for multiple platforms
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --build-arg NZBDAV_VERSION=latest \
  -t nzbdav:latest \
  --push \
  .
```

## Troubleshooting

### Build Failures

**Problem**: Frontend build fails
```bash
# Clear npm cache and rebuild
docker-compose build --no-cache
```

**Problem**: Backend build fails
```bash
# Check .NET SDK version
docker run --rm mcr.microsoft.com/dotnet/sdk:9.0 dotnet --version
```

### Runtime Issues

**Problem**: Container exits immediately
```bash
# Check logs
docker-compose logs

# Or for manual runs
docker logs <container-id>
```

**Problem**: Permission errors in /config
```bash
# Fix permissions (adjust PUID/PGID as needed)
sudo chown -R 1000:1000 ./config
```

**Problem**: Backend health check fails
```bash
# Increase retry timeout
docker-compose up -d -e MAX_BACKEND_HEALTH_RETRIES=60
```

### Network Issues

**Problem**: Cannot access on port 3000
```bash
# Check if port is already in use
netstat -tulpn | grep 3000

# Use a different port
docker run -p 8080:3000 nzbdav:latest
```

## Production Deployment

For production deployments, consider:

1. **Use a specific version tag**
   ```bash
   NZBDAV_VERSION=1.0.0 make build
   ```

2. **Set up proper volumes**
   ```yaml
   volumes:
     - /opt/nzbdav/config:/config
   ```

3. **Configure logging**
   ```yaml
   environment:
     - LOG_LEVEL=warning
   ```

4. **Set resource limits**
   ```yaml
   deploy:
     resources:
       limits:
         cpus: '2'
         memory: 2G
   ```

5. **Enable health checks**
   ```yaml
   healthcheck:
     test: ["CMD", "curl", "-f", "http://localhost:3000/"]
     interval: 30s
     timeout: 10s
     retries: 3
   ```

## Additional Resources

- [NzbDav README](README.md)
- [Docker Documentation](https://docs.docker.com/)
- [Docker Compose Documentation](https://docs.docker.com/compose/)

## Support

For issues and questions:
- GitHub Issues: [nzbdav/issues](https://github.com/nzbdav-dev/nzbdav/issues)
- Discussions: Check the project's discussion board
