docker pull henriqueguarneri/benthic-impact-assessment:latest
docker volume create data
docker volume create cache
echo "Downloading model data. This will take 10~15 minutes without visible progress until complete."
docker run --rm --mount type=volume,src=data,dst=/app/data henriqueguarneri/benthic-impact-assessment python scripts/sync_data.py download
pause