#!/bin/bash

# Build script for Headend Streamer

set -e

echo "=== Building Headend Streamer ==="

# Clean previous builds
rm -rf publish

# Restore packages
echo "Restoring packages..."
dotnet restore

# Build
echo "Building application..."
dotnet build --configuration Release

# Publish
echo "Publishing application..."
dotnet publish --configuration Release --output publish --runtime linux-x64 --self-contained false

echo "=== Build Complete ==="
echo "Output files are in the 'publish' directory"
echo "To deploy, run: sudo ./deploy.sh"