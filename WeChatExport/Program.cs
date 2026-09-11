using System;
using System.IO;
using Avalonia;
using Serilog;
using Serilog.Events;
using WeChatExport.Services;

namespace WeChatExport;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatExport",
            "logs");
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // The elevated key-capture child is this same executable and logs
                // to this same file. Without shared:true the second process's
                // writes would collide with the first's.
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled AppDomain exception");
        };

        try
        {
            // Key capture has to run elevated, so the GUI relaunches this same
            // executable through the shell's runas verb. That second instance is
            // this branch: no window, no Avalonia, just the capture, reporting
            // progress through files for the original instance to relay.
            // The finally below flushes the log for this path too.
            if (args.Length >= 2 && args[0] == KeyCaptureProtocol.CaptureSwitch)
                return ElevatedKeyCapture.Run(args[1]);

            Log.Information("Application starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
