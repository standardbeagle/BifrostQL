using System.Security.Cryptography;
using System.Text;
using BifrostQL.Core.Crypto;

namespace BifrostQL.Server.Auth
{
    /// <summary>How a presented second factor fared.</summary>
    internal enum LocalMfaVerdict
    {
        /// <summary>The code or recovery code was right and is now spent.</summary>
        Accepted,

        /// <summary>Wrong code, wrong recovery code, or malformed input.</summary>
        Invalid,

        /// <summary>A right TOTP code for a time step already accepted.</summary>
        Replayed,
    }

    /// <summary>The outcome of <see cref="LocalMfaService.VerifyAsync"/>: the verdict, the method that passed, and the row after it.</summary>
    internal sealed record LocalMfaVerification(LocalMfaVerdict Verdict, string? Method, LocalMfaState? State)
    {
        public static LocalMfaVerification Rejected(LocalMfaVerdict verdict) => new(verdict, null, null);
    }

    /// <summary>A freshly issued, unconfirmed TOTP secret, for the enrolling user's eyes only.</summary>
    internal sealed record LocalMfaEnrollmentOffer(string Secret, string OtpAuthUri, DateTimeOffset ExpiresAt, LocalMfaState State);

    /// <summary>
    /// The TOTP second factor of local auth: issues and seals secrets, verifies codes and recovery
    /// codes, and issues and spends login challenges. Owns no HTTP; <see cref="LocalMfaEndpoint"/>
    /// calls it and does the auditing and session work.
    ///
    /// <para><b>Secrets</b> are 32 CSPRNG bytes, sealed with <see cref="FieldCipher"/> under the data
    /// key of <see cref="LocalAuthOptions.MfaKeyRef"/> from the host's <see cref="EnvelopeKeyManager"/>
    /// (the field-encryption seam, no second cipher). The AAD binds the envelope to the user id, so a
    /// sealed secret copied onto another user's row does not open.</para>
    ///
    /// <para><b>Replay.</b> A TOTP code is accepted only for a time step above
    /// <see cref="LocalMfaState.LastUsedStep"/>, and the new step is written by compare-and-swap on the
    /// row version in the same write as any other change, so two requests with one code cannot both
    /// pass (PCI DSS 8.5.1). A spent recovery code leaves the row in that same write.</para>
    ///
    /// <para><b>Recovery codes</b> are 80 random bits each, shown once, stored as
    /// <c>salt.SHA-256(salt || code)</c>. A slow password hash buys nothing against 80 bits of entropy
    /// (an offline search is 2^80 hashes either way) and would cost ten PBKDF2 runs per attempt, since
    /// every unspent code is compared; the salt keeps equal codes from equal hashes. Session ids are
    /// stored the same way for the same reason.</para>
    /// </summary>
    internal sealed class LocalMfaService
    {
        public const int SecretBytes = 32;
        private const int RecoveryCodeBytes = 10; // 80 bits = 16 base32 characters
        private const int RecoverySaltBytes = 16;
        private const int MaxOptimisticAttempts = 16;

        private readonly ILocalAuthStateStore _state;
        private readonly LocalAuthOptions _options;
        private readonly Func<EnvelopeKeyManager?> _keys;

        public LocalMfaService(ILocalAuthStateStore state, LocalAuthOptions options, Func<EnvelopeKeyManager?> keys)
        {
            _state = state;
            _options = options;
            _keys = keys;
        }

        private string KeyRef => _options.MfaKeyRef
            ?? throw new InvalidOperationException("MFA is not configured: LocalAuthOptions.MfaKeyRef is null.");

        private EnvelopeKeyManager Keys => _keys() ?? throw new InvalidOperationException(
            "LocalAuthOptions.MfaKeyRef is set but no field-encryption key manager is available: register an " +
            "IRootKeyProvider and a durable IDataEncryptionKeyStore.");

