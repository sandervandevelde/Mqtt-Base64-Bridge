# MQTT JPEG to Base64 JSON bridge

This is a simple C# container that listens on an MQTT topic for JPEG payloads published as raw byte arrays, converts the bytes to base64, and republishes a JSON document to another MQTT topic.
The broker connection is anonymous by default.
It only forwards JPEG messages when the latest control message on a separate MQTT topic contains any `Peak-to-Peak_Displacement` value greater than a configurable decimal threshold. The default threshold is `5.0`.
After an image is published, the bridge waits a configurable cooldown before publishing another image. If the control displacement drops below the configured threshold, the cooldown is reset.

## Environment variables

- `MQTT_BROKER_HOST` - MQTT broker host, default `localhost`
- `MQTT_BROKER_PORT` - MQTT broker port, default `1883`
- `MQTT_USE_TLS` - `true` to enable TLS
- `MQTT_INPUT_TOPIC` - topic to subscribe to, default `images/jpeg`
- `MQTT_CONTROL_TOPIC` - separate topic for JSON control messages, default `sensors/displacement`
- `MQTT_OUTPUT_TOPIC` - topic to publish JSON to, default `images/jpeg/base64`
- `MQTT_IMAGE_COOLDOWN_SECONDS` - minimum seconds between published images, default `30`
- `MQTT_PEAK_TO_PEAK_DISPLACEMENT_THRESHOLD` - decimal threshold used to trigger publishing, default `5.0`
- `MQTT_CLIENT_ID` - optional client id

## Example output

```json
{
  "sourceTopic": "images/jpeg/in",
  "outputTopic": "images/jpeg/base64",
  "contentType": "image/jpeg",
  "payloadLength": 12345,
  "payloadBase64": "/9j/4AAQSkZJRgABAQAAAQABAAD...",
  "receivedAtUtc": "2026-08-03T00:00:00+00:00"
}
```

## Build and run

```bash
cd MqttBase64Bridge
dotnet run
```

## Docker

```bash
docker build -t mqtt-base64-bridge .
docker run --rm \
  -e MQTT_BROKER_HOST=host.docker.internal \
  -e MQTT_INPUT_TOPIC=images/jpeg/in \
  -e MQTT_CONTROL_TOPIC=sensors/displacement \
  -e MQTT_OUTPUT_TOPIC=images/jpeg/base64 \
  mqtt-base64-bridge
```

Build and Push it with: 

```
docker build -t svelde/mqtt-base64-ridge:0.1.1 .
docker push svelde/mqtt-base64-ridge:0.1.1
```

Note: You need Docker Desktop to build and push it.

If your broker uses TLS, set `MQTT_USE_TLS=true`. No username or password is required for anonymous access.

The JPEG topic and the control topic must be different topics. The control topic should publish JSON like:

```json
{
  "datetime": "2026-08-03T15:33:18Z",
  "Accelerometer": {
    "X-Axis": {
      "Peak-to-Peak_Displacement": 1
    }
  }
}
```

## KubeCtl

```
sudo kubectl delete namespace nsmqttbase64bridge
sudo kubectl apply -f mqtt-base64-bridge.yaml
```