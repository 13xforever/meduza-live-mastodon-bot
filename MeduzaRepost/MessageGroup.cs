using TL;

namespace MeduzaRepost;

public sealed class MessageGroup
{
    public long Id { get; }
    public int Expected { get; }
    public List<Message> MessageList { get; }

    public MessageGroup(Message firstMessage)
    {
        Id = 0;
        Expected = 1;
        MessageList = new(1) { firstMessage };
    }
    
    public MessageGroup(List<Message> messages)
    {
        Id = 0;
        Expected = messages.Count;
        MessageList = messages;
    }

    public MessageGroup(long groupId, List<Message> messages)
    {
        Id = groupId;
        Expected = messages.Count;
        MessageList = messages;
    }
    
    public MessageGroup(long groupId, int expectedCount, Message firstMessage)
    {
        Id = groupId;
        Expected = expectedCount;
        MessageList = new(expectedCount) { firstMessage };
    }
    
    public MessageGroup(int[] messageIds)
    {
        Id = 0;
        Expected = messageIds.Length;
        MessageList = messageIds.Select(id => new Message { id = id }).ToList();
    }
}