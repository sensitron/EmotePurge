using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="SevenTvApiClient.GetEmoteSetPreviewAsync"/> — the v4 preview query the
/// foreign-channel-import spec (F1 step 3, F3) added. Three things this file exists to prove:
/// pagination actually walks every page, the <c>MaxSetEntryPages</c> guard reports itself as
/// <c>truncated</c> instead of quietly cutting the list short, and a confirmed 7TV rate limit
/// (<c>extensions.status: 429</c>, disguised as HTTP 200) is never mistaken for the legitimate empty
/// set the state table treats as success — AK 7, "der teuerste Einzelfehler der ganzen Spec".
/// </summary>
public class SevenTvApiClientEmoteSetPreviewTests
{
    private const string SetId = "01FRY81K4800085N93FNKSBYXS";

    [Fact]
    public async Task MultiplePages_AreAllWalked_AndConcatenated()
    {
        // Two pages, three items total — pageCount 2 tells the client there is a second page even
        // though the first only carries one item, and totalCount matches the concatenated result
        // exactly, so nothing here should read as truncated.
        var handler = new PagedStubHandler(page => page switch
        {
            1 => Page(totalCount: 3, pageCount: 2, ("e1", "AliasOne", "DefaultOne", 10, 1)),
            2 => Page(totalCount: 3, pageCount: 2, ("e2", "AliasTwo", "DefaultTwo", 20, 2), ("e3", "AliasThree", "DefaultThree", 30, 3)),
            _ => throw new InvalidOperationException($"unexpected page {page}")
        });
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Equal([1, 2], handler.RequestedPages);
        Assert.Equal(3, result.Preview!.TotalCount);
        Assert.False(result.Preview.Truncated);
        Assert.Equal(["e1", "e2", "e3"], result.Preview.Items.Select(i => i.SevenTvEmoteId));
    }

    [Fact]
    public async Task PageCountAtOrBelowOne_StopsAfterTheFirstPage_NoOverfetch()
    {
        var handler = new PagedStubHandler(page => page == 1
            ? Page(totalCount: 1, pageCount: 1, ("e1", "Alias", "Default", null, null))
            : throw new InvalidOperationException($"unexpected page {page}"));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Single(handler.RequestedPages);
        Assert.False(result.Preview!.Truncated);
    }

