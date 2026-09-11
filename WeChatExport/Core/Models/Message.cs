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

    /// <summary>
    /// The coarse message type, taken from the LOW 32 BITS of the database's
    /// <see cref="RawLocalType"/>.
    /// </summary>
    /// <remarks>
    /// The low 32 bits is what the old <c>(MessageType)longValue</c> cast produced
    /// anyway (MEASURED: every real value's low 32 bits is one of 1, 3, 43, 47, 49,
    /// 10000 - the coarse type), so this keeps the six exporters' behaviour
    /// unchanged. The HIGH bits carry the appmsg SUBTYPE (5 = link, 57 = quote,
    /// 63 = channels), which is preserved in <see cref="RawLocalType"/> rather than
    /// thrown away.
    /// </remarks>
    public MessageType Type { get; set; }

    /// <summary>
    /// The database's <c>local_type</c> exactly as stored, as a <see cref="long"/>.
    /// </summary>
    /// <remarks>
    /// WeChat packs a message's type and its subtype into this one integer, and it
    /// genuinely exceeds <see cref="int"/>: MEASURED, 13,883 rows in the real
    /// <c>message_1.db</c> are larger than <c>int.MaxValue</c>. <see cref="Type"/>
    /// alone cannot round-trip it, so the full value is kept here.
    /// </remarks>
    public long RawLocalType { get; set; }

    public DateTime CreateTime { get; set; }

    public bool IsFromSelf { get; set; }

    public string? MediaPath { get; set; }

    /// <summary>
    /// True when the stored content was flagged as zstd-compressed but could not be
    /// decompressed, so <see cref="Content"/> is a marker rather than the message.
    /// </summary>
    /// <remarks>
    /// Kept as an explicit flag as well as a marker string so a caller can render it
    /// honestly (instead of showing the marker as if it were the user's text) without
    /// having to recognise the marker itself.
    /// </remarks>
    public bool ContentUnsupported { get; set; }
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
