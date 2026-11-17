namespace PulseOfTime;

/// Session log of everything that happened (events, enemy actions, animations, treasure), with times, in
/// pulse-events.log next to the exe: what to tune and which names the game uses where.
static class Log
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "pulse-events.log");
    static readonly object Gate = new();
    static StreamWriter? _w;

    public static void Write(string line, bool echo)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        if (echo) Console.WriteLine(stamped);
        lock (Gate)
        {
            try
            {
                _w ??= new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                _w.WriteLine(stamped);
            }
            catch { }
        }
    }
}
