namespace MSPChallenge_Simulation.Api;

public record UpdateStateRequest(
    string game_session_api,
    string game_session_token,
    string game_state,
    string required_simulations,
    string api_access_token,
    string api_access_renew_token,
    int month,
    GameSessionInfo? game_session_info = null
);
