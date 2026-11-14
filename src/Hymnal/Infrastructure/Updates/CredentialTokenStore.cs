using System;
using System.Linq;
using System.Threading.Tasks;
using Hymnal.Core.Interfaces;
using MattEland.HopeLab.Common;
using MattEland.HopeLab.Updates;

namespace Hymnal.Infrastructure.Updates;

/// <summary>
/// Keeps the GitHub token for updates in Hymnal's <see cref="ICredentialStore"/>, like the AI providers' API keys,
/// instead of the shared library's default plain file (<c>~/.hymnal/github-token</c>). Saving, lookup and removal all
/// come through here (<c>AppUpdateOptions.TokenStore</c>), so the token is never written to disk in plain text.
/// </summary>
public sealed class CredentialTokenStore : ITokenStore
{
    /// <summary>The credential key, alongside <c>hymnal.provider.{id}.apikey</c>.</summary>
    public const string CredentialKey = "hymnal.updates.github-token";

    private readonly ICredentialStore _credentials;

    public CredentialTokenStore(ICredentialStore credentials) => _credentials = credentials;

    public string Description => "Hymnal's credential store";

    /// <summary>
    /// The shared updater reads the token synchronously, off the UI thread. The store is async, so it is awaited on the
    /// thread pool: no caller's synchronization context is captured, so this can't deadlock.
    /// </summary>
    public string? Read()
    {
        try
        {
            var token = Task.Run(() => _credentials.RetrieveAsync(CredentialKey)).GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<Result<Unit>> SaveAsync(string token)
    {
        var t = token.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace)) // as the shared FileTokenStore checks
            return Result<Unit>.Fail("That doesn't look like a GitHub token.");
        try
        {
            await _credentials.StoreAsync(CredentialKey, t).ConfigureAwait(false);
            return Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Couldn't save the token in {Description}: {ex.Message}");
        }
    }

    public Result<Unit> Clear()
    {
        try
        {
            Task.Run(() => _credentials.DeleteAsync(CredentialKey)).GetAwaiter().GetResult();
            return Result<Unit>.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            return Result<Unit>.Fail($"Couldn't remove the token from {Description}: {ex.Message}");
        }
    }
}
