using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.App.Security;

/// <summary>
/// Verifies the startup login password without ever storing it in plain text.
/// Only a PBKDF2 digest of the expected password is kept in source; the salt
/// is fixed (not secret — it only separates this digest from any other use of
/// the same password elsewhere).
/// </summary>
public static class LoginPasswordHasher
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("AgentBridge.Login.Salt.v1");
    private const int Iterations = 100_000;
    private const int HashLengthBytes = 32;

    private static readonly byte[] ExpectedHash = Convert.FromHexString(
        "20b34cacb171d8985b3476a59d98240b1978aa43e376811d8233be5757644175");

    public static bool Verify(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        var candidate = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Salt, Iterations, HashAlgorithmName.SHA256, HashLengthBytes);
        return CryptographicOperations.FixedTimeEquals(candidate, ExpectedHash);
    }
}
