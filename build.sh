#!/bin/bash

# NzbDav Docker Build Script
# This script builds the Docker image with proper versioning

set -e

# Get version from git or use "dev"
VERSION=${NZBDAV_VERSION:-dev}
if [ "$VERSION" = "dev" ] && [ -d .git ]; then
    GIT_HASH=$(git rev-parse --short HEAD 2>/dev/null || echo "dev")
    VERSION="dev-${GIT_HASH}"
fi

echo "Building NzbDav Docker image..."
echo "Version: $VERSION"

# Build the image
docker build \
    --build-arg NZBDAV_VERSION="$VERSION" \
    --platform linux/amd64,linux/arm64 \
    -t nzbdav:latest \
    -t nzbdav:"$VERSION" \
    .

echo ""
echo "Build complete!"
echo "Image tags:"
echo "  - nzbdav:latest"
echo "  - nzbdav:$VERSION"
echo ""
echo "To run the container:"
echo "  docker-compose up -d"
echo ""
echo "Or manually:"
echo "  docker run --rm -it -p 3000:3000 -v \$(pwd)/config:/config nzbdav:latest"
