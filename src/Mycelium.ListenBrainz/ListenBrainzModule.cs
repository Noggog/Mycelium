using Autofac;
using Mycelium.ListenBrainz.Inputs;
using Mycelium.ListenBrainz.Services;
using Noggog.Autofac;

namespace Mycelium.ListenBrainz;

public class ListenBrainzModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // All keyless; the only settings are the two base URIs, the contact string baked into the
        // (mandatory) User-Agent, the labs algorithm tuning, and an off-switch. Env-overridable so
        // nothing is hardcoded that another environment can't redirect or disable.
        var contact = Environment.GetEnvironmentVariable("LISTENBRAINZ_CONTACT")
                      ?? Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")
                      ?? "https://github.com/Noggog/Mycelium";

        var enabled = Environment.GetEnvironmentVariable("LISTENBRAINZ_ENABLED") is var e
                      && !(e == "0" || string.Equals(e, "false", StringComparison.OrdinalIgnoreCase));

        builder.RegisterInstance(
                new ListenBrainzEndpointInfo(
                    MusicBrainzBaseUri: Environment.GetEnvironmentVariable("MUSICBRAINZ_BASE_URI")
                                        ?? "https://musicbrainz.org",
                    ListenBrainzBaseUri: Environment.GetEnvironmentVariable("LISTENBRAINZ_BASE_URI")
                                         ?? "https://labs.api.listenbrainz.org",
                    Contact: contact,
                    Algorithm: Environment.GetEnvironmentVariable("LISTENBRAINZ_ALGORITHM")
                               ?? "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30",
                    Enabled: enabled,
                    MusicBrainzMinIntervalOverride:
                        int.TryParse(Environment.GetEnvironmentVariable("MUSICBRAINZ_MIN_INTERVAL_MS"), out var ms) && ms >= 0
                            ? TimeSpan.FromMilliseconds(ms)
                            : null))
            .AsSelf().SingleInstance();

        // Registers MusicBrainzApi (IMusicBrainzApi) and ListenBrainzApi (IListenBrainzApi),
        // following the same assembly-scan convention as DeezerModule.
        builder.RegisterAssemblyTypes(typeof(ListenBrainzApi).Assembly)
            .InNamespacesOf(typeof(ListenBrainzApi))
            .AsImplementedInterfaces()
            .AsSelf()
            .SingleInstance();
    }
}
