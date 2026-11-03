using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Arca;

/// <summary>
/// The few command-line switches other programs (Integra) use to manage Arca:
///   Arca.exe --version   prints the version
///   Arca.exe --status    prints the version and whether any Arca window is open (exit code 0 = running, 1 = not)
///   Arca.exe --quit      closes every open Arca window and waits for them to go (exit code 0 = closed or wasn't running, 1 = still running)
/// Anything else starts Arca as usual (a file path opens that archive).
/// Arca can have several windows at once, so "running" means "another Arca.exe process exists", found by name.
/// </summary>
internal static class Cli
{
    /// <summary>Handles a switch and returns the exit code, or null when <paramref name="args"/> holds no switch (start Arca).</summary>
    public static int? TryRun(string[] args)
    {
        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)))
        {
            Print($"Arca {Version}");
            return 0;
        }
        if (args.Any(a => a.Equals("--status", StringComparison.OrdinalIgnoreCase)))
        {
            bool running = OtherInstances().Count > 0;
            Print($"Arca {Version}\nStatus: {(running ? "running" : "not running")}");
            return running ? 0 : 1;
        }
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
            return Quit();
        return null;
    }

    /// <summary>The version in this exe's file properties, e.g. "0.1.0" (any "+commit" suffix dropped).</summary>
    public static string Version
    {
        get
        {
            var v = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "").ProductVersion ?? "0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    private static List<Process> OtherInstances()
    {
        var self = Environment.ProcessId;
        var others = new List<Process>();
        foreach (var p in Process.GetProcessesByName("Arca"))
        {
            if (p.Id == self) p.Dispose();
            else others.Add(p);
        }
        return others;
    }

    private static int Quit()
    {
        var others = OtherInstances();
        if (others.Count == 0)
        {
            Print("Arca is not running.");
            return 0;
        }

        try
        {
            // Ask each window to close the normal way, so an extraction in progress gets the chance to stop cleanly.
            foreach (var p in others)
            {
                try { p.CloseMainWindow(); }
                catch (InvalidOperationException) { }   // already gone
            }

            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                if (others.All(HasExited))
                {
                    Print("Arca closed.");
                    return 0;
                }
            }
            Print("Arca did not close within 10 seconds.");
            return 1;
        }
        finally
        {
            foreach (var p in others) p.Dispose();
        }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    // Arca is a windowed program, so it has no console of its own. Output goes to the caller's pipe when
    // one is attached (Integra, `| more`), otherwise to the console of the terminal that started it.
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(int processId);
    private const int StdOutput = -11;
    private const int AttachParentProcess = -1;

    private static void Print(string text)
    {
        try
        {
            var handle = GetStdHandle(StdOutput);
            Stream stream;
            // GetFileType is 0 (unknown) for a handle that points at nothing usable.
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0)
            {
                stream = Console.OpenStandardOutput();
            }
            else if (AttachConsole(AttachParentProcess))
            {
                stream = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            }
            else
            {
                return; // started with no console and no pipe: nowhere to print
            }
            using (stream)
            {
                var bytes = new UTF8Encoding(false).GetBytes(text + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
