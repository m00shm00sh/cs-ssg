# CsSsg, a C# (formerly-)static site generator

## Requirements:
- .NET 10
- PostgreSQL

## Usage:
- Apply migration(s) in CsSsg.Src/Sql/Postgres/*
- `$ ASPNETCORE_CONTENTROOT=$(pwd) dotnet run --project CsSsg.Src --launch-profile http`
- Browse 127.0.0.1:8888 or use the REST+JSON API (you can reverse engineer it from the ConsoleLoader Client and PostsWorker)

## TODO:
- implement non-static functionality for static content (media, styling, etc)
- document the rest+json api

## Projects:
- CsSsg.Src: app code
- CsSsg.Test: unit(ish) tests
- CsSsg.ConsoleLoader: a console loader