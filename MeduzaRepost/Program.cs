using MeduzaRepost;
using MeduzaRepost.Database;

Config.Log.Debug("Upgrading databases...");
await DbImporter.UpgradeAsync(Config.Cts.Token).ConfigureAwait(false);

Config.Log.Debug("Creating telegram reader...");
using var reader = new TelegramReader();
Config.Log.Debug("Creating mastodon writer...");
using var writer = new MastodonWriter();

Config.Log.Debug("Running mastodon writer...");
var writerTask = writer.Run(reader);
using var unsubscriber = reader.Subscribe(writer);
Config.Log.Debug("Running telegram reader...");
await reader.Run().ConfigureAwait(false);
await writerTask.ConfigureAwait(false);
Config.Log.Info("Exiting");
