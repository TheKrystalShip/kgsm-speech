namespace TheKrystalShip.KGSM.Speech.Daemon;

/// <summary>
/// How one half of the engine — hearing, or speaking — has been getting on since this process started.
/// </summary>
/// <remarks>
/// <para>
/// <b>In memory, and gone when the process is.</b> This daemon exits when nothing has been asked of it
/// for long enough, which is the point of it, so these are the tallies of one run rather than of a
/// host. Whoever reports them reports when the run began beside them.
/// </para>
/// <para>
/// <b>Durations are kept as a fixed ring of the most recent passes</b>, not as a running sum. A mean
/// over the life of a process describes a machine that was busy an hour ago; the last sixty-four
/// passes describe the one somebody is waiting on. The ring is what makes a percentile possible at
/// all, and bounding it is what keeps a daemon that has been up for a week from holding a list of
/// every sentence it ever said.
/// </para>
/// <para>
/// <b>Counted, never sampled.</b> Every pass through either lane lands here exactly once, including
/// the ones that failed — a lane whose failures were not counted would report a healthy mean over the
/// requests that happened to work.
/// </para>
/// </remarks>
internal sealed class LaneTally
{
    /// <summary>How many recent passes to remember. Enough for a percentile, small enough to scan.</summary>
    private const int Remembered = 64;

    private readonly object _gate = new();
    private readonly int[] _durations = new int[Remembered];

    private int _at;
    private int _held;

    private long _done;
    private long _rejected;
    private long _failed;
    private long _characters;
    private double _audioSeconds;
    private int _last;
    private int _waiting;
    private int _running;
    private DateTimeOffset? _lastAt;
    private string? _lastOutcome;

    /// <summary>Someone is queued for this lane, or is in it. Both are what "waiting" means to a caller.</summary>
    public void Queued() => Interlocked.Increment(ref _waiting);

    /// <summary>They have left the queue — either into the lane, or by giving up.</summary>
    public void Dequeued() => Interlocked.Decrement(ref _waiting);

    /// <summary>A pass has started.</summary>
    public void Started() => Interlocked.Increment(ref _running);

    /// <summary>
    /// A pass has finished, however it went.
    /// </summary>
    /// <param name="outcome">What the caller was told.</param>
    /// <param name="milliseconds">How long it took, wall-clock.</param>
    /// <param name="audioSeconds">Seconds of audio read or produced. Zero for a pass that produced none.</param>
    /// <param name="characters">Characters said. Zero on the recognition side.</param>
    public void Finished(
        SpeechProtocol.Outcome outcome, int milliseconds, double audioSeconds = 0, int characters = 0)
    {
        Interlocked.Decrement(ref _running);

        lock (_gate)
        {
            switch (outcome)
            {
                case SpeechProtocol.Outcome.Done: _done++; break;
                case SpeechProtocol.Outcome.Busy: _rejected++; break;
                default: _failed++; break;
            }

            _audioSeconds += audioSeconds;
            _characters += characters;
            _lastAt = DateTimeOffset.UtcNow;
            _lastOutcome = Word(outcome);

            // A pass that was turned away without running never took any time, so timing it would drag
            // the mean towards zero with a number that is not a duration of anything.
            if (outcome == SpeechProtocol.Outcome.Busy) return;

            _last = milliseconds;
            _durations[_at] = milliseconds;
            _at = (_at + 1) % Remembered;
            if (_held < Remembered) _held++;
        }
    }

    /// <summary>This lane as it stands, for the status report.</summary>
    public SpeechLane Reported(bool available, string detail, string model, long modelBytes, string runtime)
    {
        lock (_gate)
        {
            int[] recent = _durations[.._held];
            Array.Sort(recent);

            return new SpeechLane
            {
                Available = available,
                Detail = detail,
                Model = model,
                ModelBytes = modelBytes,
                Runtime = runtime,
                Busy = Volatile.Read(ref _running) > 0,
                // Everyone queued minus whoever is actually in the lane: a caller wants to know how many
                // are ahead of theirs, and counting the one being served as waiting says one too many.
                Waiting = Math.Max(0, Volatile.Read(ref _waiting) - Volatile.Read(ref _running)),
                Done = _done,
                Rejected = _rejected,
                Failed = _failed,
                AudioSeconds = _audioSeconds,
                Characters = _characters,
                LastMilliseconds = _held == 0 ? null : _last,
                MeanMilliseconds = _held == 0 ? null : (int)recent.Average(),
                // The nearest remembered pass at or above the 95th percentile. On a ring of a handful
                // that is the slowest one, which is the honest answer for a sample that size.
                P95Milliseconds = _held == 0 ? null : recent[Math.Min(_held - 1, (int)Math.Ceiling(_held * 0.95) - 1)],
                LastAt = _lastAt,
                LastOutcome = _lastOutcome,
            };
        }
    }

    private static string Word(SpeechProtocol.Outcome outcome) => outcome switch
    {
        SpeechProtocol.Outcome.Done => "done",
        SpeechProtocol.Outcome.Busy => "busy",
        SpeechProtocol.Outcome.Unavailable => "unavailable",
        _ => "failed",
    };
}
