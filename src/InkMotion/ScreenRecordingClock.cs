using System.Diagnostics;

namespace InkMotion;

/// <summary>
/// Thread-safe recording timeline shared by screen, audio and webcam capture. Paused wall-clock time is
/// excluded from <see cref="Elapsed"/>, so every source stays on one continuous timeline across pauses.
/// </summary>
public sealed class ScreenRecordingClock
{
    private readonly object _sync = new();
    private readonly Stopwatch _recorded = new();
    // Set while running or after stopping, so paused capture loops block without polling.
    private readonly ManualResetEventSlim _released = new(false);
    private bool _started;
    private bool _stopped;
    private bool _paused;
    private int _pauseCount;

    public static ScreenRecordingClock StartNew()
    {
        var clock = new ScreenRecordingClock();
        clock.Start();
        return clock;
    }

    public TimeSpan Elapsed { get { lock (_sync) return _recorded.Elapsed; } }
    public bool IsRunning { get { lock (_sync) return _started && !_stopped && !_paused; } }
    public bool IsPaused { get { lock (_sync) return _paused; } }
    public int PauseCount { get { lock (_sync) return _pauseCount; } }

    public void Start()
    {
        lock (_sync)
        {
            if (_started || _stopped) throw new InvalidOperationException("The recording clock has already been used. Reset it first.");
            _started = true;
            _recorded.Start();
            _released.Set();
        }
    }

    /// <summary>Freezes the timeline. Returns false when the clock is not running.</summary>
    public bool Pause()
    {
        lock (_sync)
        {
            if (!_started || _stopped || _paused) return false;
            _recorded.Stop();
            _paused = true;
            _pauseCount++;
            _released.Reset();
            return true;
        }
    }

    /// <summary>Continues the timeline from the paused position. Returns false unless paused.</summary>
    public bool Resume()
    {
        lock (_sync)
        {
            if (!_paused || _stopped) return false;
            _paused = false;
            _recorded.Start();
            _released.Set();
            return true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _recorded.Stop();
            _stopped = true;
            _paused = false;
            _released.Set();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _recorded.Reset();
            _started = _stopped = _paused = false;
            _pauseCount = 0;
            _released.Reset();
        }
    }

    /// <summary>
    /// Blocks while paused (or not yet started). Returns true when the clock is running, or false when it
    /// has stopped or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public bool WaitWhilePaused(CancellationToken cancellationToken)
    {
        while (true)
        {
            try { _released.Wait(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
            lock (_sync)
            {
                if (_stopped) return false;
                if (_started && !_paused) return true;
            }
        }
    }
}
