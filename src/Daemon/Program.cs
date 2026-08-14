using System.Net.Sockets;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

using TheKrystalShip.KGSM.Speech;
using TheKrystalShip.KGSM.Speech.Daemon;

// kgsm-speech — the host's speech engine: one process holding whisper and kokoro, serving every
// surface that listens or speaks over a unix socket.
//
// It exists as its own process for one measured reason: the models cost about 1.6GB of host memory
// and a gigabyte of video memory, and NEITHER is released by unloading a model inside a running
// process — the CUDA runtime behind them stays resident for the life of whatever loaded it. Ending a
// process releases everything. So the models live somewhere that can end without taking a Discord
// bot or a web API with it.

const string SettingsFile = "kgsm-speech.settings.json";

IConfigurationRoot configuration = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, SettingsFile), optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = new SpeechOptions();
configuration.GetSection(SpeechOptions.Section).Bind(options);

using ILoggerFactory loggers = LoggerFactory.Create(logging =>
{
    logging.AddConfiguration(configuration.GetSection("Logging"));

    // journald reads the <N> priority prefix this writes, so `journalctl -p warning` filters this
    // daemon the same way it filters every other KGSM service.
    logging.AddSystemdConsole();
});

ILogger logger = loggers.CreateLogger("KGSM.Speech");

// What this host can say, asked the way a surface asks it. Its real job is to be the deploy's health
// probe: it proves the socket is bound, that connecting to it activates the daemon, and that the
// daemon reads its configuration and answers — none of which "the unit is active" proves. It is
// cheap on purpose, because the answer is a directory listing and loads no model.
if (args is ["--voices"])
{
    await using var asking = new SpeechClient(options.SocketPath, logger);
    (string speaking, IReadOnlyList<string> voices) = await asking.VoicesAsync();

    if (voices.Count == 0)
    {
        await Console.Error.WriteLineAsync(
            $"kgsm-speech: no voices — is the daemon reachable on {options.SocketPath}?");
        return 1;
    }

    Console.WriteLine($"speaking as {speaking}");
    foreach (string voice in voices) Console.WriteLine(voice == speaking ? $"* {voice}" : $"  {voice}");
    return 0;
}

using var stopping = new CancellationTokenSource();

// systemd sends SIGTERM to stop a unit. Without this the runtime's default handler ends the process
// mid-request, and the surface that asked waits out its own timeout instead of being told.
using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stopping.Cancel();
});

using Socket? listener = Listen(options, logger);
if (listener is null) return 1;

await new SpeechServer(listener, options, logger).RunAsync(stopping.Token);
return 0;

// The listening socket: systemd's when it activated us, our own when somebody ran this by hand.
//
// SOCKET ACTIVATION IS THE NORMAL PATH. systemd binds and listens on the socket at boot and passes it
// as file descriptor 3 the first time anything connects, so the socket exists — and a surface can
// connect to it — whether or not this daemon is running. That is what lets it idle-exit and be
// brought back by the next request with nothing lost.
//
// Binding it here is for running one by hand: useful for testing and for a host that has not been
// provisioned, and it is why the socket path is configuration rather than a constant.
static Socket? Listen(SpeechOptions options, ILogger logger)
{
    if (int.TryParse(Environment.GetEnvironmentVariable("LISTEN_FDS"), out int handed) && handed > 0)
    {
        // SD_LISTEN_FDS_START. systemd passes exactly the one socket this unit declares.
        const int First = 3;
        logger.LogDebug("Speech: adopting the socket systemd activated us with");
        return new Socket(new SafeSocketHandle((IntPtr)First, ownsHandle: true));
    }

    try
    {
        string path = options.SocketPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A socket file left behind by a process that did not exit cleanly would refuse the bind, and
        // nothing else on this host writes this path.
        File.Delete(path);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(16);

        logger.LogInformation("Speech: listening on {Socket} (started by hand)", path);
        return socket;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Speech: could not listen on {Socket}", options.SocketPath);
        return null;
    }
}
