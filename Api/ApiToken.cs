using System.Text.Json.Serialization;

namespace MSPChallenge_Simulation.Api;

public class ApiToken
{
    public string token { get; set; }
    [JsonConverter(typeof(Newtonsoft.Json.Converters.IsoDateTimeConverter))]
    public DateTime valid_until { get; set; }
}