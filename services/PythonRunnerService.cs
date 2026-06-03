using System.Diagnostics;
using System.Text;

namespace Concert_Viewer.Services;

public sealed class PythonRunnerService : IAsyncDisposable
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PythonRunnerService> _logger;
    private readonly object _syncRoot = new();

    private Process? _process;
    private CancellationTokenSource? _processCancellation;

    public PythonRunnerService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<PythonRunnerService> logger)
    {
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    public event Action<PythonRunnerOutput>? OutputReceived;
    public event Action<int?>? ProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Task StartScraperAsync(DateOnly fromDate, DateOnly untilDate, string city = "london")
    {
        lock (_syncRoot)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("A scraper run is already in progress.");
            }

            _process?.Dispose();
            _processCancellation?.Dispose();
            _processCancellation = new CancellationTokenSource();

            var masterDirectory = Path.Combine(_environment.ContentRootPath, "python_logic", "Master");
            var scriptPath = Path.Combine(masterDirectory, "run_all_scrapers.py");
            var databasePath = Path.Combine(_environment.ContentRootPath, "concerts.db");
            var appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("The scraper entrypoint could not be found.", scriptPath);
            }

            var process = new Process
            {
                StartInfo = BuildStartInfo(masterDirectory, scriptPath, databasePath, appSettingsPath, fromDate, untilDate, city),
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Python process could not be started.");
            }

            _process = process;
            Emit(PythonRunnerOutput.System($"Started scraper process {process.Id}."));
            _ = Task.Run(() => MonitorProcessAsync(process, _processCancellation.Token));
        }

        return Task.CompletedTask;
    }

    public async Task SendInputAsync(string input)
    {
        Process process;
        lock (_syncRoot)
        {
            if (_process is not { HasExited: false } runningProcess)
            {
                throw new InvalidOperationException("There is no running scraper process.");
            }

            process = runningProcess;
        }

        await process.StandardInput.WriteLineAsync(input);
        await process.StandardInput.FlushAsync();
        Emit(PythonRunnerOutput.Input($"> {input}{Environment.NewLine}"));
    }

    public Task StopAsync()
    {
        lock (_syncRoot)
        {
            if (_process is not { HasExited: false } process)
            {
                return Task.CompletedTask;
            }

            _processCancellation?.Cancel();
            process.Kill(entireProcessTree: true);
            Emit(PythonRunnerOutput.System("Stop requested."));
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        lock (_syncRoot)
        {
            _process?.Dispose();
            _processCancellation?.Dispose();
            _process = null;
            _processCancellation = null;
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string workingDirectory,
        string scriptPath,
        string databasePath,
        string appSettingsPath,
        DateOnly fromDate,
        DateOnly untilDate,
        string city)
    {
        var pythonExecutable = _configuration["PythonRunner:Executable"];
        if (string.IsNullOrWhiteSpace(pythonExecutable))
        {
            pythonExecutable = "python";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--city");
        startInfo.ArgumentList.Add(city);
        startInfo.ArgumentList.Add("--from-date");
        startInfo.ArgumentList.Add(fromDate.ToString("yyyy-MM-dd"));
        startInfo.ArgumentList.Add("--until-date");
        startInfo.ArgumentList.Add(untilDate.ToString("yyyy-MM-dd"));

        var scrapingAntApiKey = _configuration["PythonSettings:ScrapingAnt:ApiKey"];
        if (!string.IsNullOrWhiteSpace(scrapingAntApiKey))
        {
            startInfo.ArgumentList.Add("--api-key");
            startInfo.ArgumentList.Add(scrapingAntApiKey);
        }

        startInfo.ArgumentList.Add("--db-path");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add("--app-settings-path");
        startInfo.ArgumentList.Add(appSettingsPath);
        startInfo.ArgumentList.Add("--verbose");

        return startInfo;
    }

    private async Task MonitorProcessAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var stdoutTask = ReadStreamAsync(process.StandardOutput, PythonOutputKind.StandardOutput, cancellationToken);
            var stderrTask = ReadStreamAsync(process.StandardError, PythonOutputKind.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);

            Emit(PythonRunnerOutput.System($"Process exited with code {process.ExitCode}."));
            ProcessExited?.Invoke(process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            ProcessExited?.Invoke(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Python scraper process failed.");
            Emit(PythonRunnerOutput.Error($"{Environment.NewLine}{ex.Message}{Environment.NewLine}"));
            ProcessExited?.Invoke(null);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }
        }
    }

    private async Task ReadStreamAsync(StreamReader reader, PythonOutputKind kind, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }

            var text = new string(buffer, 0, count);
            Emit(new PythonRunnerOutput(kind, text));
        }
    }

    private void Emit(PythonRunnerOutput output)
    {
        OutputReceived?.Invoke(output);
    }
}

public sealed record PythonRunnerOutput(PythonOutputKind Kind, string Text)
{
    public static PythonRunnerOutput System(string text) =>
        new(PythonOutputKind.System, $"{text}{Environment.NewLine}");

    public static PythonRunnerOutput Input(string text) =>
        new(PythonOutputKind.Input, text);

    public static PythonRunnerOutput Error(string text) =>
        new(PythonOutputKind.StandardError, text);
}

public enum PythonOutputKind
{
    StandardOutput,
    StandardError,
    Input,
    System
}
