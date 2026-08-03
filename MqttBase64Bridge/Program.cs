using System.Text;
using System.Globalization;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

var brokerHost = GetEnvironment("MQTT_BROKER_HOST", "localhost");
var brokerPort = GetEnvironmentInt("MQTT_BROKER_PORT", 31883);
var useTls = GetEnvironmentBool("MQTT_USE_TLS", false);
var inputTopic = GetEnvironment("MQTT_INPUT_TOPIC", "images/jpeg");
var controlTopic = GetEnvironment("MQTT_CONTROL_TOPIC", "sensors/displacement");
var outputTopic = GetEnvironment("MQTT_OUTPUT_TOPIC", "images/jpeg/base64");
var imageCooldownSeconds = GetEnvironmentInt("MQTT_IMAGE_COOLDOWN_SECONDS", 30);
var peakToPeakDisplacementThreshold = GetEnvironmentDecimal("MQTT_PEAK_TO_PEAK_DISPLACEMENT_THRESHOLD", 5.0m);
var clientId = GetEnvironment("MQTT_CLIENT_ID", $"jpg-base64-bridge-{Guid.NewGuid():N}");
var stateGate = new object();
byte[]? lastImagePayload = null;
DateTimeOffset? lastImagePublishUtc = null;

var factory = new MqttFactory();
using var client = factory.CreateMqttClient();

var clientOptionsBuilder = new MqttClientOptionsBuilder()
    .WithClientId(clientId)
    .WithTcpServer(brokerHost, brokerPort)
    .WithCleanSession();

if (useTls)
{
    clientOptionsBuilder.WithTlsOptions(new MqttClientTlsOptionsBuilder().Build());
}

var clientOptions = clientOptionsBuilder.Build();

