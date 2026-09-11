using System.Collections.Generic;

namespace WeChatExport.Core.Models;

public class Conversation
{
    public Contact Contact { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
    public int TotalMessageCount { get; set; }
}
