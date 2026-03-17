#!/bin/bash
ASPNETCORE_CONTENTROOT=$(pwd) dotnet run --project CsSsg.Src --launch-profile http