client.ApplicationMessageReceivedAsync += async args =>
{
    var topic = args.ApplicationMessage.Topic;
    var payloadBytes = args.ApplicationMessage.PayloadSegment.ToArray();

    if (topic == controlTopic)
    {
        Console.WriteLine($"Received control message from '{controlTopic}' with {payloadBytes.Length} bytes.");

        // Attempt to extract the Peak-to-Peak_Displacement value from the control message payload

        if (TryGetPeakToPeakDisplacement(payloadBytes, out var peakToPeakDisplacement))
        {
            Console.WriteLine($"Updated displacement gate from '{controlTopic}' to {peakToPeakDisplacement:0.###}.");

            // If the displacement is below the threshold, reset the last image publish time to allow immediate publishing when the displacement exceeds the threshold again.

            if (peakToPeakDisplacement < peakToPeakDisplacementThreshold)
            {
                lock (stateGate)
                {
                    lastImagePublishUtc = null;
                }

                return;
            }

            // If the displacement exceeds the threshold, attempt to publish the last cached image if it exists and if the cooldown period has elapsed.

            Console.WriteLine($"Displacement {peakToPeakDisplacement:0.###} exceeds threshold {peakToPeakDisplacementThreshold:0.###}; attempting to publish cached image from '{inputTopic}' to '{outputTopic}'.");

            byte[]? imagePayload;
            DateTimeOffset? previousPublishUtc;

            lock (stateGate)
            {
                imagePayload = lastImagePayload?.ToArray();
                previousPublishUtc = lastImagePublishUtc;
            }

            if (imagePayload is null || imagePayload.Length == 0)
            {
                Console.WriteLine($"No cached image available to publish after control message from '{controlTopic}'.");
                return;
            }

            Console.WriteLine($"Last image publish was at {previousPublishUtc?.ToString("o") ?? "never"}; checking cooldown period of {imageCooldownSeconds} seconds.");

            if (previousPublishUtc.HasValue)
            {
                var elapsedSinceLastPublish = DateTimeOffset.UtcNow - previousPublishUtc.Value;
                if (elapsedSinceLastPublish < TimeSpan.FromSeconds(imageCooldownSeconds))
                {
                    Console.WriteLine($"Skipped publishing cached image because only {elapsedSinceLastPublish.TotalSeconds:0.###} seconds elapsed since the last publish; waiting {imageCooldownSeconds} seconds.");
                    return;
                }
            }

            Console.WriteLine($"Publishing cached image from '{inputTopic}' to '{outputTopic}' because control displacement was {peakToPeakDisplacement:0.###} and cooldown period has elapsed.");

            var payloadBase64 = Convert.ToBase64String(imagePayload);

            var outbound = new
            {
                sourceTopic = inputTopic,
                controlTopic,
                contentType = "image/jpeg",
                payloadLength = imagePayload.Length,
                payloadBase64,
                receivedAtUtc = DateTimeOffset.UtcNow
            };

            var outboundJson = JsonSerializer.Serialize(outbound, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            Console.WriteLine($"Publishing {outboundJson.Length} bytes of JSON to '{outputTopic}'.");

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(outputTopic)
                .WithPayload(Encoding.UTF8.GetBytes(outboundJson))
                .WithContentType("application/json")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await client.PublishAsync(message);

            lock (stateGate)
            {
                lastImagePublishUtc = DateTimeOffset.UtcNow;
            }

            Console.WriteLine($"Published cached image from '{inputTopic}' to '{outputTopic}' because control displacement was {peakToPeakDisplacement:0.###}.");
        }
        else
        {
            Console.WriteLine($"Ignored control message from '{controlTopic}' because Peak-to-Peak_Displacement was not found.");
        }

        return;
    }

    if (topic != inputTopic)
    {
        Console.WriteLine($"Ignored message from '{topic}' because it does not match the input topic '{inputTopic}'.");
        return;
    }

    lock (stateGate)
    {
        lastImagePayload = payloadBytes.ToArray();
    }

    Console.WriteLine($"Cached {payloadBytes.Length} bytes from '{inputTopic}' for the next control trigger.");
};

client.ConnectedAsync += async _ =>
{
    Console.WriteLine($"Connected to {brokerHost}:{brokerPort}.");
    await client.SubscribeAsync(inputTopic, MqttQualityOfServiceLevel.AtLeastOnce);
    Console.WriteLine($"Subscribed to '{inputTopic}'.");
    await client.SubscribeAsync(controlTopic, MqttQualityOfServiceLevel.AtLeastOnce);
    Console.WriteLine($"Subscribed to '{controlTopic}'.");
};

client.DisconnectedAsync += async _ =>
{
    Console.WriteLine("Disconnected from broker. Reconnecting in 5 seconds...");
    await Task.Delay(TimeSpan.FromSeconds(5));
    await client.ConnectAsync(clientOptions);
};

Console.WriteLine("Startup configuration:");
Console.WriteLine($"  MQTT_BROKER_HOST={brokerHost}");
Console.WriteLine($"  MQTT_BROKER_PORT={brokerPort}");
Console.WriteLine($"  MQTT_USE_TLS={useTls}");
Console.WriteLine($"  MQTT_INPUT_TOPIC={inputTopic}");
Console.WriteLine($"  MQTT_CONTROL_TOPIC={controlTopic}");
Console.WriteLine($"  MQTT_OUTPUT_TOPIC={outputTopic}");
Console.WriteLine($"  MQTT_IMAGE_COOLDOWN_SECONDS={imageCooldownSeconds}");
Console.WriteLine($"  MQTT_PEAK_TO_PEAK_DISPLACEMENT_THRESHOLD={peakToPeakDisplacementThreshold.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine($"  MQTT_CLIENT_ID={clientId}");

await client.ConnectAsync(clientOptions);
Console.WriteLine("Bridge running. Press Ctrl+C to stop.");

await Task.Delay(Timeout.InfiniteTimeSpan);

static string GetEnvironment(string name, string defaultValue) =>
    Environment.GetEnvironmentVariable(name) ?? defaultValue;

static int GetEnvironmentInt(string name, int defaultValue) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

static bool GetEnvironmentBool(string name, bool defaultValue) =>
    bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

static decimal GetEnvironmentDecimal(string name, decimal defaultValue) =>
    decimal.TryParse(
        Environment.GetEnvironmentVariable(name),
        NumberStyles.Number,
        CultureInfo.InvariantCulture,
        out var value) ? value : defaultValue;

static bool TryGetPeakToPeakDisplacement(byte[] payloadBytes, out decimal? maxDisplacement)
{
    decimal? foundMaximum = null;

    using var document = JsonDocument.Parse(payloadBytes);
    Visit(document.RootElement);

    maxDisplacement = foundMaximum;

    return foundMaximum.HasValue;

    void Visit(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "Peak-to-Peak_Displacement", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var displacement))
                        {
                            foundMaximum = foundMaximum.HasValue ? Math.Max(foundMaximum.Value, displacement) : displacement;
                        }
                    }

                    Visit(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }

                break;
        }
    }
}
