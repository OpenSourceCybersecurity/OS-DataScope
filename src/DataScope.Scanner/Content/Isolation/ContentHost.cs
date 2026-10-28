using DataScope.Core.Classification;

namespace DataScope.Scanner.Content.Isolation;

/// <summary>
/// The reader process (D127). The worker starts it as <c>DataScope.Worker.exe content-host</c> and sends it, over
/// standard input, first the content limits and then one file path at a time; it answers each with the text read or
/// the reason there is none. A file that crashes a reader, runs it out of memory or makes it hang takes down only
/// this process, which the worker replaces, so one hostile file can't stop a scan. It never writes anything else to
/// standard output, and never logs file contents.
/// </summary>
public static class ContentHost
{
    /// <summary>The argument that starts the worker as a reader process.</summary>
    public const string Argument = "content-host";

    /// <summary>Exit code after running out of memory: the process is not trusted to carry on.</summary>
    public const int OutOfMemoryExit = 3;

    /// <summary>
    /// Test hooks: a request for a file with the name in the first makes the process exit at once without answering,
    /// as a crash in a reader would; the second makes it hang; the third makes it hang before its start-up answer, as
    /// a reader that is slow to start would. Never set in production.
    /// </summary>
    internal const string CrashOnVariable = "DATASCOPE_CONTENT_HOST_TEST_CRASH_ON";

    internal const string HangOnVariable = "DATASCOPE_CONTENT_HOST_TEST_HANG_ON";

    internal const string HangAtStartVariable = "DATASCOPE_CONTENT_HOST_TEST_HANG_AT_START";

    public static int Run(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ContentLimits? limits;
        try
        {
            limits = HostProtocol.Read<ContentLimits>(input, HostProtocol.MaxRequestBytes);
            limits?.Validate();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            return 2;
        }
        if (limits is null)
        {
            return 0;
        }
        var reader = new FileContentReader(limits);
        var crashOn = Environment.GetEnvironmentVariable(CrashOnVariable);
        var hangOn = Environment.GetEnvironmentVariable(HangOnVariable);
        var hangAtStart = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HangAtStartVariable));
        while (true)
        {
            HostRequest? request;
            try
            {
                request = HostProtocol.Read<HostRequest>(input, HostProtocol.MaxRequestBytes);
            }
            catch (InvalidDataException)
            {
                return 2;
            }
            if (request is null)
            {
                return 0; // The worker closed the pipe: it is done with this process, or it has gone.
            }
            if (request.Path is not { } path)
            {
                if (hangAtStart)
                {
                    Thread.Sleep(Timeout.Infinite);
                }
                HostProtocol.Write(output, new HostResponse(ContentState.Scanned, null, null));
                continue;
            }
            if (!string.IsNullOrEmpty(crashOn) && string.Equals(Path.GetFileName(path), crashOn, StringComparison.Ordinal))
            {
                Environment.Exit(70);
            }
            if (!string.IsNullOrEmpty(hangOn) && string.Equals(Path.GetFileName(path), hangOn, StringComparison.Ordinal))
            {
                Thread.Sleep(Timeout.Infinite);
            }

            HostResponse response;
            var exit = false;
            try
            {
                var read = reader.Read(path, CancellationToken.None);
                response = new HostResponse(read.State, read.Text, read.Note);
            }
            catch (OutOfMemoryException)
            {
                response = new HostResponse(ContentState.Unreadable, null,
                    $"Reading this file needed more than the {limits.ReaderMemoryBytes / (1024 * 1024):N0} MB of memory the file reader is allowed.");
                exit = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A reader bug must not stop the scan. The message is not passed on: parser messages can quote contents.
                response = new HostResponse(ContentState.Unreadable, null, "Data Scope could not read this file because of an internal error.");
            }
            HostProtocol.Write(output, response);
            if (exit)
            {
                return OutOfMemoryExit;
            }
        }
    }
}
