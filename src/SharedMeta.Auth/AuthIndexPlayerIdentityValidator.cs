using System.Threading.Tasks;
using Orleans;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Auth
{
    /// <summary>
    /// <see cref="IPlayerIdentityValidator"/> backed by the auth index: a player exists as long as
    /// at least one auth key (device id, "platform:userId") is linked to it.
    /// <para>
    /// Ordering is what makes this safe for brand-new players: <see cref="AuthGrain.LoginAsync"/>
    /// writes the index entry before the endpoint mints the token, so by the time a client can
    /// present a PlayerId the index already answers true for it.
    /// </para>
    /// </summary>
    public class AuthIndexPlayerIdentityValidator : IPlayerIdentityValidator
    {
        private readonly IGrainFactory _grainFactory;

        public AuthIndexPlayerIdentityValidator(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public Task<bool> ExistsAsync(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return Task.FromResult(false);

            return _grainFactory.GetGrain<IAuthIndexGrain>(playerId).HasKeysAsync();
        }
    }
}
