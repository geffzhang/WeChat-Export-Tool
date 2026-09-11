
using System;

namespace WeChatExport.Core.Models;

public class Contact
{
    public long UserId { get; set; }
    public string? NickName { get; set; }
    public string? Remark { get; set; }
    public string? DisplayName => !string.IsNullOrEmpty(Remark) ? Remark : NickName;
    public string? AvatarPath { get; set; }
    public int UnreadCount { get; set; }
    public DateTime? LastMessageTime { get; set; }
    public string? LastMessagePreview { get; set; }
}
