using System.Diagnostics;
using System.Text;
using Npgsql;

namespace DataScope.Setup;

/// <summary>
/// Runs PostgreSQL's own pg_dump and pg_restore. The password goes to the child process in PGPASSWORD, never on its
/// command line, and only the last lines of their error output are kept for the message.
/// </summary>
internal sealed class PgTools(string binDirectory)
{
    public string BinDirectory { get; } = binDirectory;

    /// <summary>
    /// Finds the folder with pg_dump and pg_restore: the one given, else Backup:PgBinPath, else the PATH, else (on Windows)
    /// the newest "C:\Program Files\PostgreSQL\NN\bin".
    /// </summary>
    public static PgTools Locate(string? explicitDirectory, string? configured)
    {
        var exe = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        IEnumerable<string> candidates = [.. new[] { explicitDirectory, configured }.OfType<string>().Where(d => d.Length > 0)];
        if (explicitDirectory is null && configured is null)
        {
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> Installed() => OperatingSystem.IsWindows()
                ? Directory.EnumerateDirectories(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL"))
                    .Select(d => (Dir: Path.Combine(d, "bin"), Version: int.TryParse(Path.GetFileName(d), out var v) ? v : 0))
                    .OrderByDescending(x => x.Version).Select(x => x.Dir)
                : [];
            candidates = path.Concat(SafeList(Installed));
        }
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, exe)))
            {
                return new PgTools(Path.GetFullPath(dir));
            }
        }
        throw new SetupException(explicitDirectory is not null || configured is not null
            ? $"pg_dump was not found in {explicitDirectory ?? configured}. Point --pg-bin (or Backup:PgBinPath) at PostgreSQL's bin folder."
            : "pg_dump was not found. Install the PostgreSQL client tools, or point --pg-bin at PostgreSQL's bin folder (for example \"C:\\Program Files\\PostgreSQL\\16\\bin\").");
    }

    private static IEnumerable<string> SafeList(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>pg_dump in custom format (compressed, restorable by pg_restore); <paramref name="consume"/> reads its output.</summary>
    public Task DumpAsync(string connectionString, Func<Stream, Task> consume, CancellationToken ct) =>
        RunAsync("pg_dump", connectionString, ["--format=custom", "--no-owner", "--no-privileges", "--compress=6"], produce: null, consume, ct);

    /// <summary>pg_restore into the connection's database, all or nothing; <paramref name="produce"/> writes the archive.</summary>
    public Task RestoreAsync(string connectionString, Func<Stream, Task> produce, CancellationToken ct) =>
        RunAsync("pg_restore", connectionString, ["--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error"], produce, consume: null, ct);

    /// <summary>pg_restore --list: reads the whole archive and fails if it is not one.</summary>
    public Task ListAsync(Func<Stream, Task> produce, CancellationToken ct) =>
        RunAsync("pg_restore", connectionString: null, ["--list"], produce, s => s.CopyToAsync(Stream.Null, ct), ct);

    private async Task RunAsync(string tool, string? connectionString, string[] arguments, Func<Stream, Task>? produce, Func<Stream, Task>? consume, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(BinDirectory, OperatingSystem.IsWindows() ? tool + ".exe" : tool))
        {
            RedirectStandardInput = produce is not null,
            RedirectStandardOutput = consume is not null,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Clear anything inherited that could point the tool elsewhere.
        foreach (var name in new[] { "PGPASSWORD", "PGHOST", "PGPORT", "PGUSER", "PGDATABASE", "PGSERVICE", "PGPASSFILE", "PGSSLMODE", "PGOPTIONS" })
        {
            start.Environment.Remove(name);
        }
        if (connectionString is not null)
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString);
            start.ArgumentList.Add($"--host={cs.Host}");
            start.ArgumentList.Add($"--port={cs.Port}");
            start.ArgumentList.Add($"--username={cs.Username}");
            start.ArgumentList.Add($"--dbname={cs.Database}");
            start.ArgumentList.Add("--no-password");
            start.Environment["PGPASSWORD"] = cs.Password ?? "";
            start.Environment["PGSSLMODE"] = cs.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer",
            };
        }
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new SetupException($"Could not start {tool}: {ex.Message}");
        }
        void Stop()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
        using var kill = ct.Register(Stop);
        var errors = ReadTailAsync(process.StandardError);
        var output = consume is null ? Task.CompletedTask : consume(process.StandardOutput.BaseStream);
        var input = produce is null ? Task.CompletedTask : FeedAsync(produce, process.StandardInput.BaseStream);
        try
        {
            await input;
        }
        catch (SetupException)
        {
            // Our side failed (for example a damaged backup): stop the tool so nothing half-done is kept.
            Stop();
            throw;
        }
        catch (IOException)
        {
            // Usually the tool stopped reading because it failed; its exit code and message say why.
            if (!await ExitedSoon(process))
            {
                Stop();
                throw;
            }
        }
        try
        {
            await output;
        }
        catch (Exception) when (process.HasExited && process.ExitCode != 0)
        {
            // Reported below with the tool's own message.
        }
        catch
        {
            // Our side failed (for example the disk filled up): don't leave the tool blocked on a full pipe.
            Stop();
            throw;
        }
        await process.WaitForExitAsync(ct);
        var message = await errors;
        if (process.ExitCode != 0)
        {
            throw new SetupException($"{tool} failed (exit code {process.ExitCode}): {message}".TrimEnd(':', ' '));
        }
    }

    private static async Task FeedAsync(Func<Stream, Task> produce, Stream target)
    {
        try
        {
            await produce(target);
        }
        finally
        {
            try
            {
                await target.DisposeAsync();
            }
            catch (IOException)
            {
                // The tool already closed its end.
            }
        }
    }

    private static async Task<bool> ExitedSoon(Process process)
    {
        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(10)));
        return process.HasExited;
    }

    /// <summary>The last 2,000 characters of the tool's error output; enough to explain a failure without flooding the console.</summary>
    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 2000)
            {
                tail.Remove(0, tail.Length - 2000);
            }
        }
        return tail.ToString().Trim();
    }
}
