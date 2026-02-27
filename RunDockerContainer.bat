docker run --privileged --name SE_Sim -p 5026:5000 -v /var/run/docker.sock:/var/run/docker.sock se_sim_image
docker exec SE_Sim sh 
apt-get update && apt-get install -y curl
curl -fsSL https://get.docker.com -o install-docker.sh
sh install-docker.sh