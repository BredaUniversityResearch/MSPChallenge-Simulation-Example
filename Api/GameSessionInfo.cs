namespace MSPChallenge_Simulation.Api;

public record GameSessionInfo(
    int id,
    string name,
    string region,
    int config_version,
    string config_version_message,
    string config_file_name,
    string config_file_description,
    string config_file_metadata_date_modified,
    string config_file_metadata_model_hash,
    string config_file_metadata_editor_version,
    string config_file_metadata_config_version,
    string server_version
);
