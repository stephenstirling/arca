using System.IO.Pipes;

namespace Arca;

public enum ShellVerb
{
    /// <summary>"e": extract each archive beside itself (several top-level items still get a folder).</summary>
    ExtractHere,
    /// <summary>"x": extract each archive into a new folder named after it.</summary>
    ExtractToFolder,
    /// <summary>"a": pack the files and folders into a new archive.</summary>
    Add,
}

/// <summary>
/// What File Explorer's right-click menu asked for: <c>Arca.exe e|x|a "path"</c>.
/// Explorer starts one Arca per selected item, so the first one to start collects the paths of the others for a
/// moment (they hand theirs over through a named pipe and exit) and then shows one window for all of them.
/// </summary>
public sealed record ShellRequest(ShellVerb Verb, IReadOnlyList<string> Paths)
{
    /// <summary>How long the collector waits for the first other selected item, and then for each one after it.</summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan NextWait = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MaxCollecting = TimeSpan.FromSeconds(15);

    /// <summary>A request from <paramref name="args"/>, or null when they aren't one (no verb, or no existing path).</summary>
    public static ShellRequest? Parse(string[] args)
    {
        if (args.Length < 2) return null;
        ShellVerb? verb = args[0].ToLowerInvariant() switch
        {
            "e" => ShellVerb.ExtractHere,
            "x" => ShellVerb.ExtractToFolder,
            "a" => ShellVerb.Add,
            _ => null,
        };
        if (verb is null) return null;
        var paths = args.Skip(1).Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
        return paths.Count == 0 ? null : new ShellRequest(verb.Value, paths);
    }

    /// <summary>
    /// This request with every other one Explorer started for the same selection, or null when another Arca already
    /// took this one over (this process should then just exit).
    /// </summary>
    public ShellRequest? Gather()
    {
        var name = $"Arca.Gather.{Verb}.{Environment.UserName}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        using var mutex = new Mutex(false, @"Local\" + name);
        bool collector;
        try { collector = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { collector = true; }

        if (!collector) return HandOver(name) ? null : this;

        try
        {
            return this with { Paths = Collect(name) };
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private bool HandOver(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)(FirstWait + NextWait).TotalMilliseconds);
            using var writer = new StreamWriter(client);
            foreach (var path in Paths) writer.WriteLine(path);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // The collector already finished: this one opens its own window instead.
            return false;
        }
    }

    private List<string> Collect(string pipeName)
    {
        var paths = new List<string>(Paths);
        var until = DateTime.UtcNow + MaxCollecting;
        var wait = FirstWait;
        while (DateTime.UtcNow < until)
        {
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(wait);
            try
            {
                server.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            using var reader = new StreamReader(server);
            while (reader.ReadLine() is { } line)
                if (line.Length > 0 && (File.Exists(line) || Directory.Exists(line))) paths.Add(line);
            wait = NextWait;
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
