using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MeduzaRepost.Database;

internal static class DbImporter
{
    internal static async Task<bool> UpgradeAsync(CancellationToken cancellationToken)
    {
        await using var db = new BotDb();
        return await UpgradeAsync(db, cancellationToken).ConfigureAwait(false);
    }
    
    private static async Task<bool> UpgradeAsync(DbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            Config.Log.Debug($"Upgrading {dbContext.GetType().Name} database if needed…");
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException e)
        {
            Config.Log.Warn(e, "Database upgrade failed, probably importing an unversioned one.");
            return false;
        }
        Config.Log.Info($"Database {dbContext.GetType().Name} is ready.");
        return true;
    }
    
    internal static string GetDbPath(string dbName, Environment.SpecialFolder desiredFolder)
    {
        var settingsFolder = Path.Combine(Environment.GetFolderPath(desiredFolder), "meduza-bot");
        try
        {
            if (!Directory.Exists(settingsFolder))
                Directory.CreateDirectory(settingsFolder);
        }
        catch (Exception e)
        {
            Config.Log.Error(e, "Failed to create settings folder " + settingsFolder);
            settingsFolder = "";
        }

        var dbPath = Path.Combine(settingsFolder, dbName);
        if (settingsFolder != "")
            try
            {
                if (File.Exists(dbName))
                {
                    Config.Log.Info($"Found local {dbName}, moving…");
                    if (File.Exists(dbPath))
                    {
                        Config.Log.Error($"{dbPath} already exists, please reslove the conflict manually");
                        throw new InvalidOperationException($"Failed to move local {dbName} to {dbPath}");
                    }
                        
                    var dbFiles = Directory.GetFiles(".", Path.GetFileNameWithoutExtension(dbName) + ".*");
                    foreach (var file in dbFiles)
                        File.Move(file, Path.Combine(settingsFolder, Path.GetFileName(file)));
                    Config.Log.Info($"Using {dbPath}");
                }
            }
            catch (Exception e)
            {
                Config.Log.Error(e, $"Failed to move local {dbName} to {dbPath}");
                throw;
            }
        return dbPath;
    }

    
}