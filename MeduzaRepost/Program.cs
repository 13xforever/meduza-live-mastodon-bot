using System.Diagnostics;
using MeduzaRepost;
using MeduzaRepost.Database;

static void Restart()
{
    Config.Log.Info("♻️ Restarting…");
    var psi = new ProcessStartInfo("dotnet", "run -c Release");
    using (Process.Start(psi)) Environment.Exit(-1);
}

try
{
    Config.Log.Debug("Upgrading databases…");
    await DbImporter.UpgradeAsync(Config.Cts.Token).ConfigureAwait(false);

    Config.Log.Debug("Creating telegram reader…");
    using var reader = new TelegramReader();
    Config.Log.Debug("Creating mastodon writer…");
    using var writer = new MastodonWriter();

    Config.Log.Debug("Creating watchdog…");
    using var watchdog = new Watchdog(Restart);
    using var watchdogSub = reader.Subscribe(watchdog);

    Config.Log.Debug("Running mastodon writer…");
    var writerTask = writer.Run(reader);
    using var mastodonSub = reader.Subscribe(writer);
    Config.Log.Debug("Running telegram reader…");
    await reader.Run().ConfigureAwait(false);
    await writerTask.ConfigureAwait(false);
    if (!Config.Cts.IsCancellationRequested)
        Config.Log.Info("Exiting");
    else
        Restart();
}
catch (OperationCanceledException)
{
    Restart();
}