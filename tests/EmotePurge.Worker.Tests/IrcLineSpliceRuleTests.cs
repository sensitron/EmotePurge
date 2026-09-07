using Xunit;

namespace EmotePurge.Worker.Tests;

public class IrcLineSpliceRuleTests
{
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
}
