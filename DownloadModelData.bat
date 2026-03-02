docker pull henriqueguarneri/benthic-impact-assessment:latest
mkdir data
echo "Downloading model data. This will take 10~15 minutes without visible progress until complete."
docker run --rm -v ./data:/app/data henriqueguarneri/benthic-impact-assessment python scripts/sync_data.py download
pause