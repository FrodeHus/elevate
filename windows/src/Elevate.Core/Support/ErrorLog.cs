namespace Elevate.Core.Support;

/// <summary>A single recorded error, with its timestamp.</summary>
public sealed record DiagnosticsError(DateTimeOffset Date, string Message);

/// <summary>A ring buffer of the most recent errors, capped at a fixed capacity. Port of the Swift <c>ErrorLog</c>.</summary>
public sealed class ErrorLog
{
    private readonly List<DiagnosticsError> _buffer = [];
    private readonly int _capacity;

    public ErrorLog(int capacity = 50)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Appends an error, evicting the oldest entry once over capacity.</summary>
    public void Append(string message, DateTimeOffset? at = null)
    {
        _buffer.Add(new DiagnosticsError(at ?? DateTimeOffset.UtcNow, message));
        if (_buffer.Count > _capacity)
        {
            _buffer.RemoveRange(0, _buffer.Count - _capacity);
        }
    }

    /// <summary>Entries ordered oldest to newest.</summary>
    public IReadOnlyList<DiagnosticsError> Entries => _buffer;

    /// <summary>Swift's <c>Equatable</c>: same capacity and the same entries in the same order.</summary>
    public bool ContentEquals(ErrorLog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _capacity == other._capacity && _buffer.SequenceEqual(other._buffer);
    }
}
