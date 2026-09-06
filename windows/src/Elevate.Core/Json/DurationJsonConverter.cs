using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Core;

/// <summary>
/// Encodes a <see cref="TimeSpan"/> the way Swift's <c>Duration</c> encodes: a two-element array
/// of [secondsComponent, attosecondsComponent], so one state file round-trips between platforms.
/// </summary>
public sealed class DurationJsonConverter : JsonConverter<TimeSpan>
{
    private const long AttosecondsPerSecond = 1_000_000_000_000_000_000;
    private const long AttosecondsPerTick = 100_000_000_000; // 1 tick = 100 ns

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a [seconds, attoseconds] array for a duration.");
        }

        var seconds = ReadInt64(ref reader);
        var attoseconds = ReadInt64(ref reader);

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A duration array must hold exactly two numbers.");
        }

        if (attoseconds <= -AttosecondsPerSecond || attoseconds >= AttosecondsPerSecond)
        {
            throw new JsonException("The attoseconds component of a duration is out of range.");
        }

        return TimeSpan.FromTicks(checked((seconds * TimeSpan.TicksPerSecond) + (attoseconds / AttosecondsPerTick)));
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        // Floor division so the attoseconds component is never negative, as Swift normalises it.
        var ticks = value.Ticks;
        var seconds = (long)Math.Floor((double)ticks / TimeSpan.TicksPerSecond);
        var remainderTicks = ticks - (seconds * TimeSpan.TicksPerSecond);

        writer.WriteStartArray();
        writer.WriteNumberValue(seconds);
        writer.WriteNumberValue(remainderTicks * AttosecondsPerTick);
        writer.WriteEndArray();
    }

    private static long ReadInt64(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("A duration array must hold exactly two numbers.");
        }

        return reader.GetInt64();
    }
}
