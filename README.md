#  Brief introduction 

[MSP Challenge](https://www.mspchallenge.info/) is a simulation platform designed to support maritime spatial planning.
This platform can roughly be divided into a [server](https://github.com/BredaUniversityResearch/MSPChallenge-Server) and
a [client](https://github.com/BredaUniversityResearch/MSPChallenge-Client) part. The server has many
[components](https://community.mspchallenge.info/wiki/Source_code_documentation), one of which is the watchdog service.
The watchdog service is responsible for managing simulations, the built-in ones are
[Ecology](https://community.mspchallenge.info/wiki/Ecosystem_simulation_(MEL_%26_EwE)),
[Energy](https://community.mspchallenge.info/wiki/Energy_simulation_(CEL)), and
[Shipping](https://community.mspchallenge.info/wiki/Shipping_simulation_(SEL)).

These simulations are part of, and distributed with every release of the MSP Challenge server and platform.
From version [5.0.1](https://github.com/BredaUniversityResearch/MSPChallenge-Server/tags) onwards it will be possible to
extend the number of simulations by external watchdog services, which can be registered to a running server.
After monthly simulations runs, KPI values will be reported to the server, shown to the user on the client.
This repository contains an ***example*** of such an external watchdog service, which "simulates" and reports the amount of
sun hours per country.

# Feedback, feature requests, bug reports and contributions

If you have feedback, feature requests, or bug reports, on the ***example***, please create an issue on the [Github repository](https://github.com/BredaUniversityResearch/MSPChallenge-Simulation-Example/issues).

Of course we are open to contributions, please read the [contribution guidelines](https://community.mspchallenge.info/wiki/Community_Contribution) before you start.

# Getting started

## Pre-requisites
1. Install the MSP Challenge server, version [5.0.1](https://github.com/BredaUniversityResearch/MSPChallenge-Server/tags) or higher, using docker, see the [Installation from scratch](https://community.mspchallenge.info/wiki/Docker_server_installation).
This guide will assume a Linux environment, but the server can also be run [on Windows](https://community.mspchallenge.info/wiki/Installation_Manual#Option_1:_Fresh_installation_through_Docker_(on_Linux_or_Windows)) using Docker Desktop and Git bash.

#  Installation

1. Do **not** clone this repository, but download the latest release from the [releases page](https://github.com/BredaUniversityResearch/MSPChallenge-Simulation-Example/releases) instead.
   You should create a new repository for your own simulation, and copy the contents of the example repository to your own repository.
2. Unzip the downloaded file to a location of your choice.
3. Rename the csproj file to match the name of your repository.
3. Open the csproj file in your favorite IDE. We tested with both JetBrains Rider and Visual Studio.
4. Change the namespace of the project to match the name of your repository.  
1. The watchdog needs to be registered in the [Settings](http://localhost/manager/setting) of the Server Manager web application. For a local development setup, the url would normally be: http://localhost/manager/setting.
   
    todo: explain account registration 
1. On the [Settings](http://localhost/manager/setting) page choose the "More details"-icon of the "Watchdog servers" setting.
1.  
2. Add a new Watchdog server with the following settings:
    - Name: `Example`
    - Watchdog server id:
    - URL: `http://localhost:8080`
    - Token: `example`
    - Active: `true`
    - Description: `Example simulation`
    - Click on the "Save" button.


# Usage

# License

# Contact

# Acknowledgements

# References

# Changelog



# 