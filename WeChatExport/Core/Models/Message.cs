using System;

namespace WeChatExport.Core.Models;

public class Message
{
    public long MessageId { get; set; }
    public long SenderId { get; set; }
    public string? SenderName { get; set; }
    public string? Content { get; set; }
    public MessageType Type { get; set; }
    public DateTime CreateTime { get; set; }
    public bool IsFromSelf { get; set; }
    public string? MediaPath { get; set; }
}

public enum MessageType
{
    Text = 1,
    Image = 3,
    Voice = 34,
    Video = 43,
    Emoji = 47,
    File = 49,
    System = 10000
}
