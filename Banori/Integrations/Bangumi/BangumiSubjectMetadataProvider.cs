using Banned.Bangumi;
using Banori.Features.Anime;

namespace Banori.Integrations.Bangumi;

public sealed class BangumiSubjectMetadataProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration     configuration,
    TimeProvider       timeProvider) : ISubjectMetadataProvider
{
    public async Task<SubjectMetadata> GetAsync(int subjectId, CancellationToken cancellationToken = default)
    {
        var userAgent = configuration["Bangumi:UserAgent"];
        if (string.IsNullOrWhiteSpace(userAgent))
            userAgent = "Banori/0.1.1 (https://github.com/banned2054/Banori)";

        using var httpClient = httpClientFactory.CreateClient("Bangumi");
        using var client = new BangumiClient(new BangumiClientOptions
        {
            UserAgent  = userAgent,
            HttpClient = httpClient,
            Timeout    = TimeSpan.FromSeconds(20)
        });
        var subject = await client.Subjects.Get(subjectId, cancellationToken);
        return new SubjectMetadata(subject.Id, subject.Name, subject.NameCn, subject.Platform, subject.Date,
                                   subject.Images.Common ?? subject.Images.Large, timeProvider.GetUtcNow());
    }
}