    /// <summary>
    /// F3: the runaway guard (<c>MaxSetEntryPages</c> = 10 in <see cref="SevenTvApiClient"/>) must
    /// not cut the list short in silence. Every one of the ten allowed pages reports eleven pages
    /// total, so the loop stops at the cap while 7TV still says there is more — that has to come back
    /// as <c>Truncated == true</c> with the real <c>TotalCount</c>, not a quietly short list.
    /// </summary>
    [Fact]
    public async Task ReachingThePageCap_ReportsTruncated_WithTheRealTotalCount()
    {
        var handler = new PagedStubHandler(page => Page(totalCount: 5000, pageCount: 11, ($"e{page}", $"Alias{page}", $"Default{page}", null, null)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        // MaxSetEntryPages: exactly ten requests, never an eleventh even though page_count said 11.
        Assert.Equal(10, handler.RequestedPages.Count);
        Assert.Equal(Enumerable.Range(1, 10), handler.RequestedPages);
        Assert.True(result.Preview!.Truncated);
        Assert.Equal(5000, result.Preview.TotalCount);
        Assert.Equal(10, result.Preview.Items.Count);
    }

    /// <summary>
    /// AK 7, the central distinction of the whole spec: HTTP 200 carrying a GraphQL error with
    /// <c>extensions.status: 429</c> must map to <see cref="SevenTvPreviewLookupStatus.RateLimited"/>,
    /// never to the legitimate empty-set success the very next test proves is a real, distinct case.
    /// </summary>
    [Fact]
    public async Task RateLimitExtensionsStatus_IsReportedAsRateLimited_NotAsAnEmptySet()
    {
        const string rateLimitedPayload =
            """{"data":null,"errors":[{"message":"too many requests","extensions":{"code":"RATE_LIMITED","status":429}}]}""";
        var handler = new PagedStubHandler(_ => rateLimitedPayload);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.RateLimited, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>The other half of the AK 7 distinction: a genuinely empty active set is success, not
    /// an error — the exact case a 429 must never be confused with.</summary>
    [Fact]
    public async Task GenuinelyEmptySet_IsOk_WithZeroItems_NotRateLimited()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 0, pageCount: 1));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Ok, result.Status);
        Assert.Empty(result.Preview!.Items);
        Assert.False(result.Preview.Truncated);
    }

    /// <summary>
    /// A GraphQL error with no rate-limit status (a genuine upstream failure) must still map to
    /// <c>Unavailable</c>, distinct from both <c>RateLimited</c> above and the empty-set success.
    /// </summary>
    [Fact]
    public async Task GraphQlErrorWithoutRateLimitStatus_IsUnavailable()
    {
        const string errorPayload =
            """{"data":null,"errors":[{"message":"LOAD_ERROR set not found","extensions":{"code":"LOAD_ERROR","status":404}}]}""";
        var handler = new PagedStubHandler(_ => errorPayload);
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        Assert.Equal(SevenTvPreviewLookupStatus.Unavailable, result.Status);
        Assert.Null(result.Preview);
    }

    /// <summary>
    /// The set-local alias and the emote's global default name are two different fields with two
    /// different meanings (spec DTO contract, section 4) and must never collapse into one — a
    /// regression here would silently make every renamed-in-this-set emote look like it kept its
    /// upstream name.
    /// </summary>
    [Fact]
    public async Task AliasAndDefaultName_AreKeptDistinct_WhenTheyDiffer()
    {
        var handler = new PagedStubHandler(_ => Page(totalCount: 1, pageCount: 1, ("e1", "renamedInThisSet", "OriginalUploadName", 42, 7)));
        var client = CreateClient(handler);

        var result = await client.GetEmoteSetPreviewAsync(SetId);

        var item = Assert.Single(result.Preview!.Items);
        Assert.Equal("renamedInThisSet", item.Alias);
        Assert.Equal("OriginalUploadName", item.DefaultName);
        Assert.Equal(42, item.TopAllTime);
        Assert.Equal(7, item.Trending);
        // Built from the emote id, never fetched via the (far more expensive) Emote.images list —
        // see BuildForeignImageUrl.
        Assert.Equal("https://cdn.7tv.app/emote/e1/4x_static.webp", item.ImageUrl);
    }

    // Built through JsonNode rather than a hand-assembled string: the response nests five levels
    // deep (data.emote_sets.emote_set.emotes.items[].emote.scores), and getting the brace-counting
    // right in a raw string literal for that shape is exactly the kind of thing worth not doing by
    // hand.
    private static string Page(int totalCount, int pageCount, params (string Id, string Alias, string DefaultName, int? TopAllTime, int? TrendingDay)[] items)
    {
        var itemsArray = new JsonArray();
        foreach (var item in items)
        {
            itemsArray.Add(new JsonObject
            {
                ["alias"] = item.Alias,
                ["emote"] = new JsonObject
                {
                    ["id"] = item.Id,
                    ["default_name"] = item.DefaultName,
                    ["scores"] = new JsonObject
                    {
                        ["top_all_time"] = item.TopAllTime ?? 0,
                        ["trending_day"] = item.TrendingDay ?? 0,
                    },
                },
            });
        }

        var root = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["emote_sets"] = new JsonObject
                {
                    ["emote_set"] = new JsonObject
                    {
                        ["emotes"] = new JsonObject
                        {
                            ["total_count"] = totalCount,
                            ["page_count"] = pageCount,
                            ["items"] = itemsArray,
                        },
                    },
                },
            },
        };

        return root.ToJsonString();
    }

    private static SevenTvApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") };
        return new SevenTvApiClient(httpClient, new RecordingLogger<SevenTvApiClient>());
    }

    /// <summary>Answers every POST with the response the given function derives from the request's
    /// GraphQL <c>page</c> variable, and records the pages actually requested.</summary>
    private sealed class PagedStubHandler(Func<int, string> responseForPage) : HttpMessageHandler
    {
        public List<int> RequestedPages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var page = doc.RootElement.GetProperty("variables").GetProperty("page").GetInt32();
            RequestedPages.Add(page);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseForPage(page), Encoding.UTF8, "application/json"),
            };
        }
    }
}
