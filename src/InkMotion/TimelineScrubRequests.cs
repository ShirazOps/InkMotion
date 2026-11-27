namespace InkMotion;

internal sealed class TimelineScrubRequests
{
    private int? _pending;
    private int? _last;

    public void Request(int framePosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(framePosition);
        _pending = framePosition;
    }

    public bool TryTake(out int framePosition)
    {
        int? pending = _pending;
        _pending = null;
        framePosition = pending ?? 0;
        if (pending is null || pending == _last)
            return false;
        _last = pending;
        return true;
    }

    public void Reset()
    {
        _pending = null;
        _last = null;
    }
}
