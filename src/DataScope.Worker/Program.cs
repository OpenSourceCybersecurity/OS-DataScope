using DataScope.Core.Activity;
using DataScope.Core.ScanJobs;
using DataScope.Persistence;
using DataScope.Persistence.Configuration;
using DataScope.Persistence.Settings;
using DataScope.Scanner;
using DataScope.Scanner.Content;
using DataScope.Scanner.Content.Isolation;
using DataScope.Worker;

// Started by the worker itself as a file reader process (D127): reads files on request, nothing else.
if (args is [ContentHost.Argument, ..])
{
    return ContentHost.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

RunWorker(args);
return 0;

// The service itself. A method of its own, so a reader process never loads the hosting, database or directory code.
static void RunWorker(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddDataScopeConfigFile();
    if (OperatingSystem.IsWindows())
    {
        WindowsHosting.AddWindowsServiceWithEventLog(builder.Services);
    }
    builder.Services.AddDataScopePersistence(builder.Configuration);
    // Checks in every minute so System health can tell whether the scanner is running.
    builder.Services.AddServiceHeartbeat(ServiceHeartbeatWriter.Scanner,
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ScanWorker).Assembly)?.InformationalVersion
            ?? "unknown").Split('+')[0]);
    builder.Services.AddHostedService<HeartbeatService>();
    // Settings saved on System settings (D123): read before the other services start, then every 30 seconds.
    builder.Services.AddSystemSettings(builder.Configuration);
    builder.Services.AddHostedService<SystemSettingsService>();
    builder.Services.AddSingleton<CurrentSettings>();
    builder.Services.AddSingleton<Func<ScanLimits>>(sp => sp.GetRequiredService<CurrentSettings>().Scan);
    builder.Services.AddSingleton<Func<ActivityAlertOptions>>(sp => sp.GetRequiredService<CurrentSettings>().Alerts);
    builder.Services.AddSingleton<Func<RetentionOptions>>(sp => sp.GetRequiredService<CurrentSettings>().Retention);
    builder.Services.AddSingleton<Func<NotificationOptions>>(sp => sp.GetRequiredService<CurrentSettings>().Notifications);
    builder.Services.AddSingleton<Func<SmtpOptions, IEmailSender>>(_ => smtp => new SmtpEmailSender(smtp));
    builder.Services.AddSingleton(ScanLimits.From(builder.Configuration));
    builder.Services.AddSingleton<IDirectorySource, FileSystemDirectorySource>();
    var contentSection = builder.Configuration.GetSection("Scanner:Content");
    if (contentSection.GetValue("Enabled", true))
    {
        var contentLimits = new ContentLimits();
        contentSection.Bind(contentLimits);
        contentLimits.Validate();
        builder.Services.AddSingleton(contentLimits);
        // Files are read in separate, limited processes, so a file that crashes or hangs a reader can't stop the scan.
        var workerAssembly = typeof(InventoryScanExecutor).Assembly.Location;
        builder.Services.AddSingleton<IContentReader>(_ => new IsolatedContentReader(contentLimits, () => ContentHostCommand.For(workerAssembly)));
        // Full scans read with more room per file (D130), in their own reader processes started with those limits.
        var fullLimits = contentLimits.ForFullScan();
        contentSection.GetSection("Full").Bind(fullLimits);
        fullLimits.Validate();
        builder.Services.AddSingleton(_ => new FullScanContentReader(new IsolatedContentReader(fullLimits, () => ContentHostCommand.For(workerAssembly))));
    }
    builder.Services.AddSingleton<IShareEnumerator>(ShareEnumeratorFactory.Create(builder.Configuration, builder.Environment));
    if (AccessReaderFactory.CreateSecurityReader(builder.Configuration, builder.Environment) is { } securityReader)
    {
        builder.Services.AddSingleton(securityReader);
    }
    if (AccessReaderFactory.CreateDirectoryReader(builder.Configuration, builder.Environment) is { } directoryReader)
    {
        builder.Services.AddSingleton(directoryReader);
    }
    builder.Services.AddSingleton<IScanExecutor>(sp => builder.Configuration["Scanner:Executor"] == "DryRun"
        ? new DryRunScanExecutor(builder.Configuration)
        : ActivatorUtilities.CreateInstance<InventoryScanExecutor>(sp));
    builder.Services.AddHostedService<ScanWorker>();
    builder.Services.AddHostedService<DirectorySyncWorker>();
    if (AccessReaderFactory.CreateEventSource(builder.Configuration, builder.Environment) is { } eventSource)
    {
        builder.Services.AddSingleton(eventSource);
    }
    // Checked here so a bad datascope.json stops the service with a plain message; the services read current values later.
    builder.Services.AddSingleton(CurrentSettings.BindAlerts(builder.Configuration));
    builder.Services.AddHostedService<ActivityCollector>();

    builder.Services.AddSingleton(CurrentSettings.BindNotifications(builder.Configuration, builder.Environment));
    builder.Services.AddHostedService<NotificationWorker>();

    var retention = new RetentionOptions();
    builder.Configuration.GetSection(RetentionOptions.Section).Bind(retention);
    retention.Validate();
    builder.Services.AddSingleton(retention);
    builder.Services.AddHostedService<RetentionWorker>();

    builder.Build().Run();
}
