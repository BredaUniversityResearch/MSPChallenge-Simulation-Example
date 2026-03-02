@echo off
set cwd=%cd%
dotnet publish -c Release -r linux-x64 -f net8.0 --self-contained
cd bin\Release\net8.0\linux-x64\publish
docker build -t se_sim_image .
RunDockerContainer.bat
cd "%cwd%"
pause
