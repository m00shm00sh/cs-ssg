#!/bin/bash
dotnet test --collect:"XPlat Code Coverage"
covxml=$(ls -t $(find CsSsg.Test/TestResults/ -name coverage.cobertura.xml) | head -1)
reportgenerator -reports:$covxml -targetdir:coveragereport -reporttypes:Html
xdg-open coveragereport/index.html
