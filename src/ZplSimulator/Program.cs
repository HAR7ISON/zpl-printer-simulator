using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;

namespace ZplSimulator;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            if (args.Length == 0)
            {
                ServiceBase.Run(new SimulatorService());
                return 0;
            }
            if (args is ["--console"])
            {
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                new Worker().Run(stop.Token).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length is 3 or 4 && args[0] is "--render" or "--print")
            {
                var file = args[1];
                var destination = args[2];
                var config = Config.Load(args.Length == 4 ? args[3] : null);
                var pages = new Renderer(config, Console.Error.WriteLine).Render(File.ReadAllText(file));
                Directory.CreateDirectory(destination);
                if (args[0] == "--print")
                {
                    if (!config.PrintToFile) throw new InvalidDataException("--print requires PrintToFile=true; use the installed queue for physical printing.");
                    var pdf = Path.GetFullPath(Path.Combine(destination, "label-" + Guid.NewGuid().ToString("N") + ".pdf"));
                    NativePrinter.Print(config, pages, Path.GetFileName(file), pdf);
                    Console.WriteLine("Submitted PDF: " + pdf);
                    return 0;
                }
                for (var i = 0; i < pages.Count; i++) File.WriteAllBytes(Path.Combine(destination, $"label-{i + 1:000}.png"), pages[i].Png);
                Console.WriteLine($"Rendered {pages.Count} label(s).");
                return 0;
            }
            Console.Error.WriteLine("Usage: ZplSimulator --console | --render input.zpl output-folder [config.json] | --print input.zpl output-folder [config.json]");
            return 2;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

internal sealed class SimulatorService : ServiceBase
{
    private CancellationTokenSource? stop;
    private Task? worker;
    public SimulatorService() { ServiceName = "ZplSimulator"; CanStop = true; }
    protected override void OnStart(string[] args)
    {
        _ = Config.Load(); // Report invalid configuration to the Service Control Manager.
        stop = new CancellationTokenSource();
        worker = new Worker().Run(stop.Token);
        _ = worker.ContinueWith(t =>
        {
            if (t.IsFaulted) { Worker.Log(t.Exception!.ToString()); Environment.Exit(1); }
        }, TaskScheduler.Default);
    }
    protected override void OnStop()
    {
        stop?.Cancel();
        try { worker?.Wait(TimeSpan.FromSeconds(15)); } catch (AggregateException) { }
    }
}

internal sealed class Worker
{
    private readonly string pending = Path.Combine(Config.DataDirectory, "Pending");
    private readonly string failed = Path.Combine(Config.DataDirectory, "Failed");
    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        lock (LogLock)
        {
            Directory.CreateDirectory(Config.DataDirectory);
            var path = Path.Combine(Config.DataDirectory, "service.log");
            if (File.Exists(path) && new FileInfo(path).Length > 5_000_000)
                File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }

    public async Task Run(CancellationToken stop)
    {
        var config = Config.Load();
        Directory.CreateDirectory(pending);
        Directory.CreateDirectory(failed);
        // Never auto-replay an interrupted print: the physical printer might already have printed it.
        foreach (var file in Directory.EnumerateFiles(pending).Where(f => f.EndsWith(".printing") || f.EndsWith(".receiving")))
        {
            File.Move(file, Path.Combine(failed, Path.GetFileName(file)), true);
            Log($"Interrupted job retained for manual inspection: {Path.GetFileName(file)}");
        }
        var listener = new TcpListener(IPAddress.Loopback, config.Port);
        listener.Start(16);
        Log($"Listening on 127.0.0.1:{config.Port}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var consumer = ProcessQueue(linked.Token);
        // A consumer failure must stop intake instead of silently accumulating jobs forever.
        _ = consumer.ContinueWith(t => { if (t.IsFaulted) linked.Cancel(); }, TaskScheduler.Default);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(linked.Token);
                try { await Receive(client, config.MaxJobBytes, linked.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
                { Log("Receive failed: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            listener.Stop();
            linked.Cancel();
            try { await consumer; } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task Receive(TcpClient client, int limit, CancellationToken stop)
    {
        // Standard TCP/IP RAW ports delimit jobs by closing the connection. An idle
        // connection is closed too, but the partial payload is quarantined, never printed.
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(pending, id + ".receiving");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                var buffer = new byte[8192];
                var stream = client.GetStream();
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int read = await stream.ReadAsync(buffer, idle.Token);
                    if (read == 0) break;
                    if (file.Length + read > limit) throw new InvalidDataException("Job exceeds MaxJobBytes.");
                    await file.WriteAsync(buffer.AsMemory(0, read), stop);
                }
                file.Flush(true);
                if (file.Length == 0) { file.Close(); File.Delete(path); return; }
            }
            File.Move(path, Path.ChangeExtension(path, ".zpl"));
            Log($"Received {id}");
        }
        catch
        {
            if (File.Exists(path)) File.Move(path, Path.Combine(failed, Path.GetFileName(path)));
            throw;
        }
    }

    private async Task ProcessQueue(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            foreach (var path in Directory.EnumerateFiles(pending, "*.zpl").Order())
            {
                stop.ThrowIfCancellationRequested();
                string active = Path.ChangeExtension(path, ".printing");
                File.Move(path, active);
                try
                {
                    var config = Config.Load(); // Target and label settings reload for every job.
                    if (new FileInfo(active).Length > config.MaxJobBytes) throw new InvalidDataException("Job exceeds MaxJobBytes.");
                    byte[] bytes = await File.ReadAllBytesAsync(active, stop);
                    // ZPL ^CI28 selects UTF-8; default Zebra code page is CP850.
                    bool utf8 = Encoding.Latin1.GetString(bytes).Contains("^CI28", StringComparison.Ordinal);
                    var text = (utf8 ? new UTF8Encoding(false, true) : Encoding.GetEncoding(850)).GetString(bytes).TrimStart('\uFEFF');
                    var pages = new Renderer(config, message => Log(Path.GetFileName(path) + ": " + message)).Render(text);
                    var name = Path.GetFileNameWithoutExtension(path);
                    string? output = null;
                    if (config.PrintToFile)
                    {
                        Directory.CreateDirectory(config.OutputDirectory);
                        output = Path.Combine(config.OutputDirectory, name + ".pdf");
                    }
                    NativePrinter.Print(config, pages, name, output);
                    if (output != null)
                    {
                        // EndDoc means spooled, not necessarily written to disk yet.
                        var deadline = DateTime.UtcNow.AddSeconds(60);
                        while ((!File.Exists(output) || new FileInfo(output).Length == 0) && DateTime.UtcNow < deadline)
                            await Task.Delay(250, stop);
                        if (!File.Exists(output) || new FileInfo(output).Length == 0)
                            throw new IOException("Printer accepted the job but no output appeared within 60 seconds. Check the destination queue before retrying.");
                    }
                    File.Delete(active);
                    Log($"Submitted {name}, {pages.Sum(p => p.Copies)} page(s) to {config.TargetPrinter}" + (output == null ? "" : $"; output: {output}"));
                }
                catch (Exception ex)
                {
                    var destination = Path.Combine(failed, Path.GetFileNameWithoutExtension(active) + ".zpl");
                    File.Move(active, destination, true);
                    await File.WriteAllTextAsync(destination + ".error.txt", ex.ToString(), CancellationToken.None);
                    Log($"Job failed, retained at {destination}: {ex.Message}");
                }
            }
            await Task.Delay(500, stop);
        }
    }
}