        /// <summary>
        /// Fails the host start when MFA is configured and its key cannot be resolved, instead of the
        /// first enrollment. A no-op when <see cref="LocalAuthOptions.MfaKeyRef"/> is null.
        /// </summary>
        public async Task EnsureKeyAsync(CancellationToken ct)
        {
            if (!_options.MfaEnabled)
                return;
            await Keys.GetCurrentVersionAsync(KeyRef, ct).ConfigureAwait(false);
        }

        private static byte[] Aad(string userId) =>
            CryptoAad.Build(DbLocalAuthStateStore.MfaTable, "secret_envelope", userId);

        private async Task<string> SealAsync(string userId, byte[] secret, CancellationToken ct)
        {
            var version = await Keys.GetCurrentVersionAsync(KeyRef, ct).ConfigureAwait(false);
            var dek = await Keys.GetDataKeyAsync(KeyRef, version, ct).ConfigureAwait(false);
            return FieldCipher.Encrypt(dek, Base32.Encode(secret), Aad(userId), version);
        }

        private async Task<byte[]> OpenAsync(LocalMfaState state, CancellationToken ct)
        {
            var version = FieldCipher.PeekKeyVersion(state.SecretEnvelope) ?? 1;
            var dek = await Keys.GetDataKeyAsync(KeyRef, version, ct).ConfigureAwait(false);
            return Base32.Decode(FieldCipher.Decrypt(dek, state.SecretEnvelope, Aad(state.UserId)));
        }

        /// <summary>
        /// Issues a fresh secret as a pending enrollment, replacing any earlier pending one. Null when
        /// the user already has an active second factor (it must be disabled first).
        /// </summary>
        public async Task<LocalMfaEnrollmentOffer?> StartEnrollmentAsync(
            string userId, string accountLabel, DateTimeOffset now, CancellationToken ct)
        {
            var secret = RandomNumberGenerator.GetBytes(SecretBytes);
            var sealedSecret = await SealAsync(userId, secret, ct).ConfigureAwait(false);
            var expiresAt = now + _options.MfaEnrollmentTimeout;
            for (var attempt = 0; attempt < MaxOptimisticAttempts; attempt++)
            {
                var current = await _state.GetMfaAsync(userId, ct).ConfigureAwait(false);
                if (current is { Status: LocalMfaStatus.Active })
                    return null;
                var next = new LocalMfaState(userId, sealedSecret, _options.MfaTotpAlgorithm, _options.MfaTotpDigits,
                    _options.MfaTotpPeriodSeconds, LocalMfaStatus.Pending, now, expiresAt, EnabledAt: null,
                    LastUsedStep: null, Array.Empty<string>(), Version: 1);
                var written = current is null
                    ? await _state.InsertMfaAsync(next, ct).ConfigureAwait(false)
                    : await _state.UpdateMfaAsync(next, current.Version, ct).ConfigureAwait(false);
                if (written)
                {
                    var encoded = Base32.Encode(secret);
                    return new LocalMfaEnrollmentOffer(encoded, OtpAuthUri(accountLabel, encoded), expiresAt, next);
                }
            }
            throw new InvalidOperationException(
                $"The enrollment could not be written after {MaxOptimisticAttempts} concurrent attempts.");
        }

        /// <summary>The <c>otpauth://totp/</c> key URI authenticator apps import (by QR code or by hand).</summary>
        internal string OtpAuthUri(string accountLabel, string base32Secret)
        {
            var issuer = Uri.EscapeDataString(_options.MfaIssuer);
            var account = Uri.EscapeDataString(accountLabel);
            var algorithm = _options.MfaTotpAlgorithm switch
            {
                LocalTotpAlgorithm.Sha256 => "SHA256",
                LocalTotpAlgorithm.Sha512 => "SHA512",
                _ => "SHA1",
            };
            return $"otpauth://totp/{issuer}:{account}?secret={base32Secret}&issuer={issuer}&algorithm={algorithm}" +
                   $"&digits={_options.MfaTotpDigits}&period={_options.MfaTotpPeriodSeconds}";
        }

