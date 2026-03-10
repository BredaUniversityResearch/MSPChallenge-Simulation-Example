#!/bin/bash

set cwd=pwd
dotnet publish -c Release -r linux-x64 -f net8.0 --self-contained
cd ./bin/Release/net8.0/linux-x64/publish
docker build -t docker-hub.mspchallenge.info/cradlewebmaster/msp-challenge-sand-extraction-sim:1.0.0 -t docker-hub.mspchallenge.info/cradlewebmaster/msp-challenge-sand-extraction-sim:latest .
./RunDockerContainer.sh
cd "$cwd"
