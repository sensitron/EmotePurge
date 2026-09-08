using Xunit;

namespace EmotePurge.Worker.Tests;

public class IrcLineSpliceRuleTests
{
    // Everything a real Twitch reply line carries before reply-parent-msg-body. Synthetic
    // (invented ids/logins/text), never recorded chat — same rule as everywhere else in this repo.
    private const string ReplyTagsBeforeParentBody =
        "@badge-info=;badges=;client-nonce=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;color=#1E90FF;"
        + "display-name=TestUser1;emotes=;first-msg=0;flags=;id=aaaa1111-1111-1111-1111-111111111111;mod=0;"
        + "reply-parent-display-name=TestUser0;";

    private const string ReplyTagsAfterParentBody =
        ";reply-parent-msg-id=eeee5555-5555-5555-5555-555555555555;reply-parent-user-id=555555555;"
        + "reply-parent-user-login=testuser0;reply-thread-parent-display-name=TestUser0;"
        + "reply-thread-parent-msg-id=eeee5555-5555-5555-5555-555555555555;"
        + "reply-thread-parent-user-login=testuser0;returning-chatter=0;room-id=111111111;subscriber=0;"
        + "tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
        + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :@TestUser0 alles klar";

    [Fact]
    public void IsSpliced_NullLine_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced(null));

    [Fact]
    public void IsSpliced_EmptyLine_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced(string.Empty));

    [Fact]
    public void IsSpliced_SplicedTagBlockWithSecondAt_ReturnsTrue()
    {
        // Synthetic: the splice lands inside the value of a typed tag ("subscriber=0"), so the
        // second line's tag block starts mid-value instead of after a ';'.
        var line = "@badge-info=;badges=;color=#0000FF;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0@badge-info=;badges=;display-name=TestUser2;"
            + "id=bbbb2222-2222-2222-2222-222222222222;mod=0;room-id=111111111;subscriber=0;"
            + "tmi-sent-ts=1694000000000;turbo=0;user-id=222222222;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_OrdinaryLineWithMentionInMessageText_ReturnsFalse()
    {
        var line = "@badge-info=;badges=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello @othertestuser1 how are you";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_OrdinaryLineWithHostmaskPrefix_ReturnsFalse()
    {
        var line = "@badge-info=;badges=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=444444444;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_LineWithoutTags_ReturnsFalse()
    {
        var line = ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ServerPing_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced("PING :tmi.twitch.tv"));

    [Fact]
    public void IsSpliced_NoSpaceWithSecondAt_ReturnsTrue()
    {
        // Synthetic: no space anywhere in the line, so the whole line is the tag block. A second
        // '@' here can only mean a splice landed with no trailing IRC command/params at all.
        var line = "@badge-info=;badges=;id=aaaa1111-1111-1111-1111-111111111111@badge-info=;id=bbbb2222-2222-2222-2222-222222222222";

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_NoSpaceWithoutSecondAt_ReturnsFalse()
    {
        // Synthetic: no space anywhere in the line and only the leading '@' — the whole line is
        // the tag block and it is not spliced.
        var line = "@badge-info=;badges=;id=aaaa1111-1111-1111-1111-111111111111";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void TagBlockForLog_NullLine_ReturnsEmptyString() => Assert.Equal(string.Empty, IrcLineSpliceRule.TagBlockForLog(null));

    [Fact]
    public void TagBlockForLog_EmptyLine_ReturnsEmptyString() => Assert.Equal(string.Empty, IrcLineSpliceRule.TagBlockForLog(string.Empty));

    [Fact]
    public void TagBlockForLog_LineWithSpace_ReturnsSegmentUpToFirstSpace()
    {
        var line = "@badge-info=;id=aaaa1111-1111-1111-1111-111111111111 :testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.Equal("@badge-info=;id=aaaa1111-1111-1111-1111-111111111111", IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_LineWithoutSpace_ReturnsWholeLine()
    {
        var line = "@badge-info=;id=aaaa1111-1111-1111-1111-111111111111";

        Assert.Equal(line, IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_TagBlockLongerThanMaxLength_TruncatesToMaxLength()
    {
        var tagBlock = new string('a', 20);
        var line = tagBlock + " rest of line ignored";

        Assert.Equal(new string('a', 10), IrcLineSpliceRule.TagBlockForLog(line, maxLength: 10));
    }

    [Fact]
    public void TagBlockForLog_TagBlockShorterThanMaxLength_ReturnsUnchanged()
    {
        var tagBlock = new string('a', 5);
        var line = tagBlock + " rest of line ignored";

        Assert.Equal(tagBlock, IrcLineSpliceRule.TagBlockForLog(line, maxLength: 10));
    }

    [Fact]
    public void IsSpliced_MentionInsideReplyParentBody_ReturnsFalse()
    {
        // The case that made the first version of this rule unusable: Twitch's reply UI prepends
        // "@nutzername " to every reply, reply-parent-msg-body carries the parent verbatim, and the
        // IRCv3 escaping leaves '@' alone. A bare second '@' therefore fires on every reply.
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ReplyParentBodyImitatingATagBlock_ReturnsFalse()
    {
        // Chat text under a stranger's control, shaped exactly like a spliced tag block. The ';'
        // is escaped as "\:", so the whole thing stays inside the one reply-parent-msg-body tag,
        // whose value the rule skips. Anything else would hand every chatter a sentinel trigger.
        var line = ReplyLine(@"@badge-info=\:badges=\shaha");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ReplyParentBodyEndingInATagBlockStart_ReturnsFalse()
    {
        // Same imitation, this time as the entire parent body and therefore at the very end of the
        // tag's value — the position where a real splice would sit.
        var line = ReplyLine("@badge-info=");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_SpliceIntoAReplyLineOutsideTheParentBody_ReturnsTrue()
    {
        // A reply line — so the parent body carries its usual mention — cut open inside the value
        // of a typed tag. Skipping free-text values must not blind the rule to this.
        var line = ReplyLine(@"@TestUser0\shallo\sdu")
            .Replace("room-id=111111111;subscriber=0;", "room-id=111111111;subscriber=0@badge-info=;badges=;", StringComparison.Ordinal);

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_SecondAtFollowedByKeyWithoutEquals_ReturnsFalse()
    {
        // A login is followed by a separator, never by '=' — that is the whole distinction.
        var line = "@badge-info=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "reply-parent-msg-id=eeee5555-5555-5555-5555-555555555555;room-id=111111111;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        Assert.False(IrcLineSpliceRule.IsSpliced(line.Replace("room-id=111111111", "room-id=111111111@testuser0", StringComparison.Ordinal)));
    }

    [Fact]
    public void TagBlockForLog_ReplyParentBody_ValueIsRedacted()
    {
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        // The promise the sentinel's log comment makes: no message text, not this line's and not a
        // stranger's. Everything else about the tag block survives unchanged.
        Assert.Contains("reply-parent-msg-body=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TestUser0\\shallo", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-display-name=TestUser0;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-user-login=testuser0;", tagBlock, StringComparison.Ordinal);
        Assert.StartsWith("@badge-info=;badges=;", tagBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TagBlockForLog_ThreadParentBody_ValueIsRedactedToo()
    {
        // The key is matched by its "msg-body" suffix, so a sibling tag Twitch may add later is
        // redacted from its first appearance instead of leaking once.
        var line = @"@badge-info=;reply-parent-msg-body=@TestUser0\shallo;"
            + @"reply-thread-parent-msg-body=@TestUser0\sanfang;room-id=111111111;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        Assert.Equal(
            "@badge-info=;reply-parent-msg-body=<entfernt>;reply-thread-parent-msg-body=<entfernt>;room-id=111111111;user-type=",
            tagBlock);
    }

    [Fact]
    public void TagBlockForLog_SplicedLineWithoutFreeTextTag_IsLoggedVerbatim()
    {
        var line = "@badge-info=;room-id=111111111;subscriber=0@badge-info=;room-id=222222222;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.Equal(
            "@badge-info=;room-id=111111111;subscriber=0@badge-info=;room-id=222222222;user-type=",
            IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_FreeTextValueRedactedBeforeTruncation()
    {
        // Redaction must not be something a long parent body can push past the length cap.
        var line = "@reply-parent-msg-body=" + new string('x', 4000) + ";room-id=111111111 "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        Assert.Equal("@reply-parent-msg-body=<entfernt>;room-id=111111111", tagBlock);
    }

    private static string ReplyLine(string parentBody) =>
        ReplyTagsBeforeParentBody + "reply-parent-msg-body=" + parentBody + ReplyTagsAfterParentBody;
}
