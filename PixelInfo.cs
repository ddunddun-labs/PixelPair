using System.Text.Json.Serialization;

namespace PixelPair
{
    public class PixelInfo
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }

        [JsonPropertyName("color")]
        public string Color { get; set; } = "#000000";
    }
}
