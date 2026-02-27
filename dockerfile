# Use a minimal Debian-based image for the runtime
FROM debian:11-slim

# Install necessary dependencies for running .NET
RUN apt-get update && apt-get install -y --no-install-recommends \
		libicu67 \
		git \
        libgdiplus \
        bash \
        procps \
        nano \
        ca-certificates \
	;	

# Install docker client so MCP server can start the required container
#ENV DOCKERVERSION=27.4.0
#RUN curl -fsSLO https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKERVERSION}.tgz \
  #&& tar xzvf docker-${DOCKERVERSION}.tgz --strip 1 -C /usr/local/bin docker/docker \
  #&& rm docker-${DOCKERVERSION}.tgz

# Set the working directory in the container
WORKDIR /app

# Define a build argument for the source directory
ARG source_dir=.

# Copy the published .NET app into the container
COPY ${source_dir} /app

# Set executable permissions for the .NET app (if not already set)
RUN chmod +x /app/MSPChallenge-Simulation

# Define the entrypoint to run the .NET app
ENTRYPOINT ["sh", "-c", "./MSPChallenge-Simulation"]


