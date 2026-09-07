using TwitchLib.Client.Models;
using TwitchLib.Client.Parsing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Deliberately TwitchLib-bound — same exception as SharedChatRuleTwitchLibTests. IrcLineSpliceRule
// (E6, docs/superpowers/plans/2026-09-08-worker-erfassungsfehler.md) depends on a fact about the
// installed TwitchLib.Client 4.0.1 that no pure test can see: when a splice lands inside the value
// of a *typed* tag (e.g. "subscriber=0@badge-info="), the library's tag parser scans for the next
// ';' or ' ' to end that tag's value — so the embedded "@badge-info=" never becomes its own
// dictionary entry and therefore never surfaces via ChatMessage.UndocumentedTags. Only the raw,
// unparsed line (ChatMessage.RawIrcMessage) still shows the second '@'. That is the library
// behaviour under test here, not our own code — hence a real IRC line runs through TwitchLib's own
// IrcParser and ChatMessage constructor instead of a hand-built fake. The line is synthetic
// (invented ids/logins/text), never recorded chat, same rule as everywhere else in this repo.
// The TwitchLib.* Dependabot freeze (see .github/dependabot.yml) exists so an update cannot flip
// this contract without this test going red first.
public class IrcLineSpliceRuleTwitchLibTests
{
    private const string RoomId = "111111111";

    [Fact]
    public void SplicedLineWithSecondAtInsideTypedTagValue_RawIrcMessageShowsIt_ButUndocumentedTagsDoesNot()
    {
        // The splice sits right after "subscriber=0" (first line's tag block ends mid-value) and
        // is immediately followed by the second line's tag block starting with "@badge-info=" —
        // exactly the shape reported for issue #114. Every key on both sides (badges, color,
        // display-name, id, mod, room-id, subscriber, tmi-sent-ts, turbo, user-id, user-type) is a
        // typed ChatMessage property, deliberately excluding badge-info as a real key — it only
        // ever appears embedded inside the corrupted subscriber value, never as its own tag.
        var line = "@badges=;color=#FF0000;display-name=TestUser1;id=cccc3333-3333-3333-3333-333333333333;"
            + "mod=0;room-id=" + RoomId + ";subscriber=0@badge-info=;badges=;color=#00FF00;"
            + "display-name=TestUser2;id=dddd4444-4444-4444-4444-444444444444;mod=0;room-id=" + RoomId + ";"
            + "subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=444444444;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello spliced";

        var message = ParseLine(line);

        // (a) The raw line is preserved verbatim (ChatMessage.RawIrcMessage = ircMessage.ToString(),
        // which returns the cached original raw string, not a re-serialization of the parsed tags)
        // and still carries the second '@', so the sentinel fires on it.
        Assert.Equal(line, message.RawIrcMessage);
        Assert.True(IrcLineSpliceRule.IsSpliced(message.RawIrcMessage));

        // (b) This is the proof for E6: the corruption never becomes a separate dictionary entry,
        // so UndocumentedTags — built only from unrecognised keys — does not show it at all.
        Assert.Null(message.UndocumentedTags);
    }

    private static ChatMessage ParseLine(string line)
    {
        var ircMessage = IrcParser.ParseMessage(line);
        return new ChatMessage("purgebot_test", ircMessage, new MessageEmoteCollection(), false, "prefix", "suffix");
    }
}
