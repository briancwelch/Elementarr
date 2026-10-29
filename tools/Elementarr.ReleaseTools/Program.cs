using System.Security.Cryptography;
using Elementarr.UpdateCore;

return ReleaseTools.Run(args);

internal static class ReleaseTools
{
    /// <summary>Generates an ignored publisher key or signs release metadata using a key matching the embedded update trust root.</summary>
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "keygen")
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
                using (var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write)) file.Write(key.ExportPkcs8PrivateKey());
                Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
                return 0;
            }
            if (args.Length == 4 && args[0] == "sign")
            {
                using var key = ECDsa.Create();
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(args[1]), out _);
                if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != ReleaseTrust.PublicKey)
                    throw new InvalidOperationException("The signing key does not match the published update trust key.");
                var bytes = File.ReadAllBytes(args[2]);
                var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
                _ = ReleaseTrust.Verify(bytes, signature);
                File.WriteAllText(args[3], Convert.ToBase64String(signature));
                Console.WriteLine("Release metadata signed and verified.");
                return 0;
            }
            throw new ArgumentException("Use: keygen PRIVATE_KEY_FILE | sign PRIVATE_KEY_FILE MANIFEST_FILE SIGNATURE_FILE");
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or ArgumentException or InvalidOperationException)
        { Console.Error.WriteLine(exception.Message); return 1; }
    }
}
