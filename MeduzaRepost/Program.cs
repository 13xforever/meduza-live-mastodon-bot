using MeduzaRepost;
using MeduzaRepost.Database;

Config.Log.Info("Upgrading databases...");
await DbImporter.UpgradeAsync(Config.Cts.Token).ConfigureAwait(false);

var reader = new TelegramReader();
var writer = new MastodonWriter();

var writerTask = writer.Run(reader);
using var unsubscriber = reader.Subscribe(writer);
await reader.Run().ConfigureAwait(false);
await writerTask.ConfigureAwait(false);
