using Microsoft.AspNetCore.DataProtection;
using AlphaStack.Application.Common.Interfaces;

namespace AlphaStack.Infrastructure.Security;

/// <summary>
/// Uses ASP.NET Core Data Protection to encrypt/decrypt sensitive values
/// like Kite API keys and Telegram bot tokens at rest.
/// Keys are stored on disk (configurable via DataProtection:KeyRingPath).
/// </summary>
public class DataProtectionEncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;

    public DataProtectionEncryptionService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("AlphaStack.Secrets.v1");
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
