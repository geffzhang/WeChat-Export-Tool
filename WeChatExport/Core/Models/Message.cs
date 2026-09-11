using System;

namespace WeChatExport.Core.Models;

public class Message
{
    public long MessageId { get; set; }
    public long SenderId { get; set; }

    /// <summary>
    /// The sender exactly as stored in the database - a wxid in a real WeChat
    /// database, which <see cref="SenderId"/> cannot represent. Used as the display
    /// label of last resort (see ExportService.GetSenderLabel) so a message is at
    /// least attributed to a stable, distinguishable identifier rather than "Unknown".
    /// </summary>
    public string? SenderIdentifier { get; set; }

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
