# Use a minimal Debian-based image for the runtime
FROM debian:trixie-slim

# Install necessary dependencies for running .NET
RUN apt-get update && apt-get install -y --no-install-recommends \
		libicu76 \
        bash \
        nano \
		docker-cli \
	;	

# Set the working directory in the container
WORKDIR /app

# Define a build argument for the source directory
ARG source_dir=.

# Copy the published .NET app into the container
COPY ${source_dir} /app

# Set executable permissions for the .NET app (if not already set)
RUN chmod +x /app/MSPChallenge-Simulation

# Define the entrypoint to run the .NET app
#   Dot not use default port 5000, since that will add a host "localhost" restriction
ENTRYPOINT ["sh", "-c", "./MSPChallenge-Simulation --port 5026"]

