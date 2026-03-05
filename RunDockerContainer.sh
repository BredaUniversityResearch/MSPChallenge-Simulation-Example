#!/bin/bash

if [ ! -e filename ]; then
  touch .env.local
fi
MSYS_NO_PATHCONV=1 docker run --name SE_Sim -p 5026:5026 -v "$PWD/.env.local:/app/.env.local" -v /var/run/docker.sock:/var/run/docker.sock se_sim_image
