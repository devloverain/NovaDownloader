namespace NovaDownloader.Core.Engine;

public class SpeedCalculator
{
    private readonly double _windowSeconds;
    private readonly Queue<(DateTime Timestamp, long Bytes)> _samples = new();
    private readonly Lock _lock = new();

    public SpeedCalculator(double windowSeconds = 3.0)
    {
        _windowSeconds = Math.Max(0.5, windowSeconds);
    }

    public void AddSample(long totalDownloadedBytes)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _samples.Enqueue((now, totalDownloadedBytes));

            var cutoff = now.AddSeconds(-_windowSeconds);
            while (_samples.Count > 2 && _samples.Peek().Timestamp < cutoff)
            {
                _samples.Dequeue();
            }
        }
    }

    public double CalculateSpeedBytesPerSec()
    {
        lock (_lock)
        {
            if (_samples.Count < 2) return 0.0;

            var oldest = _samples.Peek();
            var newest = _samples.Last();

            var elapsedSeconds = (newest.Timestamp - oldest.Timestamp).TotalSeconds;
            if (elapsedSeconds <= 0.001) return 0.0;

            var bytesDelta = newest.Bytes - oldest.Bytes;
            if (bytesDelta <= 0) return 0.0;

            return bytesDelta / elapsedSeconds;
        }
    }

    public TimeSpan? CalculateEta(long totalBytes, long downloadedBytes, double speedBytesPerSec)
    {
        if (totalBytes <= 0 || speedBytesPerSec <= 10) return null;

        var remainingBytes = totalBytes - downloadedBytes;
        if (remainingBytes <= 0) return TimeSpan.Zero;

        var seconds = remainingBytes / speedBytesPerSec;
        if (seconds > TimeSpan.MaxValue.TotalSeconds || double.IsInfinity(seconds) || double.IsNaN(seconds))
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(seconds, 86400 * 365));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
        }
    }
}