        /// <summary>
        /// Checks one presented factor against the user's row in <paramref name="expectedStatus"/> and,
        /// when it passes, writes the spent step or recovery code together with
        /// <paramref name="onAccepted"/>'s change in one compare-and-swap. A lost swap re-reads and
        /// re-checks, so the loser of a race on one code sees a replay. Exactly one of
        /// <paramref name="code"/> and <paramref name="recoveryCode"/> is non-null; recovery codes are
        /// refused for a pending enrollment, which has none.
        /// </summary>
        public async Task<LocalMfaVerification> VerifyAsync(
            string userId, LocalMfaStatus expectedStatus, string? code, string? recoveryCode, DateTimeOffset now,
            Func<LocalMfaState, LocalMfaState>? onAccepted, CancellationToken ct)
        {
            for (var attempt = 0; attempt < MaxOptimisticAttempts; attempt++)
            {
                var state = await _state.GetMfaAsync(userId, ct).ConfigureAwait(false);
                if (state is null || state.Status != expectedStatus)
                    return LocalMfaVerification.Rejected(LocalMfaVerdict.Invalid);

                LocalMfaState spent;
                string method;
                if (code is not null)
                {
                    var key = await OpenAsync(state, ct).ConfigureAwait(false);
                    var step = Totp.Match(key, code, Totp.TimeStep(now, state.PeriodSeconds), state.Algorithm, state.Digits);
                    CryptographicOperations.ZeroMemory(key);
                    if (step is null)
                        return LocalMfaVerification.Rejected(LocalMfaVerdict.Invalid);
                    if (state.LastUsedStep is { } last && step <= last)
                        return LocalMfaVerification.Rejected(LocalMfaVerdict.Replayed);
                    spent = state with { LastUsedStep = step };
                    method = LocalAuthMethods.Otp;
                }
                else if (recoveryCode is not null && expectedStatus == LocalMfaStatus.Active)
                {
                    var index = MatchRecoveryCode(state.RecoveryCodeHashes, recoveryCode);
                    if (index < 0)
                        return LocalMfaVerification.Rejected(LocalMfaVerdict.Invalid);
                    spent = state with { RecoveryCodeHashes = state.RecoveryCodeHashes.Where((_, i) => i != index).ToArray() };
                    method = LocalAuthMethods.RecoveryCode;
                }
                else
                {
                    return LocalMfaVerification.Rejected(LocalMfaVerdict.Invalid);
                }

                var next = onAccepted is null ? spent : onAccepted(spent);
                if (await _state.UpdateMfaAsync(next, state.Version, ct).ConfigureAwait(false))
                    return new LocalMfaVerification(LocalMfaVerdict.Accepted, method, next with { Version = state.Version + 1 });
            }
            throw new InvalidOperationException(
                $"The second factor could not be recorded after {MaxOptimisticAttempts} concurrent attempts.");
        }

        /// <summary>
        /// Ten new recovery codes: the display form (<c>XXXX-XXXX-XXXX-XXXX</c>) for the user, and the
        /// salted hashes for the row.
        /// </summary>
        public static (IReadOnlyList<string> Codes, IReadOnlyList<string> Hashes) NewRecoveryCodes()
        {
            var codes = new string[LocalAuthOptions.RecoveryCodeCount];
            var hashes = new string[LocalAuthOptions.RecoveryCodeCount];
            for (var i = 0; i < codes.Length; i++)
            {
                var raw = Base32.Encode(RandomNumberGenerator.GetBytes(RecoveryCodeBytes));
                codes[i] = string.Join('-', Enumerable.Range(0, 4).Select(g => raw.Substring(g * 4, 4)));
                var salt = RandomNumberGenerator.GetBytes(RecoverySaltBytes);
                hashes[i] = Convert.ToHexString(salt).ToLowerInvariant() + "." + HashRecoveryCode(salt, raw);
            }
            return (codes, hashes);
        }

