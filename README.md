# Manga Ingest With Upscaling

## Overview

MangaIngestWithUpscaling is a **Blazor-based web application** designed to **ingest, process, and automatically upscale manga images**. 

## Features

- **Manga ingestion**: Ingest Mangas into a library structure useful for [Kavita](https://www.kavitareader.com/) and [Komga](https://komga.org/).
  - Keep track of mangas known under many different titles. This software will not just put them into the same folder, but also change the ComicInfo.xml file to reflect the new title.
  - Change the title of a manga. This may sound simple, but going back and changing the ComicInfo.xml file is quite a hassle.
- **Image upscaling**: Enhance image resolution with [MangaJaNai](https://github.com/the-database/mangajanai) upscaling models. 

## Usage

1. **Set up a library** through the UI.
2. **Ingest manga** by uploading putting the manga into the ingest folder you just configured.
3. Profit!

## Installation

The preferred way to run the application is through Docker. Below is an example docker-compose file to get you started.

```yaml
version: '3.4'

services:
  mangaingestwithupscaling:
    image: ghcr.io/lolle2000la/manga-ingest-with-upscaling:latest
    restart: unless-stopped
    environment:
      TZ: #your timezone here
      Ingest_Upscaler__SelectedDeviceIndex: 0 # if you have multiple GPUs, you can select which one to use
      Ingest_Upscaler__UseFp16: true # if you want to use fp16 instead of fp32, preferred if you have a GPU that supports it
      Ingest_Upscaler__UseCPU: false # if you want to use the CPU instead of the GPU
    volumes:
      - /path/to/store/appdata:/data # for storing the database and logs
      - /path/to/store/models:/models # for storing the upscaling models. 
      # ... other folders you want to be able to access from the container
      - /path/to/ingest:/ingest
      - /path/to/target:/target
    ports:
      - 8080:8080 # the web interface will be available on this port
    # Make sure you have the nvidia-container-toolkit installed on your host.
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]

```
## Building Prerequisites

- .NET 9.0 SDK or later

## Running from Source

1. **Clone the repository:**
   ```sh
   git clone https://github.com/your-repo/MangaIngestWithUpscaling.git
   cd MangaIngestWithUpscaling
   ```
2. **Restore dependencies:**
   ```sh
   dotnet restore
   ```
3. **Build the project:**
   ```sh
   dotnet build
   ```
4. **Run the application:**
   ```sh
   dotnet run
   ```

## Configuration

The application relies on `appsettings.json` for configuration. Modify the connection strings and other parameters as needed.
Alternatively, you can use environment variables to override the configuration values.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=data.db;Pooling=false",
    "LoggingConnection": "Data Source=logs.db;Pooling=false"
  },
  "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.SQLite" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "Enrich": [ "FromLogContext" ]
  },
  "AllowedHosts": "*",
  "Upscaler": {
    "UseFp16": true,
    "UseCPU": false,
    "SelectedDeviceIndex": 0
  }
}

```

## Tech Stack

- **Frontend:** Blazor (MudBlazor components)
- **Backend:** ASP.NET Core
- **Database:** SQLite
- **Logging:** Serilog
- **Reactive Programming:** ReactiveUI

## Contributing

1. Fork the repository.
2. Create a new feature branch.
3. Commit changes and push to the branch.
4. Open a pull request.

## License

This project is licensed under the MIT License.

## Contact

For inquiries, please open an issue or reach out via [your contact info].

