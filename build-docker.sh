#!/bin/bash
set -e

# Script to build Docker image locally with proper versioning
# Usage: ./build-docker.sh [tag-name]

# Determine version
if [ -n "$1" ]; then
    # Use provided tag
    VERSION="$1"
elif git rev-parse --git-dir > /dev/null 2>&1; then
    # Try to use git describe, fallback to branch-commit
    if git describe --tags --exact-match > /dev/null 2>&1; then
        VERSION=$(git describe --tags --exact-match)
    else
        BRANCH=$(git rev-parse --abbrev-ref HEAD)
        COMMIT=$(git rev-parse --short HEAD)
        VERSION="${BRANCH}-${COMMIT}"
    fi
else
    # No git, use generic version
    VERSION="local-build"
fi

# Generate build timestamp in ISO 8601 format (UTC)
BUILD_TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Default image name
IMAGE_NAME="${IMAGE_NAME:-nzbdav/nzbdav}"
TAG="${TAG:-${VERSION}}"

echo "Building Docker image..."
echo "  Image: ${IMAGE_NAME}:${TAG}"
echo "  Version: ${VERSION}"
echo "  Build Timestamp: ${BUILD_TIMESTAMP}"
echo ""

# Build the image
docker build \
    --build-arg NZBDAV_VERSION="${VERSION}" \
    --build-arg NZBDAV_BUILD_TIMESTAMP="${BUILD_TIMESTAMP}" \
    -t "${IMAGE_NAME}:${TAG}" \
    .

echo ""
echo "Build complete!"
echo "  Run with: docker run -p 3000:3000 ${IMAGE_NAME}:${TAG}"
echo ""
echo "To also tag as 'latest':"
echo "  docker tag ${IMAGE_NAME}:${TAG} ${IMAGE_NAME}:latest"
