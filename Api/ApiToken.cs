using Newtonsoft.Json;

namespace MSPChallenge_Simulation_Example.Api;

public class ApiToken
{
    [JsonProperty("token")]
    public string Token { get; set; }

    [JsonProperty("valid_until")]
    public DateTime ValidUntil { get; set; }
}