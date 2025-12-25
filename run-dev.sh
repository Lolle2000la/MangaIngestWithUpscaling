#!/bin/bash
# Run Server and Worker in development mode

# Trap to kill background processes on exit
trap "trap - SIGTERM && kill -- -$$" SIGINT SIGTERM EXIT

# Start Server
echo "Starting Server (Remote Only)..."
# We use the launch profile to ensure environment variables are set, 
# but we also export the critical one just in case.
export Ingest_Upscaler__RemoteOnly=true
dotnet run --project src/MangaIngestWithUpscaling --launch-profile "https (remote only)" &

# Wait for server to initialize (adjust as needed)
echo "Waiting 10 seconds for server to initialize..."
sleep 10

# Start Worker
echo "Starting Remote Worker..."
dotnet run --project src/MangaIngestWithUpscaling.RemoteWorker --launch-profile "https" &

# Wait for both
wait
