using MeduzaRepost;
using MeduzaRepost.Database;

Config.Log.Info("Upgrading databases...");
await DbImporter.UpgradeAsync(Config.Cts.Token).ConfigureAwait(false);

Config.Log.Info("Creating telegram reader...");
using var reader = new TelegramReader();
Config.Log.Info("Creating mastodon writer...");
using var writer = new MastodonWriter();

Config.Log.Info("Running mastodon writer...");
var writerTask = writer.Run(reader);
using var unsubscriber = reader.Subscribe(writer);
Config.Log.Info("Running telegram reader...");
await reader.Run().ConfigureAwait(false);
await writerTask.ConfigureAwait(false);
Config.Log.Info("Exiting");
