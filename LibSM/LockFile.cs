namespace SourceManager;

public sealed class LockFile : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;
    private bool _disposed;

    public LockFile(string path)
    {
        _path = path;
    }

    public bool TryAcquire(int timeoutMs = 3000)
    {
        int waited = 0;
        int interval = 50;
        while (waited < timeoutMs)
        {
            try
            {
                _stream = new FileStream(_path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None, 4096, System.IO.FileOptions.DeleteOnClose);
                using var writer = new StreamWriter(_stream, leaveOpen: true);
                writer.WriteLine($"{System.Environment.ProcessId}");
                writer.WriteLine($"{System.Environment.MachineName}");
                writer.Flush();
                return true;
            }
            catch (IOException)
            {
                if (File.Exists(_path))
                {
                    try
                    {
                        var fileAge = DateTime.Now - File.GetLastWriteTime(_path);
                        if (fileAge.TotalMinutes > 5)
                        {
                            File.Delete(_path);
                            continue;
                        }
                    }
                    catch { }
                }
            }
            Thread.Sleep(interval);
            waited += interval;
            interval = Math.Min(interval * 2, 500);
        }
        return false;
    }

    public void Release()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream?.Dispose();
            try { File.Delete(_path); } catch { }
            _disposed = true;
        }
    }
}