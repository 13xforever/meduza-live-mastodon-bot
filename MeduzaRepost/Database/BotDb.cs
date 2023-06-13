using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace MeduzaRepost.Database;

internal sealed class BotDb: DbContext
{
    public DbSet<BotState> BotState { get; set; } = null!;
    public DbSet<MessageMap> MessageMaps { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var dbPath = DbImporter.GetDbPath("bot.db", Environment.SpecialFolder.ApplicationData);
#if DEBUG
        optionsBuilder.UseLoggerFactory(Config.LoggerFactory);
#endif
        optionsBuilder.UseSqlite($"Data Source=\"{dbPath}\"");
    }

}

internal sealed class BotState
{
    [Key]
    public string Key { get; set; } = null!;
    public string? Value { get; set; }
}

[Index(nameof(MastodonId), IsUnique = true)]
internal sealed class MessageMap
{
    [Key]
    public long TelegramId { get; set; }
    public string MastodonId { get; set; } = null!;
    public long? Pts { get; set; }
}