namespace MeduzaRepost;

public sealed record TgEvent(TgEventType Type, MessageGroup Group, int pts, string? Link = null);