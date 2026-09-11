
using System;

namespace WeChatExport.Core.Models;

public class Contact
{
    /// <summary>
    /// The identifier the database uses for this contact, kept as the string it
    /// really is (a wxid in a real WeChat database). <see cref="UserId"/> is the
    /// numeric view of the same thing and is 0 whenever the identifier is not
    /// numeric - which is why per-sender lookups must key off this property.
    /// </summary>
    public string? Identifier { get; set; }

    public long UserId { get; set; }
    public string? NickName { get; set; }
    public string? Remark { get; set; }
    public string? DisplayName => !string.IsNullOrEmpty(Remark) ? Remark : NickName;
    public string? AvatarPath { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastMessageTime { get; set; }
    public string? LastMessagePreview { get; set; }
}
