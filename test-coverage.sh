#!/bin/bash
dotnet test --collect:"XPlat Code Coverage"
covxml=$(ls -t $(find CsSsg.Test{,.{HtmlApi,JsonApi}}/TestResults/ -name coverage.cobertura.xml) | head -3 | tr \\n \;)
reportgenerator -reports:$covxml -targetdir:coveragereport -reporttypes:Html -filefilters:'-*/obj/*'
xdg-open coveragereport/index.html
