using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elevate.Core;

/// <summary>
/// Encodes a <see cref="TimeSpan"/> the way Swift's stdlib <c>Duration: Codable</c> encodes: the raw
/// 128-bit attosecond value split into its high 64 bits (signed) and low 64 bits (unsigned) — the
/// synthesised <c>[_high, _low]</c> pair — so one state file round-trips between platforms.
/// Eight hours, for example, is <c>[1561, 4632500939389927424]</c>.
/// </summary>
public sealed class DurationJsonConverter : JsonConverter<TimeSpan>
{
    private const long AttosecondsPerTick = 100_000_000_000; // 1 tick = 100 ns

    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a [high, low] array for a duration.");
        }

        var high = ReadHigh(ref reader);
        var low = ReadLow(ref reader);

        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("A duration array must hold exactly two numbers.");
        }

        var attoseconds = ((Int128)high << 64) | (Int128)low;

        // Floor division, so a negative duration truncates the same way it is written.
        var ticks = attoseconds / AttosecondsPerTick;
        if (attoseconds % AttosecondsPerTick < 0)
        {
            ticks -= 1;
        }

        if (ticks < long.MinValue || ticks > long.MaxValue)
        {
            throw new JsonException("The duration is out of range for a TimeSpan.");
        }

        return TimeSpan.FromTicks((long)ticks);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        var attoseconds = (Int128)value.Ticks * AttosecondsPerTick;

        writer.WriteStartArray();
        writer.WriteNumberValue((long)(attoseconds >> 64));
        writer.WriteNumberValue((ulong)(UInt128)attoseconds);
        writer.WriteEndArray();
    }

    private static long ReadHigh(ref Utf8JsonReader reader)
    {
        RequireNumber(ref reader);
        if (!reader.TryGetInt64(out var high))
        {
            throw new JsonException("The high component of a duration must be a 64-bit signed integer.");
        }

        return high;
    }

    private static ulong ReadLow(ref Utf8JsonReader reader)
    {
        RequireNumber(ref reader);
        if (!reader.TryGetUInt64(out var low))
        {
            throw new JsonException("The low component of a duration must be a 64-bit unsigned integer.");
        }

        return low;
    }

    private static void RequireNumber(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("A duration array must hold exactly two numbers.");
        }
    }
}
