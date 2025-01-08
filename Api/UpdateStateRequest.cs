namespace MSPChallenge_Simulation_Example.Api;

public record class UpdateStateRequest(
    string game_session_api,
    string game_session_token,
    string game_state,
    string required_simulations,
    string api_access_token,
    string api_access_renew_token,
    int month
);