        private static string HashRecoveryCode(byte[] salt, string normalizedCode)
        {
            var input = new byte[salt.Length + normalizedCode.Length];
            salt.CopyTo(input, 0);
            Encoding.ASCII.GetBytes(normalizedCode, input.AsSpan(salt.Length));
            return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        }

        /// <summary>
        /// The index of the stored hash <paramref name="presented"/> matches, or -1. Dashes, spaces and
        /// case are ignored. Every stored hash is computed and compared in constant time.
        /// </summary>
        internal static int MatchRecoveryCode(IReadOnlyList<string> storedHashes, string presented)
        {
            var normalized = new string(presented.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
            var wellFormed = normalized.Length == RecoveryCodeBytes * 8 / 5 && normalized.All(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7');
            var probe = wellFormed ? normalized : new string('A', RecoveryCodeBytes * 8 / 5);
            var found = -1;
            for (var i = 0; i < storedHashes.Count; i++)
            {
                var parts = storedHashes[i].Split('.');
                var expected = Encoding.ASCII.GetBytes(parts[1]);
                var actual = Encoding.ASCII.GetBytes(HashRecoveryCode(Convert.FromHexString(parts[0]), probe));
                if (CryptographicOperations.FixedTimeEquals(expected, actual) & wellFormed)
                    found = i;
            }
            return found;
        }

        /// <summary>
        /// Issues a login challenge for an account whose password just verified; returns the ticket the
        /// client presents with its code. The store keeps only the ticket's SHA-256. Spent and
        /// expired tickets are purged with expired sessions (<see cref="LocalSessionManager"/>).
        /// </summary>
        public async Task<(string Ticket, DateTimeOffset ExpiresAt)> IssueChallengeAsync(
            LocalAccountState account, DateTimeOffset now, CancellationToken ct)
        {
            var ticket = LocalSessionId.New();
            var expiresAt = now + _options.MfaChallengeLifetime;
            await _state.CreateMfaChallengeAsync(new LocalMfaChallenge(
                LocalSessionId.Hash(ticket), account.UserId, account.CredentialStamp, now, expiresAt,
                FailedAttempts: 0, ConsumedAt: null, Version: 1), ct).ConfigureAwait(false);
            return (ticket, expiresAt);
        }

        /// <summary>Whether a challenge can still be answered at <paramref name="now"/>.</summary>
        public bool IsUsable(LocalMfaChallenge challenge, DateTimeOffset now) =>
            challenge.ConsumedAt is null && challenge.ExpiresAt > now && challenge.FailedAttempts < _options.MaxMfaChallengeAttempts;

        /// <summary>
        /// Spends a challenge by compare-and-swap. False when another request spent it first, so one
        /// ticket yields at most one session.
        /// </summary>
        public async Task<bool> SpendChallengeAsync(LocalMfaChallenge challenge, DateTimeOffset now, CancellationToken ct) =>
            await _state.UpdateMfaChallengeAsync(challenge with { ConsumedAt = now }, challenge.Version, ct).ConfigureAwait(false);

        /// <summary>
        /// Counts a wrong code against a challenge; the attempt that reaches
        /// <see cref="LocalAuthOptions.MaxMfaChallengeAttempts"/> spends it.
        /// </summary>
        public async Task RecordChallengeFailureAsync(string challengeHash, DateTimeOffset now, CancellationToken ct)
        {
            for (var attempt = 0; attempt < MaxOptimisticAttempts; attempt++)
            {
                var current = await _state.GetMfaChallengeAsync(challengeHash, ct).ConfigureAwait(false);
                if (current is null || current.ConsumedAt is not null)
                    return;
                var failed = current.FailedAttempts + 1;
                var next = current with
                {
                    FailedAttempts = failed,
                    ConsumedAt = failed >= _options.MaxMfaChallengeAttempts ? now : null,
                };
                if (await _state.UpdateMfaChallengeAsync(next, current.Version, ct).ConfigureAwait(false))
                    return;
            }
            throw new InvalidOperationException(
                $"The challenge failure could not be recorded after {MaxOptimisticAttempts} concurrent attempts.");
        }
    }
}
