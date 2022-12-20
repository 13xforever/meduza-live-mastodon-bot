
using MeduzaRepost;
using MeduzaRepost.Database;

Config.Log.Info("Upgrading databases...");
await DbImporter.UpgradeAsync(Config.Cts.Token).ConfigureAwait(false);

await new TelegramReader().Run().ConfigureAwait(false);
//await new MastodonWriter().Run().ConfigureAwait(false);
