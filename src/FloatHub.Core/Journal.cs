using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FloatHub.Core;

public sealed class Journal(string folder)
{
    private readonly object gate = new();
    public void Record(string action, Exception? error = null)
    {
        lock (gate)
        {
            try
            {
                var dir = Directory.CreateDirectory(Path.Combine(folder, "logs"));
                var now = DateTimeOffset.Now;
                var file = Path.Combine(dir.FullName, $"{now:yyyy-MM-dd}.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Move(file, Path.Combine(dir.FullName, $"{now:yyyy-MM-dd-HHmmssfff}.jsonl"), true);
                File.AppendAllText(file, JsonSerializer.Serialize(new
                {
                    time = now,
                    level = error == null ? "info" : "error",
                    action,
                    type = error?.GetType().Name,
                    message = error?.Message
                }) + Environment.NewLine);
                foreach (var old in dir.GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8)) old.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
