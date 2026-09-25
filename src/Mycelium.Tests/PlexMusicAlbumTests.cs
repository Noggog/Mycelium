using FluentAssertions;
using Mycelium.Plex.Services.Singletons;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mycelium.Tests;

public class PlexMusicAlbumTests
{
    [Fact]
    public void The_owning_Plex_artist_binds_from_the_string_Plex_sends()
    {
        // Plex serialises rating keys as strings; the album listing's parentRatingKey is what tells two
        // same-named Plex artists' albums apart.
        var album = JObject.Parse(
                """{"ratingKey":"5001","title":"Lesser Evil","parentTitle":"Doldrums","parentRatingKey":"101"}""")
            .ToObject<PlexMusicAlbum>();

        album!.ParentRatingKey.Should().Be(101);
    }
}
